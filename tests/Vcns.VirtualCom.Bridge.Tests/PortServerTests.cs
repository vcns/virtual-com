using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Vcns.VirtualCom.Bridge.Configuration;
using Vcns.VirtualCom.Bridge.Server;
using Vcns.VirtualCom.Bridge.Transport;
using static Vcns.VirtualCom.Bridge.Tests.TestNetwork;

namespace Vcns.VirtualCom.Bridge.Tests;

/// <summary>
/// Server mode over loopback. Each time the server opens "the COM port", it gets one end of a fresh socket pair;
/// the test holds the other end as the device.
/// </summary>
public sealed class PortServerTests : IAsyncDisposable
{
    private readonly ConcurrentQueue<Socket> _devices = new();
    private readonly List<IAsyncDisposable> _cleanup = [];
    private int _opens;
    private volatile bool _deviceMissing;

    private Stream OpenSerial(ServerPort port)
    {
        if (_deviceMissing)
            throw new IOException($"{port.ComPort} isn't present: the device may be unplugged.");
        var (device, serial) = CreateSerialPairAsync().GetAwaiter().GetResult();
        _devices.Enqueue(device);
        Interlocked.Increment(ref _opens);
        return new NetworkStream(serial, ownsSocket: true);
    }

    private static ServerPort Shared(params string[] clients) => new()
    {
        ComPort = "COM3",
        TcpPort = 0,
        ListenAddress = "127.0.0.1",
        AllowedClients = clients.Length == 0 ? ["127.0.0.1"] : [.. clients],
    };

    private async Task<PortServer> StartAsync(ServerPort port)
    {
        var server = new PortServer(port, OpenSerial, logPayload: false, changed: () => { });
        _cleanup.Add(server);
        server.Start();
        await WaitUntilAsync(() => server.State == PortServerState.Listening);
        return server;
    }

