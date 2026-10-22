using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NovaStarProxy.Configuration;
using NovaStarProxy.Diagnostics;

namespace NovaStarProxy.Transport;

internal enum SessionState
{
    Starting,
    Connected,
    Stopping,
    Stopped,
    Failed,
}

internal enum SessionOutcome
{
    None,
    StoppedByUser,
    InUse,
    Refused,
    TimedOut,
    HostNotFound,
    Unreachable,
    VirtualPortError,
    ClosedByController,
    ConnectionLost,
    Error,
}

/// <summary>Callbacks from a session to whatever owns it (the <see cref="SessionManager"/>).</summary>
internal interface ISessionCoordinator
{
    /// <summary>Atomically reserves the resolved controller addresses so no other session can use them.</summary>
    bool TryClaim(ProxySession session, IReadOnlyCollection<IPEndPoint> endpoints, out string? holder);

    /// <summary>How long to wait so the controller has released a recently closed session to the same endpoint.</summary>
    TimeSpan GetReleaseDelay(ProxySession session);

    void Changed(ProxySession session);

    void Ended(ProxySession session);
}

/// <summary>
/// One active bridge between a virtual serial port and a TCP connection. Bytes are copied in both directions
/// exactly as received: no framing, interpretation or buffering beyond a single read.
/// </summary>
internal sealed class ProxySession
{
    private const int BufferSize = 16 * 1024;
    private static readonly TimeSpan PumpShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromMilliseconds(500);

    private readonly PortTarget _target;
    private volatile IPortEndpoint? _endpoint;
    private readonly ISessionCoordinator _coordinator;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private long _bytesToController;
    private long _bytesFromController;
    private SessionState _state = SessionState.Starting;
    private string _status = "Starting…";

    public ProxySession(ConnectionProfile profile, PortTarget target, ProxyOptions options, ISessionCoordinator coordinator)
    {
        Profile = profile;
        Options = options;
        _target = target;
        _coordinator = coordinator;
    }

    public ConnectionProfile Profile { get; }

    /// <summary>Which port the proxy opens, e.g. "COM9 (VCNS virtual COM driver)" or "CNCB0".</summary>
    public string PortDescription => _target.Description;

    public ProxyOptions Options { get; }

    public SessionState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public string StatusText
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    public bool IsEnded => State is SessionState.Stopped or SessionState.Failed;

    public SessionOutcome Outcome { get; private set; }

    /// <summary>Why the session ended, phrased for the user.</summary>
    public string? OutcomeMessage { get; private set; }

    /// <summary>The session got as far as forwarding data.</summary>
    public bool WasConnected { get; private set; }

    /// <summary>A TCP connection was opened at some point, so the controller may need time to release it.</summary>
    public bool TcpOpened { get; private set; }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public DateTimeOffset? ConnectedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public long BytesToController => Interlocked.Read(ref _bytesToController);

    public long BytesFromController => Interlocked.Read(ref _bytesFromController);

    internal IReadOnlyCollection<IPEndPoint> ClaimedEndpoints { get; set; } = [];

    public Task Completion { get; private set; } = Task.CompletedTask;

    private string Port => Profile.LocalComPort;

    private string Endpoint => Profile.Endpoint;

    internal void Start() => Completion = Task.Run(RunAsync);

