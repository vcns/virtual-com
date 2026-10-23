using Vcns.VirtualCom.Bridge.Configuration;
using Vcns.VirtualCom.Bridge.VirtualPorts;

namespace Vcns.VirtualCom.Bridge.Tests;

public class ConfigValidatorTests
{
    private static ConnectionProfile Profile(string server = "192.168.10.50", int port = 5200, string com = "COM9", string? bridge = null) =>
        new() { TargetServer = server, TargetPort = port, LocalComPort = com, BridgePort = bridge };

    private static AppConfig Config(params ConnectionProfile[] profiles) => new() { Profiles = [.. profiles] };

    [Fact]
    public void Valid_configuration_has_no_errors()
    {
        var config = Config(Profile(), Profile("192.168.10.51", com: "COM10"), Profile("controller.example.net", com: "COM11"));
        Assert.Empty(ConfigValidator.Validate(config));
    }

    [Theory]
    [InlineData("192.168.10.50", true)]
    [InlineData("controller.example.net", true)]
    [InlineData("uhd-jr-01", true)]
    [InlineData("fe80::1", true)]
    [InlineData("192.168.10.500", false)]
    [InlineData("10.1", false)]
    [InlineData("bad host", false)]
    [InlineData("", false)]
    public void Host_names_are_validated(string host, bool valid) => Assert.Equal(valid, ConfigValidator.IsValidHost(host));

    [Theory]
    [InlineData("COM1", true)]
    [InlineData("COM9", true)]
    [InlineData("COM256", true)]
    [InlineData("com12", true)]
    [InlineData("COM0", false)]
    [InlineData("COM257", false)]
    [InlineData("COM09", false)]
    [InlineData("LPT1", false)]
    [InlineData("CNCB0", false)]
    public void Com_port_names_are_validated(string name, bool valid) => Assert.Equal(valid, ConfigValidator.TryParseComNumber(name, out _));

    [Fact]
    public void Incomplete_profile_is_rejected()
    {
        var errors = ConfigValidator.Validate(Config(Profile(server: "", port: 0, com: "")));
        Assert.Equal(3, errors.Count);
        Assert.All(errors, e => Assert.StartsWith("Row 1:", e));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void Out_of_range_ports_are_rejected(int port) =>
        Assert.Contains(ConfigValidator.Validate(Config(Profile(port: port))), e => e.Contains("out of range"));

    [Fact]
    public void A_com_port_can_belong_to_only_one_profile()
    {
        var errors = ConfigValidator.Validate(Config(Profile(), Profile("192.168.10.51", com: "com9")));
        Assert.Contains(errors, e => e.Contains("more than one profile") && e.Contains("rows 1, 2"));
    }

    [Fact]
    public void Each_endpoint_gets_its_own_com_port()
    {
        var errors = ConfigValidator.Validate(Config(Profile("Controller.example.net"), Profile("controller.example.net", com: "COM10")));
        Assert.Contains(errors, e => e.Contains("same endpoint"));
    }

    [Fact]
    public void Same_host_on_different_ports_is_allowed() =>
        Assert.Empty(ConfigValidator.Validate(Config(Profile(port: 5200), Profile(port: 5201, com: "COM10"))));

    [Fact]
    public void Bridge_port_must_differ_from_every_local_port()
    {
        Assert.Contains(ConfigValidator.Validate(Config(Profile(bridge: "COM9"))), e => e.Contains("other end"));
        Assert.Contains(ConfigValidator.Validate(Config(Profile(bridge: "COM10"), Profile("192.168.10.51", com: "COM10"))),
            e => e.Contains("also used as a Local COM Port"));
    }

    [Fact]
    public void Option_ranges_are_validated()
    {
        var config = Config(Profile());
        config.Options.ConnectTimeoutSeconds = 0;
        config.Options.ReleaseIntervalSeconds = 61;
        config.Options.InUseDetectionMilliseconds = -1;
        Assert.Equal(3, ConfigValidator.Validate(config).Count);
    }

    [Fact]
    public void Active_profile_cannot_be_removed_or_retargeted()
    {
        var active = Profile();
        var removed = Config();
        var retargeted = Config(active.Clone());
        retargeted.Profiles[0].TargetServer = "192.168.10.99";
        var disabled = Config(active.Clone());
        disabled.Profiles[0].Enabled = false;

        Assert.Contains(ConfigValidator.ValidateActiveChanges(removed, [active]), e => e.Contains("before removing"));
        Assert.Contains(ConfigValidator.ValidateActiveChanges(retargeted, [active]), e => e.Contains("before changing"));
        Assert.Empty(ConfigValidator.ValidateActiveChanges(disabled, [active]));
    }

    [Fact]
    public void Ports_owned_by_other_devices_are_rejected()
    {
        var raw = new RawPortData();
        raw.SerialComm[@"\Device\VCP0"] = "COM3";
        raw.SerialComm[@"\Device\com0com10"] = "COM9";
        raw.SerialComm[@"\Device\com0com20"] = "CNCB0";
        raw.DevicePortNames[@"USB\VID_1234&PID_5678\ABC"] = "COM5";
        raw.ComDb = [0b0100_0000]; // COM7 reserved
        var inventory = new PortInventory(raw);

        var config = Config(Profile(com: "COM3"), Profile("a.example", com: "COM5"), Profile("b.example", com: "COM7"),
            Profile("c.example", com: "COM1"), Profile("d.example", com: "COM9"), Profile("e.example", com: "COM20"),
            Profile("f.example", com: "COM4", bridge: "COM21"));
        var errors = ConfigValidator.ValidatePorts(config, inventory);

        Assert.Equal(4, errors.Count);
        Assert.Contains(errors, e => e.StartsWith("Row 1: COM3") && e.Contains(@"\Device\VCP0"));
        Assert.Contains(errors, e => e.StartsWith("Row 2: COM5") && e.Contains("unplugged"));
        Assert.Contains(errors, e => e.StartsWith("Row 3: COM7"));
        Assert.Contains(errors, e => e.StartsWith("Row 4: COM1"));
    }

    [Fact]
    public void Endpoint_keys_normalise_case_and_ip_literals()
    {
        Assert.Equal(ConfigValidator.EndpointKey("Controller.Example.NET", 5200), ConfigValidator.EndpointKey("controller.example.net", 5200));
        Assert.Equal("192.168.10.50:5200", ConfigValidator.EndpointKey("192.168.10.50", 5200));
    }
}
