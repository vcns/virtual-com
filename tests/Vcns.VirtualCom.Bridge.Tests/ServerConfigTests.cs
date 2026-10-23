using System.Net;
using Vcns.VirtualCom.Bridge.Configuration;
using Vcns.VirtualCom.Bridge.Server;

namespace Vcns.VirtualCom.Bridge.Tests;

public sealed class ServerConfigTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VcnsBridgeTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static ServerPort Port(string com = "COM3", int tcp = 5203, string listen = "10.0.0.2", params string[] clients) => new()
    {
        ComPort = com,
        TcpPort = tcp,
        ListenAddress = listen,
        AllowedClients = clients.Length == 0 ? ["10.0.0.0/24"] : [.. clients],
    };

    private static ServerConfig Config(params ServerPort[] ports) => new() { Enabled = true, Ports = [.. ports] };

    [Theory]
    [InlineData("10.0.0.5", "10.0.0.5", true)]
    [InlineData("10.0.0.5", "10.0.0.6", false)]
    [InlineData("10.20.0.0/16", "10.20.200.1", true)]
    [InlineData("10.20.0.0/16", "10.21.0.1", false)]
    [InlineData("10.20.3.7/16", "10.20.9.9", true)]
    [InlineData("0.0.0.0/0", "192.0.2.44", true)]
    [InlineData("fd00::/8", "fd12:3456::1", true)]
    [InlineData("fd00::/8", "10.0.0.1", false)]
    public void Client_filter_matches_addresses_and_ranges(string entry, string client, bool allowed)
    {
        Assert.Equal(allowed, new ClientFilter([entry]).Allows(IPAddress.Parse(client)));
    }

    [Fact]
    public void Client_filter_treats_IPv4_mapped_IPv6_addresses_as_IPv4()
    {
        Assert.True(new ClientFilter(["10.0.0.0/8"]).Allows(IPAddress.Parse("10.1.2.3").MapToIPv6()));
    }

    [Theory]
    [InlineData("10.1")]
    [InlineData("10.0.0.0/33")]
    [InlineData("server.example.net")]
    [InlineData("")]
    public void Client_filter_rejects_invalid_entries(string entry)
    {
        Assert.False(ClientFilter.TryParse(entry, out _, out _));
    }

    [Fact]
    public void A_valid_configuration_passes()
    {
        Assert.Empty(ServerConfigValidator.Validate(Config(Port(), Port("COM4", 5204))));
    }

    [Fact]
    public void Each_com_port_is_shared_once()
    {
        var errors = ServerConfigValidator.Validate(Config(Port(), Port("com3", 5204)));
        Assert.Contains(errors, e => e.Contains("shared by more than one server row"));
    }

    [Fact]
    public void Tcp_ports_clash_when_listeners_overlap()
    {
        Assert.Contains(ServerConfigValidator.Validate(Config(Port(), Port("COM4", 5203, "0.0.0.0"))), e => e.Contains("both listen on TCP port 5203"));
        Assert.Empty(ServerConfigValidator.Validate(Config(Port(), Port("COM4", 5203, "10.0.0.3"))));
    }

    [Fact]
    public void An_enabled_port_needs_allowed_clients()
    {
        var port = Port();
        port.AllowedClients = [];
        Assert.Contains(ServerConfigValidator.ValidatePort(port), e => e.Contains("Allowed Clients is empty"));
        port.Enabled = false;
        Assert.DoesNotContain(ServerConfigValidator.ValidatePort(port), e => e.Contains("Allowed Clients is empty"));
    }

    [Fact]
    public void Invalid_values_are_reported()
    {
        var port = Port("COMX", 70000, "10.1", "not-an-address");
        port.Serial.BaudRate = 10;
        port.Serial.DataBits = 9;
        var errors = ServerConfigValidator.ValidatePort(port);
        Assert.Contains(errors, e => e.Contains("not a valid COM port name"));
        Assert.Contains(errors, e => e.Contains("out of range (1–65535)"));
        Assert.Contains(errors, e => e.Contains("not a full IPv4 address"));
        Assert.Contains(errors, e => e.Contains("'not-an-address'"));
        Assert.Contains(errors, e => e.Contains("Baud rate 10"));
        Assert.Contains(errors, e => e.Contains("Data bits"));
    }

    // Parity and stop bits are passed as their numbers: test methods are public and the enums are internal.
    [Theory]
    [InlineData("8N1", 8, (int)SerialParity.None, (int)SerialStopBits.One)]
    [InlineData("7e1", 7, (int)SerialParity.Even, (int)SerialStopBits.One)]
    [InlineData("8O2", 8, (int)SerialParity.Odd, (int)SerialStopBits.Two)]
    [InlineData("5M1.5", 5, (int)SerialParity.Mark, (int)SerialStopBits.OnePointFive)]
    public void Serial_formats_parse_and_print(string text, int dataBits, int parity, int stopBits)
    {
        Assert.True(SerialSettings.TryParseFormat(text, out var d, out var p, out var s));
        Assert.Equal((dataBits, (SerialParity)parity, (SerialStopBits)stopBits), (d, p, s));
        Assert.Equal(text.ToUpperInvariant(), new SerialSettings { DataBits = d, Parity = p, StopBits = s }.Format);
    }

    [Theory]
    [InlineData("9N1")]
    [InlineData("8X1")]
    [InlineData("8N3")]
    public void Invalid_serial_formats_are_rejected(string text)
    {
        Assert.False(SerialSettings.TryParseFormat(text, out _, out _, out _));
    }

    [Fact]
    public void Tcp_port_is_the_base_plus_the_com_number()
    {
        Assert.Equal(5203, TcpPortSuggester.Suggest("COM3", 5200, new HashSet<int>()));
        Assert.Equal(4008, TcpPortSuggester.Suggest("COM8", 4000, new HashSet<int>()));
    }

    [Fact]
    public void Tcp_port_suggestion_skips_ports_in_use()
    {
        Assert.Equal(5205, TcpPortSuggester.Suggest("COM3", 5200, new HashSet<int> { 5203, 5204 }));
    }

    [Fact]
    public void Excluded_port_ranges_are_read_from_netsh_output()
    {
        const string output = """

            Protocol tcp Port Exclusion Ranges

            Start Port    End Port
            ----------    --------
                  5357        5357
                 50000       50059     *

            * - Administered port exclusions.

            """;
        Assert.Equal([(5357, 5357), (50000, 50059)], TcpPortSuggester.ParseExcludedRanges(output).ToArray());
    }

    [Fact]
    public void Server_configuration_round_trips_with_readable_enums()
    {
        var path = Path.Combine(_directory, "server.json");
        var config = Config(Port(" com3 ", clients: [" 10.0.0.0/24 ", "10.0.0.0/24", "192.0.2.10"]));
        config.Ports[0].Serial = new SerialSettings { BaudRate = 9600, DataBits = 7, Parity = SerialParity.Even, FlowControl = SerialFlowControl.RtsCts };
        ServerConfigStore.Save(path, config);

        var json = File.ReadAllText(path);
        Assert.Contains("\"parity\": \"Even\"", json);
        Assert.Contains("\"flowControl\": \"RtsCts\"", json);

        var loaded = ServerConfigStore.Load(path, out var warning);
        Assert.Null(warning);
        Assert.True(loaded.Enabled);
        var port = Assert.Single(loaded.Ports);
        Assert.Equal("COM3", port.ComPort);
        Assert.Equal(["10.0.0.0/24", "192.0.2.10"], port.AllowedClients);
        Assert.True(port.SameAs(config.Ports[0]));
    }

    [Fact]
    public void Invalid_server_configuration_is_not_written()
    {
        var path = Path.Combine(_directory, "server.json");
        Assert.Throws<ConfigValidationException>(() => ServerConfigStore.Save(path, Config(Port(), Port())));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Missing_server_configuration_means_server_mode_is_off()
    {
        var config = ServerConfigStore.Load(Path.Combine(_directory, "none.json"), out var warning);
        Assert.False(config.Enabled);
        Assert.Null(warning);
    }
}
