using System.Net.Sockets;
using Vcns.VirtualCom.Bridge.Configuration;
using Vcns.VirtualCom.Bridge.Server;
using static Vcns.VirtualCom.Bridge.Tests.TestNetwork;

namespace Vcns.VirtualCom.Bridge.Tests;

/// <summary>Customer, label, device and notes: the fields that tell dozens of profiles apart.</summary>
public sealed class LabelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VcnsBridgeTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static ConnectionProfile Profile(string? customer = null, string? label = null) => new()
    {
        TargetServer = "192.168.10.50",
        TargetPort = 5200,
        LocalComPort = "COM9",
        Customer = customer,
        Label = label,
    };

    [Fact]
    public void Labels_round_trip_and_blank_ones_are_dropped()
    {
        var path = Path.Combine(_directory, "config.json");
        var store = new ConfigStore(path);
        var profile = Profile("  Acme Ltd, Leeds ", "Main hall LED wall");
        profile.Device = "NovaPro UHD Jr";
        profile.Notes = "   ";
        store.Save(new AppConfig { Profiles = [profile] });

        var loaded = Assert.Single(store.Load(out var warning).Profiles);
        Assert.Null(warning);
        Assert.Equal("Acme Ltd, Leeds", loaded.Customer);
        Assert.Equal("Main hall LED wall", loaded.Label);
        Assert.Equal("NovaPro UHD Jr", loaded.Device);
        Assert.Null(loaded.Notes);
    }

    [Fact]
    public void Configurations_from_before_labels_still_load()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "config.json");
        File.WriteAllText(path, """{ "profiles": [ { "targetServer": "192.168.10.50", "targetPort": 5200, "localComPort": "COM9" } ] }""");
        var loaded = Assert.Single(new ConfigStore(path).Load(out var warning).Profiles);
        Assert.Null(warning);
        Assert.Null(loaded.Label);
        Assert.Equal("COM9", loaded.Title);
    }

    [Theory]
    [InlineData(null, null, "COM9", null)]
    [InlineData(null, "Main hall", "COM9 · Main hall", "Main hall")]
    [InlineData("Acme", null, "COM9", "Acme")]
    [InlineData("Acme", "Main hall", "COM9 · Main hall", "Acme – Main hall")]
    public void Titles_and_device_manager_names_combine_customer_and_label(string? customer, string? label, string title, string? portName)
    {
        var profile = Profile(customer, label);
        Assert.Equal(title, profile.Title);
        Assert.Equal(portName, profile.PortDisplayName);
    }

    [Fact]
    public void Overlong_or_multi_line_labels_are_rejected()
    {
        var errors = ConfigValidator.ValidateProfile(Profile(new string('x', ConfigValidator.MaxNameLength + 1), "Main\nhall"));
        Assert.Contains(errors, e => e.StartsWith("Customer is 81 characters long"));
        Assert.Contains(errors, e => e.StartsWith("Label contains a line break"));
    }

    [Fact]
    public void Labels_can_change_while_a_profile_is_connected()
    {
        var active = Profile("Acme", "Main hall");
        var edited = active.Clone();
        edited.Customer = "Acme Ltd";
        edited.Label = "Main hall LED wall";
        edited.Notes = "Pause Zabbix host acme-leeds-uhd first.";
        Assert.Empty(ConfigValidator.ValidateActiveChanges(new AppConfig { Profiles = [edited] }, [active]));

        edited.TargetPort = 5201;
        Assert.NotEmpty(ConfigValidator.ValidateActiveChanges(new AppConfig { Profiles = [edited] }, [active]));
    }

    [Fact]
    public void Server_labels_round_trip()
    {
        var path = Path.Combine(_directory, "server.json");
        ServerConfigStore.Save(path, new ServerConfig
        {
            Enabled = true,
            Ports = [new ServerPort { ComPort = "COM3", Label = " Acme Leeds – MCTRL300 #1 ", TcpPort = 5203, ListenAddress = "10.0.0.2", AllowedClients = ["10.0.0.0/24"] }],
        });
        var port = Assert.Single(ServerConfigStore.Load(path, out _).Ports);
        Assert.Equal("Acme Leeds – MCTRL300 #1", port.Label);
        Assert.Equal("COM3 · Acme Leeds – MCTRL300 #1", port.Title);
    }

    [Fact]
    public async Task A_new_server_label_applies_without_dropping_the_client()
    {
        var devices = new List<Socket>();
        await using var host = new ServerHost(_ =>
        {
            var (device, serial) = CreateSerialPairAsync().GetAwaiter().GetResult();
            lock (devices)
                devices.Add(device);
            return new NetworkStream(serial, ownsSocket: true);
        });
        var port = new ServerPort { ComPort = "COM3", Label = "MCTRL300 #1", TcpPort = 0, ListenAddress = "127.0.0.1", AllowedClients = ["127.0.0.1"] };
        var config = new ServerConfig { Enabled = true, Ports = [port] };
        await host.ApplyAsync(config);
        var server = Assert.Single(host.Servers);
        await WaitUntilAsync(() => server.State == PortServerState.Listening);

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(System.Net.IPAddress.Loopback, server.LocalPort);
        await WaitUntilAsync(() => server.State == PortServerState.Connected);

        var relabelled = config.Clone();
        relabelled.Ports[0].Label = "Acme Leeds – MCTRL300 #1";
        await host.ApplyAsync(relabelled);

        Assert.Same(server, Assert.Single(host.Servers));
        Assert.Equal(PortServerState.Connected, server.State);
        Assert.Equal("Acme Leeds – MCTRL300 #1", server.Snapshot().Label);
        lock (devices)
        {
            foreach (var device in devices)
                device.Dispose();
        }
    }
}
