using System.Net;
using System.Net.Sockets;
using Vcns.VirtualCom.Bridge.Configuration;
using Vcns.VirtualCom.Bridge.Server;
using static Vcns.VirtualCom.Bridge.Tests.TestNetwork;

namespace Vcns.VirtualCom.Bridge.Tests;

/// <summary>
/// Server mode over the real driver. The virtual port's COM end stands in for a hardware COM port, and its host end
/// plays the device, so the test can see the line settings the server applies.
/// </summary>
[Collection(VirtualPortCollection.Name)]
public sealed class ServerEndToEndTests
{
    private static readonly string PortName = Environment.GetEnvironmentVariable("VCOM_TEST_PORT") ?? "";

    [VirtualPortFact]
    public async Task A_shared_port_applies_its_line_settings_and_carries_bytes()
    {
        using var device = VirtualComHost.Open(PortName);
        await using var host = new ServerHost();
        await host.ApplyAsync(new ServerConfig
        {
            Enabled = true,
            Ports =
            [
                new ServerPort
                {
                    ComPort = PortName,
                    TcpPort = 0,
                    ListenAddress = "127.0.0.1",
                    AllowedClients = ["127.0.0.1"],
                    Serial = new SerialSettings { BaudRate = 9600, DataBits = 7, Parity = SerialParity.Even },
                },
            ],
        });
        var server = Assert.Single(host.Servers);
        await WaitUntilAsync(() => server.State == PortServerState.Listening);
        Assert.False(device.GetState().ComOpen, "The COM port should stay closed until a client connects.");

        using (var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true })
        {
            await client.ConnectAsync(IPAddress.Loopback, server.LocalPort);
            await WaitUntilAsync(() => device.GetState().ComOpen);

            var state = device.GetState();
            Assert.Equal(9600u, state.BaudRate);
            Assert.Equal(7, state.DataBits);
            Assert.Equal(2, state.Parity);
            Assert.True(state.Dtr && state.Rts, "DTR and RTS should be raised with no flow control.");

            var request = RandomBytes(2000, seed: 31);
            await client.SendAsync(request.AsMemory(), SocketFlags.None);
            var received = new byte[request.Length];
            await device.Stream.ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(request, received);

            var reply = RandomBytes(3000, seed: 32);
            await device.Stream.WriteAsync(reply);
            Assert.Equal(reply, await ReceiveExactlyAsync(client, reply.Length).WaitAsync(TimeSpan.FromSeconds(10)));
        }

        await WaitUntilAsync(() => !device.GetState().ComOpen);
    }
}
