using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using Vcns.VirtualCom.Bridge.Configuration;
using Vcns.VirtualCom.Bridge.Diagnostics;
using Vcns.VirtualCom.Bridge.Transport;

namespace Vcns.VirtualCom.Bridge.Server;

internal enum PortServerState
{
    Starting,
    Listening,
    Connected,
    NotListening,
    Stopped,
}

/// <summary>
/// Shares one COM port on one TCP port the way a single-session serial device server does: raw TCP with no
/// handshake, one client at a time, and any other client accepted and closed straight away, leaving the current
/// session alone. The COM port is open only while a client is connected, so other software can use it otherwise.
/// </summary>
internal sealed class PortServer : IAsyncDisposable
{
    private const int BufferSize = 16 * 1024;
    private static readonly TimeSpan RetryListenAfter = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SessionShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<ServerPort, Stream> _openSerial;
    private readonly ClientFilter _filter;
    private readonly Action _changed;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Task _run = Task.CompletedTask;
    private Task _session = Task.CompletedTask;
    private bool _sessionActive;
    private Socket? _client;
    private PortServerState _state = PortServerState.Starting;
    private string? _problem;
    private string? _lastEvent;
    private IPEndPoint? _clientEndpoint;
    private DateTimeOffset? _clientSince;
    private long _bytesToDevice;
    private long _bytesFromDevice;
    private int _boundPort;

    public PortServer(ServerPort port, Func<ServerPort, Stream> openSerial, bool logPayload, Action changed)
    {
        Port = port;
        LogPayload = logPayload;
        _openSerial = openSerial;
        _filter = new ClientFilter(port.AllowedClients);
        _changed = changed;
    }

    public ServerPort Port { get; }

    public bool LogPayload { get; }

    /// <summary>The TCP port actually listened on: <see cref="ServerPort.TcpPort"/>, or the one Windows chose for 0.</summary>
    public int LocalPort => Volatile.Read(ref _boundPort);

    public PortServerState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    private string Name => $"{Port.Title} on {Port.Endpoint}";

    public void Start() => _run = Task.Run(RunAsync);

    public PortStatus Snapshot()
    {
        lock (_gate)
        {
            return new PortStatus
            {
                ComPort = Port.ComPort,
                Label = Port.Label,
                TcpPort = Port.TcpPort,
                ListenAddress = Port.ListenAddress,
                State = _state.ToString(),
                Client = _clientEndpoint?.ToString(),
                ClientSince = _clientSince,
                BytesToDevice = Interlocked.Read(ref _bytesToDevice),
                BytesFromDevice = Interlocked.Read(ref _bytesFromDevice),
                Problem = _problem,
                LastEvent = _lastEvent,
            };
        }
    }