    /// <summary>What NovaLCT (or whatever has the COM port open) is doing, if the driver can tell.</summary>
    public string? DescribePeer()
    {
        try
        {
            return _endpoint?.DescribePeer();
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_state is SessionState.Stopped or SessionState.Failed or SessionState.Stopping)
                return;
            _state = SessionState.Stopping;
            _status = WasConnected ? "Disconnecting…" : "Cancelling…";
            // Marks the token cancelled immediately (so RunAsync can't move to Connected after this) but runs the
            // cancellation callbacks on the thread pool rather than on the caller, which is usually the UI thread.
            _ = _stop.CancelAsync();
        }
        Log.Info($"{Port}: stop requested.");
        _coordinator.Changed(this);
    }

    private async Task RunAsync()
    {
        var ct = _stop.Token;
        Socket? socket = null;
        try
        {
            SetStatus($"Resolving {Profile.TargetServer}…");
            var addresses = await ResolveAsync(ct);
            var endpoints = addresses.Select(a => new IPEndPoint(a, Profile.TargetPort)).ToArray();
            if (!_coordinator.TryClaim(this, endpoints, out var holder))
            {
                throw new SessionFailure(SessionOutcome.InUse,
                    $"Port in use: {Endpoint} is already connected through {holder} in this application. The controller accepts only one session at a time.");
            }

            var delay = _coordinator.GetReleaseDelay(this);
            if (delay > TimeSpan.Zero)
            {
                SetStatus($"Waiting {delay.TotalSeconds:0.#} s for the controller to release the previous session…");
                await Task.Delay(delay, ct);
            }

            SetStatus($"Opening virtual port {PortDescription}…");
            var port = OpenPort();

            SetStatus($"Connecting to {Endpoint}…");
            socket = await ConnectAsync(addresses, ct);
            TcpOpened = true;
            Log.Info($"{Port}: TCP connection established {socket.LocalEndPoint} → {socket.RemoteEndPoint}.");

            if (Options.InUseDetectionMilliseconds > 0)
            {
                SetStatus($"Checking that {Endpoint} accepted the session…");
                if (ClosedByRemoteWithin(socket, TimeSpan.FromMilliseconds(Options.InUseDetectionMilliseconds), ct))
                {
                    throw new SessionFailure(SessionOutcome.InUse,
                        $"Port in use: {Endpoint} accepted the connection and then closed it straight away. Another client, such as Zabbix polling, "
                        + $"probably holds the controller's only session. Pause it, then start {Port} again.");
                }
            }

            // Only now does NovaLCT see the "cable" (CTS, DSR, DCD) come up, where the driver supports it.
            RaiseLink(port);

            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                _state = SessionState.Connected;
                _status = $"Connected to {Endpoint}";
                WasConnected = true;
                ConnectedAt = DateTimeOffset.Now;
            }
            Log.Info($"{Port}: connected to {Endpoint} via {PortDescription}; forwarding data.");
            if (port.DescribePeer() is { } peer)
                Log.Info($"{Port}: {peer}.");
            _coordinator.Changed(this);

            await BridgeAsync(port.Stream, socket, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetOutcome(SessionOutcome.StoppedByUser, WasConnected ? "Disconnected." : "Start cancelled.");
        }
        catch (SessionFailure failure)
        {
            SetOutcome(failure.Outcome, failure.Message);
        }
        catch (Exception ex)
        {
            Log.Error($"{Port}: unexpected error", ex);
            SetOutcome(SessionOutcome.Error, $"Unexpected error: {ex.Message}");
        }
        finally
        {
            if (socket is not null)
                await CloseGracefullyAsync(socket);
            var endpoint = _endpoint;
            _endpoint = null;
            endpoint?.Dispose();
            Complete();
        }
    }

    private async Task BridgeAsync(Stream serial, Socket socket, CancellationToken ct)
    {
        using var network = new NetworkStream(socket, ownsSocket: false);
        using var pumps = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var toController = PumpAsync(serial, network, towardController: true, pumps.Token);
        var fromController = PumpAsync(network, serial, towardController: false, pumps.Token);

        var first = await Task.WhenAny(toController, fromController);
        await pumps.CancelAsync();
        try
        {
            await Task.WhenAll(toController, fromController).WaitAsync(PumpShutdownTimeout, CancellationToken.None);
        }
        catch (Exception)
        {
            // Each pump's own failure is examined below; cancellation of the other one is expected.
        }

        ct.ThrowIfCancellationRequested();

        if (first.Exception?.InnerException is PumpException pump)
            throw DescribePumpFailure(pump);
        if (first.Exception?.InnerException is { } unexpected)
        {
            Log.Error($"{Port}: forwarding failed", unexpected);
            throw new SessionFailure(SessionOutcome.Error, $"Forwarding stopped unexpectedly: {unexpected.Message}");
        }
        if (first == fromController && first.IsCompletedSuccessfully)
        {
            if (BytesFromController == 0)
            {
                throw new SessionFailure(SessionOutcome.InUse,
                    $"Port in use: {Endpoint} closed the connection without sending any data. Another client, such as Zabbix polling, may hold the controller's session.");
            }
            throw new SessionFailure(SessionOutcome.ClosedByController, $"{Endpoint} closed the connection.");
        }
        throw new SessionFailure(SessionOutcome.Error, "Forwarding stopped unexpectedly.");
    }

    private async Task PumpAsync(Stream source, Stream destination, bool towardController, CancellationToken ct)
    {
        var (sourceIsNetwork, direction) = towardController ? (false, "→ controller") : (true, "← controller");
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
                throw new PumpException(sourceIsNetwork, ex);
            }

            if (read == 0)
            {
                // End of stream on TCP means the controller closed the connection. A serial read only returns
                // nothing if its (very long) time-out expires, which is not an error.
                if (sourceIsNetwork)
                    return;
                await Task.Delay(10, ct);
                continue;
            }

            if (Options.LogPayload)
                Log.Payload(Port, direction, buffer.AsSpan(0, read));

            try
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            catch (Exception ex) when (IsIoFailure(ex))
            {
                throw new PumpException(!sourceIsNetwork, ex);
            }
            Interlocked.Add(ref towardController ? ref _bytesToController : ref _bytesFromController, read);
        }
    }

    private SessionFailure DescribePumpFailure(PumpException pump)
    {
        if (!pump.IsNetwork)
        {
            return new SessionFailure(SessionOutcome.VirtualPortError,
                $"The virtual serial port {PortDescription} reported an error: {pump.InnerException!.Message}");
        }

        var socketError = (pump.InnerException as SocketException ?? pump.InnerException?.InnerException as SocketException)?.SocketErrorCode;
        if (socketError == SocketError.ConnectionReset && BytesFromController == 0)
        {
            return new SessionFailure(SessionOutcome.InUse,
                $"Port in use: {Endpoint} reset the connection without sending any data. Another client, such as Zabbix polling, may hold the controller's session.");
        }
        return new SessionFailure(SessionOutcome.ConnectionLost, socketError switch
        {
            SocketError.ConnectionReset => $"The connection to {Endpoint} was reset by the controller or the network.",
            SocketError.TimedOut or SocketError.ConnectionAborted =>
                $"The connection to {Endpoint} was lost: the controller stopped responding (network interrupted or controller restarted).",
            SocketError.NetworkDown or SocketError.NetworkUnreachable or SocketError.HostUnreachable =>
                $"The connection to {Endpoint} was lost: the network became unavailable.",
            _ => $"The connection to {Endpoint} failed: {pump.InnerException!.Message}",
        });
    }

    private IPortEndpoint OpenPort()
    {
        try
        {
            var endpoint = _target.Open();
            _endpoint = endpoint;
            return endpoint;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            throw new SessionFailure(SessionOutcome.VirtualPortError, ex.Message);
        }
    }

    private void RaiseLink(IPortEndpoint port)
    {
        try
        {
            port.SetLinkUp(true);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            // Data still flows; NovaLCT just won't see the modem lines rise.
            Log.Warn($"{Port}: could not raise the virtual port's modem lines ({ex.Message}).");
        }
    }

    private async Task<IPAddress[]> ResolveAsync(CancellationToken ct)
    {
        if (IPAddress.TryParse(Profile.TargetServer, out var literal))
            return [literal];

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Options.ConnectTimeoutSeconds));
        try
        {
            var addresses = (await Dns.GetHostAddressesAsync(Profile.TargetServer, timeout.Token))
                .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6 ? 1 : 0)
                .ToArray();
            if (addresses.Length == 0)
                throw new SessionFailure(SessionOutcome.HostNotFound, $"'{Profile.TargetServer}' has no IP addresses.");
            return addresses;
        }
        catch (SocketException ex)
        {
            throw new SessionFailure(SessionOutcome.HostNotFound,
                $"Could not find '{Profile.TargetServer}' ({ex.Message}). Check the host name and this PC's DNS settings.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SessionFailure(SessionOutcome.HostNotFound, $"Looking up '{Profile.TargetServer}' timed out.");
        }
    }

    private async Task<Socket> ConnectAsync(IPAddress[] addresses, CancellationToken ct)
    {
        var socket = addresses.All(a => a.AddressFamily == AddressFamily.InterNetwork)
            ? new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            : new Socket(SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Options.ConnectTimeoutSeconds));
        try
        {
            await socket.ConnectAsync(addresses, Profile.TargetPort, timeout.Token);
            ConfigureKeepAlive(socket);
            return socket;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new SessionFailure(SessionOutcome.TimedOut,
                $"No response from {Endpoint} within {Options.ConnectTimeoutSeconds} s. Check that the controller is on and reachable from this PC (network, VPN, firewall).");
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => new SessionFailure(SessionOutcome.Refused,
                    $"{Endpoint} refused the connection. The host is reachable, but nothing is accepting connections on port {Profile.TargetPort}."),
                SocketError.TimedOut => new SessionFailure(SessionOutcome.TimedOut,
                    $"No response from {Endpoint}. Check that the controller is on and reachable from this PC (network, VPN, firewall)."),
                SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.NetworkDown => new SessionFailure(SessionOutcome.Unreachable,
                    $"{Endpoint} is unreachable ({ex.Message}). Check the network connection or VPN."),
                _ => new SessionFailure(SessionOutcome.ConnectionLost, $"Could not connect to {Endpoint}: {ex.Message}"),
            };
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Detects a dead link within about 30 s even when no data is flowing, so the state shown is never stale for long.
    /// </summary>
    private void ConfigureKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 2);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 5);
            // TCP_MAXRT: give up on unacknowledged data after 30 s rather than Windows' default of a minute or more.
            socket.SetSocketOption(SocketOptionLevel.Tcp, (SocketOptionName)5, 30);
        }
        catch (SocketException ex)
        {
            Log.Warn($"{Port}: could not configure TCP keep-alive ({ex.Message}).");
        }
    }

    /// <summary>True if the controller closes or resets the connection within <paramref name="window"/>.</summary>
    private static bool ClosedByRemoteWithin(Socket socket, TimeSpan window, CancellationToken ct)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(window.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
            if (remaining <= TimeSpan.Zero)
                return false;
            try
            {
                // Readable with nothing to read means the peer sent FIN (or RST).
                if (socket.Poll(remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100), SelectMode.SelectRead))
                    return socket.Available == 0;
            }
            catch (SocketException)
            {
                return true;
            }
        }
    }

    private static async Task CloseGracefullyAsync(Socket socket)
    {
        try
        {
            // Send FIN and read whatever is still in flight, so Windows doesn't answer unread data with a reset and the
            // controller sees an orderly close it can release promptly.
            socket.Shutdown(SocketShutdown.Send);
            var scratch = new byte[4096];
            using var drain = new CancellationTokenSource(DrainTimeout);
            while (await socket.ReceiveAsync(scratch, SocketFlags.None, drain.Token) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            socket.Dispose();
        }
    }

    private void SetStatus(string status)
    {
        lock (_gate)
        {
            if (_state != SessionState.Starting)
                return;
            _status = status;
        }
        _coordinator.Changed(this);
    }

    private void SetOutcome(SessionOutcome outcome, string message)
    {
        Outcome = outcome;
        OutcomeMessage = message;
    }

    private void Complete()
    {
        lock (_gate)
        {
            _state = Outcome == SessionOutcome.StoppedByUser ? SessionState.Stopped : SessionState.Failed;
            _status = OutcomeMessage ?? "Stopped.";
            EndedAt = DateTimeOffset.Now;
        }

        var duration = ConnectedAt is { } since ? $"; connected for {EndedAt - since:hh\\:mm\\:ss}" : "";
        var summary = $"{Port} → {Endpoint}: session ended ({Outcome}). {OutcomeMessage} "
            + $"Bytes to controller: {BytesToController}, from controller: {BytesFromController}{duration}.";
        if (Outcome is SessionOutcome.StoppedByUser)
            Log.Info(summary);
        else
            Log.Warn(summary);

        _coordinator.Ended(this);
    }

    private static bool IsIoFailure(Exception ex) =>
        ex is IOException or SocketException or ObjectDisposedException or UnauthorizedAccessException;

    private sealed class PumpException(bool isNetwork, Exception inner) : Exception(inner.Message, inner)
    {
        public bool IsNetwork { get; } = isNetwork;
    }
}

internal sealed class SessionFailure(SessionOutcome outcome, string message) : Exception(message)
{
    public SessionOutcome Outcome { get; } = outcome;
}
