using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NovaStarProxy.Configuration;
using NovaStarProxy.Transport;
using static NovaStarProxy.Tests.TestNetwork;

namespace NovaStarProxy.Tests;

public sealed class ProxySessionTests : IAsyncDisposable
{
    private static readonly TimeSpan EndTimeout = TimeSpan.FromSeconds(15);
    private readonly List<Socket> _sockets = [];
    private readonly ProxyOptions _options = new()
    {
        ConnectTimeoutSeconds = 3,
        ReleaseIntervalSeconds = 0,
        InUseDetectionMilliseconds = 300,
        ShowZabbixReminder = false,
    };

    public async ValueTask DisposeAsync()
    {
        foreach (var socket in _sockets)
            socket.Dispose();
        await Task.CompletedTask;
    }

    private static ConnectionProfile Profile(int port, string host = "127.0.0.1", string com = "COM9") =>
        new() { TargetServer = host, TargetPort = port, LocalComPort = com };

    /// <summary>A session manager plus a "virtual serial port" that is a loopback socket; App is NovaLCT's end.</summary>
    private async Task<(SessionManager Manager, Socket App, PortTarget Port)> CreateManagerAsync()
    {
        var (app, port) = await CreatePortAsync();
        return (new SessionManager(() => _options), app, port);
    }

    private async Task<(Socket App, PortTarget Port)> CreatePortAsync(string description = "test port", Action<TestEndpoint>? opened = null)
    {
        var (app, proxy) = await CreateSerialPairAsync();
        _sockets.AddRange([app, proxy]);
        return (app, new PortTarget(description, () =>
        {
            var endpoint = new TestEndpoint(new NetworkStream(proxy, ownsSocket: true));
            opened?.Invoke(endpoint);
            return endpoint;
        }));
    }

    [Fact]
    public async Task Bytes_are_forwarded_unchanged_in_both_directions()
    {
        await using var controller = new FakeController();
        var (manager, app, port) = await CreateManagerAsync();
        var session = manager.Start(Profile(controller.Port), port);
        var remote = await controller.NextSessionAsync();
        await WaitUntilAsync(() => session.State == SessionState.Connected);

        var upstream = RandomBytes(1_000_000, seed: 1);
        var downstream = RandomBytes(750_000, seed: 2);
        var atController = ReceiveExactlyAsync(remote, upstream.Length);
        var atApp = ReceiveExactlyAsync(app, downstream.Length);
        await Task.WhenAll(SendInChunksAsync(app, upstream, seed: 3), SendInChunksAsync(remote, downstream, seed: 4), atController, atApp)
            .WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(upstream, await atController);
        Assert.Equal(downstream, await atApp);
        await WaitUntilAsync(() => session.BytesToController == upstream.Length && session.BytesFromController == downstream.Length);

        manager.Stop(session.Profile.Id);
        await session.Completion.WaitAsync(EndTimeout);
        Assert.Equal(SessionState.Stopped, session.State);
        Assert.Equal(SessionOutcome.StoppedByUser, session.Outcome);
        Assert.True(await WaitForCloseAsync(remote), "The controller should see the TCP connection close.");
        Assert.Empty(manager.ActiveSessions);
    }

