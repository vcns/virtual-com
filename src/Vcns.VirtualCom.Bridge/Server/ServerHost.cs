using Vcns.VirtualCom.Bridge.Configuration;
using Vcns.VirtualCom.Bridge.Diagnostics;
using Vcns.VirtualCom.Bridge.Transport;

namespace Vcns.VirtualCom.Bridge.Server;

/// <summary>
/// Runs a <see cref="PortServer"/> for each enabled shared port. Applying a new configuration restarts only the ports
/// whose settings changed, so other clients stay connected.
/// </summary>
internal sealed class ServerHost : IAsyncDisposable
{
    private static readonly TimeSpan StatusDelay = TimeSpan.FromMilliseconds(200);

    private readonly Func<ServerPort, Stream> _openSerial;
    private readonly string? _statusFile;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly List<PortServer> _servers = [];
    private readonly Timer _statusTimer;
    private int _statusPending;

    /// <param name="openSerial">Opens a shared port's COM port; by default the real port with its line settings.</param>
    /// <param name="statusFile">Where to write <see cref="ServerStatus"/> after each change; null for none.</param>
    public ServerHost(Func<ServerPort, Stream>? openSerial = null, string? statusFile = null)
    {
        _openSerial = openSerial ?? (port => SerialDevice.OpenDevice(port.ComPort, port.Serial));
        _statusFile = statusFile;
        _statusTimer = new Timer(_ => WriteStatus());
    }

    public IReadOnlyList<PortServer> Servers
    {
        get
        {
            lock (_servers)
                return _servers.ToArray();
        }
    }

    public async Task ApplyAsync(ServerConfig config)
    {
        await _applyGate.WaitAsync();
        try
        {
            var wanted = config.Enabled ? config.Ports.Where(p => p.Enabled).ToList() : [];
            foreach (var server in Servers)
            {
                if (server.LogPayload == config.LogPayload && wanted.Any(w => w.SameAs(server.Port)))
                    continue;
                lock (_servers)
                    _servers.Remove(server);
                await server.DisposeAsync();
            }
            foreach (var port in wanted)
            {
                if (Servers.Any(s => s.Port.SameAs(port)))
                    continue;
                var server = new PortServer(port.Clone(), _openSerial, config.LogPayload, StatusChanged);
                lock (_servers)
                    _servers.Add(server);
                server.Start();
            }
            Log.Info(config.Enabled
                ? $"Server configuration applied: {wanted.Count} shared port(s){(wanted.Count == 0 ? "" : ": " + string.Join(", ", wanted.Select(p => $"{p.ComPort} on {p.Endpoint}")))}."
                : "Server mode is switched off; no ports are shared.");
        }
        finally
        {
            _applyGate.Release();
        }
        StatusChanged();
    }

    private void StatusChanged()
    {
        if (_statusFile is not null && Interlocked.Exchange(ref _statusPending, 1) == 0)
            _statusTimer.Change(StatusDelay, Timeout.InfiniteTimeSpan);
    }

    private void WriteStatus()
    {
        Interlocked.Exchange(ref _statusPending, 0);
        var status = new ServerStatus
        {
            UpdatedAt = DateTimeOffset.Now,
            ProcessId = Environment.ProcessId,
            Ports = Servers.Select(s => s.Snapshot()).OrderBy(s => s.TcpPort).ToList(),
        };
        try
        {
            status.Write(_statusFile!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not write the server status file: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _applyGate.WaitAsync();
        try
        {
            foreach (var server in Servers)
                await server.DisposeAsync();
            lock (_servers)
                _servers.Clear();
        }
        finally
        {
            _applyGate.Release();
        }
        await _statusTimer.DisposeAsync();
        if (_statusFile is not null)
            WriteStatus();
    }
}
