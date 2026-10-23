using System.Diagnostics;
using System.IO.Ports;
using Vcns.VirtualCom.Bridge.Configuration;
using Vcns.VirtualCom.Bridge.Transport;
using Vcns.VirtualCom.Bridge.VirtualPorts;
using static Vcns.VirtualCom.Bridge.Tests.TestNetwork;

namespace Vcns.VirtualCom.Bridge.Tests;

/// <summary>Runs only when VCOM_TEST_PORT names an installed VCNS virtual port, e.g. COM20.</summary>
public sealed class VirtualPortFactAttribute : FactAttribute
{
    public VirtualPortFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VCOM_TEST_PORT")))
            Skip = "Set VCOM_TEST_PORT to an installed VCNS virtual COM port (e.g. COM20).";
    }
}

/// <summary>Tests that use the real test port run one at a time: each one opens its ends.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class VirtualPortCollection
{
    public const string Name = "Real virtual port";
}

/// <summary>
/// The whole chain over the real driver: a System.IO.Ports client on the virtual COM port (as NovaLCT would be),
/// the proxy, TCP, and a fake controller.
/// </summary>
[Collection(VirtualPortCollection.Name)]
public sealed class VcnsEndToEndTests
{
    private static readonly string PortName = Environment.GetEnvironmentVariable("VCOM_TEST_PORT") ?? "";

    private static void WaitUntil(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException($"Timed out waiting for {what}.");
            Thread.Sleep(10);
        }
    }

    [VirtualPortFact]
    public async Task Serial_client_talks_to_the_controller_through_the_driver_and_proxy()
    {
        await using var controller = new FakeController();
        using var com = new SerialPort(PortName, 115200, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = 5000,
        };
        com.Open();
        Assert.False(com.CtsHolding || com.DsrHolding || com.CDHolding, "No proxy yet: the line should look unplugged.");

        var profile = new ConnectionProfile { TargetServer = "127.0.0.1", TargetPort = controller.Port, LocalComPort = PortName };
        Assert.True(PortTarget.TryResolve(profile, PortInventory.Capture(includeDeviceScan: false), out var target, out var problem), problem);
        var manager = new SessionManager(() => new ProxyOptions { InUseDetectionMilliseconds = 300, ReleaseIntervalSeconds = 0 });

        var session = manager.Start(profile, target);
        var remote = await controller.NextSessionAsync();
        WaitUntil(() => session.State == SessionState.Connected, "the session to connect");
        WaitUntil(() => com.CtsHolding && com.DsrHolding && com.CDHolding, "the line to come up once the controller is connected");
        Assert.Contains("115200", session.DescribePeer());

        var request = RandomBytes(4096, seed: 1);
        com.BaseStream.Write(request);
        Assert.Equal(request, await ReceiveExactlyAsync(remote, request.Length));

        var response = RandomBytes(200_000, seed: 2);
        var received = new byte[response.Length];
        var reading = com.BaseStream.ReadExactlyAsync(received).AsTask();
        await SendInChunksAsync(remote, response, seed: 3);
        await reading.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(response, received);

        await manager.StopAllAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SessionOutcome.StoppedByUser, session.Outcome);
        WaitUntil(() => !com.CtsHolding && !com.DsrHolding && !com.CDHolding, "the line to drop after disconnecting");
        Assert.True(await WaitForCloseAsync(remote), "The controller should see the TCP connection close.");
    }
}