    [Fact]
    public async Task Concurrent_session_is_reported_as_in_use_without_disturbing_the_existing_one()
    {
        await using var controller = new FakeController();
        using var zabbix = new TcpClient();
        await zabbix.ConnectAsync(IPAddress.Loopback, controller.Port);
        var zabbixSession = await controller.NextSessionAsync();

        var (manager, _, port) = await CreateManagerAsync();
        var session = manager.Start(Profile(controller.Port), port);
        await session.Completion.WaitAsync(EndTimeout);

        Assert.Equal(SessionState.Failed, session.State);
        Assert.Equal(SessionOutcome.InUse, session.Outcome);
        Assert.StartsWith("Port in use", session.OutcomeMessage);

        // The existing client still has a working session.
        zabbixSession.Send("ping"u8);
        var reply = new byte[4];
        Assert.Equal(4, await zabbix.GetStream().ReadAtLeastAsync(reply, 4).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Controller_closing_later_without_data_is_reported_as_in_use()
    {
        await using var controller = new FakeController();
        var (manager, _, port) = await CreateManagerAsync();
        var session = manager.Start(Profile(controller.Port), port);
        var remote = await controller.NextSessionAsync();
        await WaitUntilAsync(() => session.State == SessionState.Connected);

        remote.Dispose();
        await session.Completion.WaitAsync(EndTimeout);
        Assert.Equal(SessionOutcome.InUse, session.Outcome);
    }

    [Fact]
    public async Task Remote_disconnect_after_data_ends_the_session_and_releases_the_port()
    {
        await using var controller = new FakeController();
        var (manager, app, port) = await CreateManagerAsync();
        var session = manager.Start(Profile(controller.Port), port);
        var remote = await controller.NextSessionAsync();
        await WaitUntilAsync(() => session.State == SessionState.Connected);

        remote.Send("hello"u8);
        Assert.Equal("hello", Encoding.ASCII.GetString(await ReceiveExactlyAsync(app, 5)));
        remote.Shutdown(SocketShutdown.Both);
        remote.Dispose();

        await session.Completion.WaitAsync(EndTimeout);
        Assert.Equal(SessionState.Failed, session.State);
        Assert.Equal(SessionOutcome.ClosedByController, session.Outcome);
        Assert.True(await WaitForCloseAsync(app), "The proxy should release its end of the virtual port.");
        Assert.Empty(manager.ActiveSessions);
    }

    [Fact]
    public async Task Connection_reset_mid_session_is_reported_as_lost()
    {
        await using var controller = new FakeController();
        var (manager, app, port) = await CreateManagerAsync();
        var session = manager.Start(Profile(controller.Port), port);
        var remote = await controller.NextSessionAsync();
        await WaitUntilAsync(() => session.State == SessionState.Connected);

        remote.Send("x"u8);
        await ReceiveExactlyAsync(app, 1);
        remote.LingerState = new LingerOption(true, 0);
        remote.Close();

        await session.Completion.WaitAsync(EndTimeout);
        Assert.Equal(SessionOutcome.ConnectionLost, session.Outcome);
        Assert.Contains("reset", session.OutcomeMessage);
    }

    [Fact]
    public async Task Refused_connection_is_reported()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        _options.ConnectTimeoutSeconds = 10; // Windows retries refused loopback connections for about 2 s.
        var (manager, _, port) = await CreateManagerAsync();
        var session = manager.Start(Profile(closedPort), port);
        await session.Completion.WaitAsync(EndTimeout);

        Assert.Equal(SessionOutcome.Refused, session.Outcome);
        Assert.Contains("refused", session.OutcomeMessage);
    }

    [Fact]
    public async Task Unreachable_host_times_out_or_is_reported_unreachable()
    {
        _options.ConnectTimeoutSeconds = 1;
        var (manager, _, port) = await CreateManagerAsync();
        var watch = Stopwatch.StartNew();
        var session = manager.Start(Profile(5200, host: "192.0.2.1"), port); // TEST-NET-1: never routed
        await session.Completion.WaitAsync(EndTimeout);

        Assert.Contains(session.Outcome, new[] { SessionOutcome.TimedOut, SessionOutcome.Unreachable });
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Unknown_host_is_reported()
    {
        var (manager, _, port) = await CreateManagerAsync();
        var session = manager.Start(Profile(5200, host: "no-such-controller.invalid"), port);
        await session.Completion.WaitAsync(EndTimeout);
        Assert.Equal(SessionOutcome.HostNotFound, session.Outcome);
    }

    [Fact]
    public async Task Start_can_be_cancelled_while_connecting()
    {
        _options.ConnectTimeoutSeconds = 30;
        var (manager, _, port) = await CreateManagerAsync();
        var session = manager.Start(Profile(5200, host: "192.0.2.1"), port);
        await Task.Delay(300);
        var watch = Stopwatch.StartNew();
        manager.Stop(session.Profile.Id);
        await session.Completion.WaitAsync(EndTimeout);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Cancelling took {watch.Elapsed}.");
        Assert.Equal(SessionState.Stopped, session.State);
        Assert.Equal("Start cancelled.", session.OutcomeMessage);
    }

    [Fact]
    public async Task Virtual_port_failure_stops_before_any_tcp_connection()
    {
        await using var controller = new FakeController();
        var manager = new SessionManager(() => _options);
        var session = manager.Start(Profile(controller.Port),
            new PortTarget("CNCB0", () => throw new IOException("The virtual serial port CNCB0 does not exist.")));
        await session.Completion.WaitAsync(EndTimeout);

        Assert.Equal(SessionOutcome.VirtualPortError, session.Outcome);
        Assert.Contains("CNCB0", session.OutcomeMessage);
        Assert.Equal(0, controller.AcceptedCount);
    }

    [Fact]
    public async Task Same_endpoint_cannot_be_started_twice()
    {
        await using var controller = new FakeController();
        var (manager, _, port) = await CreateManagerAsync();
        var first = manager.Start(Profile(controller.Port), port);
        await controller.NextSessionAsync();

        var (_, otherPort) = await CreatePortAsync("other port");
        var ex = Assert.Throws<StartRejectedException>(() => manager.Start(Profile(controller.Port, com: "COM10"), otherPort));
        Assert.Contains("already in use by COM9", ex.Message);
        Assert.Throws<StartRejectedException>(() => manager.Start(first.Profile, port));
        await manager.StopAllAsync(EndTimeout);
    }

    [Fact]
    public async Task Same_controller_under_another_name_is_locked_by_address()
    {
        await using var controller = new FakeController();
        var (manager, _, port) = await CreateManagerAsync();
        var first = manager.Start(Profile(controller.Port, host: "127.0.0.1"), port);
        await controller.NextSessionAsync();
        await WaitUntilAsync(() => first.State == SessionState.Connected);

        var (_, otherPort) = await CreatePortAsync("other port");
        var second = manager.Start(Profile(controller.Port, host: "localhost", com: "COM10"), otherPort);
        await second.Completion.WaitAsync(EndTimeout);

        Assert.Equal(SessionOutcome.InUse, second.Outcome);
        Assert.Contains("through COM9", second.OutcomeMessage);
        Assert.Equal(1, controller.AcceptedCount);
        Assert.Equal(SessionState.Connected, first.State);
        await manager.StopAllAsync(EndTimeout);
    }

    [Fact]
    public async Task Restart_waits_for_the_release_interval_then_reconnects()
    {
        _options.ReleaseIntervalSeconds = 1;
        await using var controller = new FakeController();
        var (_, port1) = await CreatePortAsync();
        var (_, port2) = await CreatePortAsync();
        var manager = new SessionManager(() => _options);
        var profile = Profile(controller.Port);

        var first = manager.Start(profile, port1);
        var remote = await controller.NextSessionAsync();
        await WaitUntilAsync(() => first.State == SessionState.Connected);
        manager.Stop(profile.Id);
        await first.Completion.WaitAsync(EndTimeout);
        Assert.True(await WaitForCloseAsync(remote));
        controller.EndSession();

        var watch = Stopwatch.StartNew();
        var second = manager.Start(profile, port2);
        await WaitUntilAsync(() => second.StatusText.StartsWith("Waiting"), 2000);
        await controller.NextSessionAsync();
        await WaitUntilAsync(() => second.State == SessionState.Connected);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(800), $"Reconnected after only {watch.Elapsed}.");
        await manager.StopAllAsync(EndTimeout);
    }

    [Fact]
    public async Task Stop_all_releases_every_session()
    {
        await using var controllerA = new FakeController();
        await using var controllerB = new FakeController();
        var (appA, portA) = await CreatePortAsync("port A");
        var (appB, portB) = await CreatePortAsync("port B");
        var manager = new SessionManager(() => _options);

        var a = manager.Start(Profile(controllerA.Port), portA);
        var b = manager.Start(Profile(controllerB.Port, com: "COM10"), portB);
        var remoteA = await controllerA.NextSessionAsync();
        var remoteB = await controllerB.NextSessionAsync();
        await WaitUntilAsync(() => a.State == SessionState.Connected && b.State == SessionState.Connected);

        await manager.StopAllAsync(EndTimeout);

        Assert.True(a.IsEnded && b.IsEnded);
        Assert.Empty(manager.ActiveSessions);
        Assert.True(await WaitForCloseAsync(remoteA));
        Assert.True(await WaitForCloseAsync(remoteB));
        Assert.True(await WaitForCloseAsync(appA));
        Assert.True(await WaitForCloseAsync(appB));
    }

    [Fact]
    public async Task Link_is_raised_only_once_the_controller_accepts_the_session()
    {
        await using var controller = new FakeController();
        TestEndpoint? endpoint = null;
        var (_, port) = await CreatePortAsync(opened: e => endpoint = e);
        var manager = new SessionManager(() => _options);

        var session = manager.Start(Profile(controller.Port), port);
        await controller.NextSessionAsync();
        await WaitUntilAsync(() => session.State == SessionState.Connected);
        Assert.Equal([true], endpoint!.LinkChanges);
        Assert.Equal("test peer", session.DescribePeer());
        await manager.StopAllAsync(EndTimeout);

        // A controller that is busy with another client never gets the link raised.
        using var zabbix = new TcpClient();
        await using var busy = new FakeController();
        await zabbix.ConnectAsync(IPAddress.Loopback, busy.Port);
        await busy.NextSessionAsync();
        TestEndpoint? refused = null;
        var (_, port2) = await CreatePortAsync(opened: e => refused = e);
        var failed = manager.Start(Profile(busy.Port), port2);
        await failed.Completion.WaitAsync(EndTimeout);
        Assert.Equal(SessionOutcome.InUse, failed.Outcome);
        Assert.Empty(refused!.LinkChanges);
    }

    [Fact]
    public void Opening_a_missing_serial_device_gives_a_clear_error()
    {
        var ex = Assert.Throws<IOException>(() => SerialDevice.Open("CNCB987"));
        Assert.Contains("does not exist", ex.Message);
    }
}