    private static async Task<Socket> ConnectAsync(PortServer server)
    {
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, server.LocalPort);
        return client;
    }

    private async Task<Socket> NextDeviceAsync()
    {
        Socket? device = null;
        await WaitUntilAsync(() => _devices.TryDequeue(out device));
        return device!;
    }

    [Fact]
    public async Task Bytes_pass_both_ways_unchanged()
    {
        var server = await StartAsync(Shared());
        using var client = await ConnectAsync(server);
        using var device = await NextDeviceAsync();

        var upstream = RandomBytes(150_000, seed: 11);
        var downstream = RandomBytes(120_000, seed: 12);
        var atDevice = ReceiveExactlyAsync(device, upstream.Length);
        var atClient = ReceiveExactlyAsync(client, downstream.Length);
        await Task.WhenAll(SendInChunksAsync(client, upstream, seed: 13), SendInChunksAsync(device, downstream, seed: 14), atDevice, atClient)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(upstream, await atDevice);
        Assert.Equal(downstream, await atClient);
        await WaitUntilAsync(() => server.Snapshot().BytesToDevice == upstream.Length && server.Snapshot().BytesFromDevice == downstream.Length);
        Assert.Equal(nameof(PortServerState.Connected), server.Snapshot().State);
    }

    [Fact]
    public async Task A_second_client_is_accepted_and_closed_without_disturbing_the_first()
    {
        var server = await StartAsync(Shared());
        using var first = await ConnectAsync(server);
        using var device = await NextDeviceAsync();

        using var second = await ConnectAsync(server);
        Assert.True(await WaitForCloseAsync(second, timeoutMs: 1000), "The second client should be closed straight away.");
        Assert.Equal(1, _opens);
        Assert.Contains("Turned away", server.Snapshot().LastEvent);

        await first.SendAsync(new byte[] { 0x55, 0xAA }.AsMemory(), SocketFlags.None);
        Assert.Equal([0x55, 0xAA], await ReceiveExactlyAsync(device, 2));
    }

    [Fact]
    public async Task A_client_that_isnt_allowed_is_closed()
    {
        var server = await StartAsync(Shared("10.0.0.0/8"));
        using var client = await ConnectAsync(server);
        Assert.True(await WaitForCloseAsync(client, timeoutMs: 1000));
        Assert.Equal(0, _opens);
        Assert.Contains("not an allowed client", server.Snapshot().LastEvent);
    }

    [Fact]
    public async Task The_com_port_is_open_only_while_a_client_is_connected()
    {
        var server = await StartAsync(Shared());
        Assert.Equal(0, _opens);

        var client = await ConnectAsync(server);
        using var device = await NextDeviceAsync();
        client.Shutdown(SocketShutdown.Both);
        client.Dispose();

        Assert.True(await WaitForCloseAsync(device), "The COM port should be closed when the client leaves.");
        await WaitUntilAsync(() => server.State == PortServerState.Listening);

        using var again = await ConnectAsync(server);
        using var device2 = await NextDeviceAsync();
        Assert.Equal(2, _opens);
    }

    [Fact]
    public async Task A_missing_device_closes_the_client_and_the_port_keeps_listening()
    {
        var server = await StartAsync(Shared());
        _deviceMissing = true;
        using (var client = await ConnectAsync(server))
            Assert.True(await WaitForCloseAsync(client, timeoutMs: 2000));
        await WaitUntilAsync(() => server.Snapshot().Problem?.Contains("isn't present") == true);

        _deviceMissing = false;
        using var retry = await ConnectAsync(server);
        using var device = await NextDeviceAsync();
        await retry.SendAsync(new byte[] { 1, 2, 3 }.AsMemory(), SocketFlags.None);
        Assert.Equal([1, 2, 3], await ReceiveExactlyAsync(device, 3));
    }

    [Fact]
    public async Task A_busy_tcp_port_is_reported_and_retried()
    {
        using var squatter = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        squatter.Start();
        var port = Shared();
        port.TcpPort = ((IPEndPoint)squatter.LocalEndpoint).Port;

        var server = new PortServer(port, OpenSerial, logPayload: false, changed: () => { });
        _cleanup.Add(server);
        server.Start();
        await WaitUntilAsync(() => server.State == PortServerState.NotListening);
        Assert.Contains("already in use", server.Snapshot().Describe());
    }

    [Fact]
    public async Task Client_mode_sees_a_busy_shared_port_as_in_use()
    {
        var server = await StartAsync(Shared());
        using var holder = await ConnectAsync(server);
        using var device = await NextDeviceAsync();

        var (app, proxySide) = await CreateSerialPairAsync();
        using (app)
        {
            var manager = new SessionManager(() => new ProxyOptions { InUseDetectionMilliseconds = 500, ReleaseIntervalSeconds = 0 });
            var profile = new ConnectionProfile { TargetServer = "127.0.0.1", TargetPort = server.LocalPort, LocalComPort = "COM9" };
            var session = manager.Start(profile, new PortTarget("test", () => new TestEndpoint(new NetworkStream(proxySide, ownsSocket: true))));
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(SessionOutcome.InUse, session.Outcome);
        }
    }

    [Fact]
    public async Task Client_mode_and_server_mode_carry_bytes_end_to_end()
    {
        var server = await StartAsync(Shared());
        var (app, proxySide) = await CreateSerialPairAsync();
        using (app)
        {
            var manager = new SessionManager(() => new ProxyOptions { InUseDetectionMilliseconds = 300, ReleaseIntervalSeconds = 0 });
            var profile = new ConnectionProfile { TargetServer = "127.0.0.1", TargetPort = server.LocalPort, LocalComPort = "COM9" };
            var session = manager.Start(profile, new PortTarget("test", () => new TestEndpoint(new NetworkStream(proxySide, ownsSocket: true))));
            using var device = await NextDeviceAsync();
            await WaitUntilAsync(() => session.State == SessionState.Connected);

            var request = RandomBytes(4096, seed: 21);
            var reply = RandomBytes(8192, seed: 22);
            var atDevice = ReceiveExactlyAsync(device, request.Length);
            await app.SendAsync(request.AsMemory(), SocketFlags.None);
            Assert.Equal(request, await atDevice);
            var atApp = ReceiveExactlyAsync(app, reply.Length);
            await device.SendAsync(reply.AsMemory(), SocketFlags.None);
            Assert.Equal(reply, await atApp);

            session.Stop();
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(await WaitForCloseAsync(device), "Disconnecting the client should release the shared COM port.");
        }
    }

    [Fact]
    public async Task Applying_a_configuration_restarts_only_changed_ports()
    {
        await using var host = new ServerHost(OpenSerial);
        var com3 = Shared();
        var com4 = Shared();
        com4.ComPort = "COM4";
        var config = new ServerConfig { Enabled = true, Ports = [com3, com4] };
        await host.ApplyAsync(config);
        var before = host.Servers.ToDictionary(s => s.Port.ComPort);

        var changed = config.Clone();
        changed.Ports[1].Serial.BaudRate = 9600;
        await host.ApplyAsync(changed);
        var after = host.Servers.ToDictionary(s => s.Port.ComPort);

        Assert.Same(before["COM3"], after["COM3"]);
        Assert.NotSame(before["COM4"], after["COM4"]);
        Assert.Equal(9600, after["COM4"].Port.Serial.BaudRate);

        changed.Enabled = false;
        await host.ApplyAsync(changed);
        Assert.Empty(host.Servers);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var item in _cleanup)
            await item.DisposeAsync();
        while (_devices.TryDequeue(out var device))
            device.Dispose();
    }
}