    private async Task RunAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            var listener = TryListen();
            if (listener is null)
            {
                try
                {
                    // The address may not exist yet (network still starting) or the port may be busy: keep trying.
                    await Task.Delay(RetryListenAfter, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            try
            {
                await AcceptLoopAsync(listener, ct);
            }
            finally
            {
                listener.Stop();
            }
        }
        SetState(PortServerState.Stopped, problem: null);
    }

    private TcpListener? TryListen()
    {
        var address = IPAddress.Parse(Port.ListenAddress);
        var listener = new TcpListener(address, Port.TcpPort) { ExclusiveAddressUse = true };
        try
        {
            if (address.Equals(IPAddress.IPv6Any))
                listener.Server.DualMode = true;
            listener.Start(backlog: 4);
        }
        catch (SocketException ex)
        {
            listener.Stop();
            var problem = ex.SocketErrorCode switch
            {
                SocketError.AddressAlreadyInUse => $"TCP port {Port.TcpPort} is already in use on {Port.ListenAddress} by another program.",
                SocketError.AddressNotAvailable => $"{Port.ListenAddress} isn't one of this PC's addresses at the moment.",
                SocketError.AccessDenied => $"Windows won't let the bridge listen on TCP port {Port.TcpPort}: it may be reserved (netsh int ipv4 show excludedportrange protocol=tcp).",
                _ => $"Can't listen on {Port.Endpoint}: {ex.Message}",
            };
            if (problem != Snapshot().Problem)
                Log.Warn($"{Name}: {problem} Retrying every {RetryListenAfter.TotalSeconds:0} s.");
            SetState(PortServerState.NotListening, problem);
            return null;
        }

        Volatile.Write(ref _boundPort, ((IPEndPoint)listener.LocalEndpoint).Port);
        Log.Info($"{Port.ComPort}: listening on {listener.LocalEndpoint} ({Port.Serial.BaudRate} baud, {Port.Serial.Format}, "
            + $"flow control {Port.Serial.FlowControl}; clients {string.Join(", ", Port.AllowedClients)}).");
        SetState(PortServerState.Listening, problem: null);
        return listener;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (true)
        {
            Socket client;
            try
            {
                client = await listener.AcceptSocketAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                if (!ct.IsCancellationRequested)
                    Log.Warn($"{Name}: accepting connections failed ({ex.Message}); listening again.");
                return;
            }
            OnClient(client, ct);
        }
    }

    private void OnClient(Socket client, CancellationToken ct)
    {
        var remote = (IPEndPoint)client.RemoteEndPoint!;
        if (!_filter.Allows(remote.Address))
        {
            Log.Warn($"{Name}: closed a connection from {remote}, which isn't an allowed client.");
            Note($"Refused {remote.Address} (not an allowed client)");
            client.Dispose();
            return;
        }

        IPEndPoint? holder;
        lock (_gate)
        {
            holder = _sessionActive ? _clientEndpoint : null;
            if (!_sessionActive)
            {
                _sessionActive = true;
                _client = client;
                _clientEndpoint = remote;
            }
        }
        if (holder is not null)
        {
            // What a single-session device does: accept, then close at once. The client sees "in use".
            Log.Info($"{Name}: turned away {remote}; {holder} has the session.");
            Note($"Turned away {remote.Address} ({holder.Address} had the session)");
            client.Dispose();
            return;
        }
        _session = Task.Run(() => RunSessionAsync(client, remote, ct));
    }

    private async Task RunSessionAsync(Socket client, IPEndPoint remote, CancellationToken ct)
    {
        Stream? serial = null;
        var reason = "the session ended";
        try
        {
            client.NoDelay = true;
            SocketTuning.ApplyKeepAlive(client, Name);
            try
            {
                serial = _openSerial(Port);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                Log.Warn($"{Name}: {remote} connected, but the port couldn't be opened: {ex.Message} Closed the connection.");
                SetProblem(ex.Message);
                reason = "the COM port couldn't be opened";
                return;
            }

            lock (_gate)
            {
                _state = PortServerState.Connected;
                _problem = null;
                _clientSince = DateTimeOffset.Now;
                Interlocked.Exchange(ref _bytesToDevice, 0);
                Interlocked.Exchange(ref _bytesFromDevice, 0);
            }
            Log.Info($"{Name}: {remote} connected; {Port.ComPort} open at {Port.Serial.BaudRate} baud, {Port.Serial.Format}.");
            _changed();

            reason = await ForwardAsync(serial, client, ct);
        }
        catch (Exception ex)
        {
            Log.Error($"{Name}: unexpected error in the session with {remote}", ex);
            reason = $"unexpected error: {ex.Message}";
        }
        finally
        {
            if (serial is not null)
            {
                try
                {
                    serial.Dispose();
                }
                catch (IOException ex)
                {
                    Log.Warn($"{Name}: closing {Port.ComPort} failed: {ex.Message}");
                }
            }
            await SocketTuning.CloseGracefullyAsync(client);

            DateTimeOffset? since;
            lock (_gate)
            {
                since = _clientSince;
                _sessionActive = false;
                _client = null;
                _clientEndpoint = null;
                _clientSince = null;
                if (_state == PortServerState.Connected)
                    _state = PortServerState.Listening;
            }
            var duration = since is { } s ? $" after {DateTimeOffset.Now - s:hh\\:mm\\:ss}" : "";
            Log.Info($"{Name}: {remote} disconnected{duration} ({reason}). Bytes to {Port.ComPort}: {Interlocked.Read(ref _bytesToDevice)}, "
                + $"from it: {Interlocked.Read(ref _bytesFromDevice)}.");
            Note($"{remote.Address} disconnected ({reason})");
        }
    }

    /// <summary>Copies bytes both ways until either side ends, and returns why it ended.</summary>
    private async Task<string> ForwardAsync(Stream serial, Socket client, CancellationToken ct)
    {
        using var network = new NetworkStream(client, ownsSocket: false);
        using var pumps = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var toDevice = PumpAsync(network, serial, towardDevice: true, pumps.Token);
        var fromDevice = PumpAsync(serial, network, towardDevice: false, pumps.Token);

        var first = await Task.WhenAny(toDevice, fromDevice);
        await pumps.CancelAsync();
        try
        {
            await Task.WhenAll(toDevice, fromDevice).WaitAsync(SessionShutdownTimeout, CancellationToken.None);
        }
        catch (Exception)
        {
            // The first pump's result is examined below; cancellation of the other one is expected.
        }

        if (ct.IsCancellationRequested)
            return "the server stopped";
        if (first.IsCompletedSuccessfully)
            return first == toDevice ? "the client closed the connection" : "the COM port stopped responding";
        return first.Exception?.InnerException switch
        {
            PumpFailure { SerialSide: true } failure => $"{Port.ComPort} failed: {failure.InnerException!.Message} The device may have been unplugged.",
            PumpFailure failure => $"the connection was lost: {failure.InnerException!.Message}",
            { } other => $"unexpected error: {other.Message}",
            null => "the session ended",
        };
    }

    private async Task PumpAsync(Stream source, Stream destination, bool towardDevice, CancellationToken ct)
    {
        var direction = towardDevice ? $"→ {Port.ComPort}" : $"← {Port.ComPort}";
        var buffer = new byte[BufferSize];
        while (true)
        {
            int read;
            try
            {
                read = await source.ReadAsync(buffer, ct);
            }
            catch (Exception ex) when (IsIoFailure(ex))
            {
                throw new PumpFailure(serialSide: !towardDevice, ex);
            }

            if (read == 0)
            {
                // End of stream on TCP: the client closed. A serial read only returns nothing when its (very long)
                // time-out expires, which isn't an error.
                if (towardDevice)
                    return;
                await Task.Delay(10, ct);
                continue;
            }

            if (LogPayload)
                Log.Payload(Port.ComPort, direction, buffer.AsSpan(0, read));

            try
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            catch (Exception ex) when (IsIoFailure(ex))
            {
                throw new PumpFailure(serialSide: towardDevice, ex);
            }
            Interlocked.Add(ref towardDevice ? ref _bytesToDevice : ref _bytesFromDevice, read);
        }
    }

    private void SetState(PortServerState state, string? problem)
    {
        lock (_gate)
        {
            if (_state == state && _problem == problem)
                return;
            _state = state;
            _problem = problem;
        }
        _changed();
    }

    private void SetProblem(string problem)
    {
        lock (_gate)
            _problem = problem;
        _changed();
    }

    private void Note(string lastEvent)
    {
        lock (_gate)
            _lastEvent = $"{DateTimeOffset.Now:HH:mm:ss} {lastEvent}";
        _changed();
    }

    private static bool IsIoFailure(Exception ex) =>
        ex is IOException or SocketException or ObjectDisposedException or UnauthorizedAccessException;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        Socket? client;
        lock (_gate)
            client = _client;
        try
        {
            client?.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }
        try
        {
            await Task.WhenAll(_run, _session).WaitAsync(SessionShutdownTimeout);
        }
        catch (TimeoutException)
        {
            Log.Warn($"{Name}: didn't stop within {SessionShutdownTimeout.TotalSeconds:0} s.");
        }
        _stop.Dispose();
    }

    private sealed class PumpFailure(bool serialSide, Exception inner) : Exception(inner.Message, inner)
    {
        public bool SerialSide { get; } = serialSide;
    }
}
