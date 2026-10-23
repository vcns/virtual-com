using Vcns.VirtualCom.Bridge.Configuration;
using Vcns.VirtualCom.Bridge.Transport;
using Vcns.VirtualCom.Bridge.VirtualPorts;

namespace Vcns.VirtualCom.Bridge.Tests;

public class PortInventoryTests
{
    /// <summary>The management host described by the user: COM1 built in, COM3–COM8 USB ports for MCTRL300s.</summary>
    private static RawPortData ManagementHost()
    {
        var raw = new RawPortData();
        raw.SerialComm[@"\Device\Serial0"] = "COM1";
        for (var i = 3; i <= 8; i++)
            raw.SerialComm[$@"\Device\Silabser{i - 3}"] = $"COM{i}";
        raw.ComDb = [0b1111_1101]; // COM1, COM3–COM8
        return raw;
    }

    [Fact]
    public void Pairs_are_read_from_serialcomm()
    {
        var raw = ManagementHost();
        raw.Com0comInstalled = true;
        raw.SerialComm[@"\Device\com0com10"] = "COM9";
        raw.SerialComm[@"\Device\com0com20"] = "CNCB0";
        raw.SerialComm[@"\Device\com0com112"] = "COM10";
        raw.SerialComm[@"\Device\com0com212"] = "CNCB12";
        var inventory = new PortInventory(raw);

        Assert.Equal([new Com0comPair(0, "COM9", "CNCB0"), new Com0comPair(12, "COM10", "CNCB12")], inventory.Pairs);
        Assert.Equal("CNCB0", inventory.PartnerOf("COM9"));
        Assert.Equal("COM9", inventory.PartnerOf("cncb0"));
        Assert.Null(inventory.PartnerOf("COM3"));
    }

    [Fact]
    public void Hidden_proxy_side_is_found_from_com0com_parameters()
    {
        var raw = ManagementHost();
        raw.SerialComm[@"\Device\com0com10"] = "COM9";
        raw.Com0comParameters["CNCA0"] = new() { ["PortName"] = "COM9", ["AllDataBits"] = "yes" };
        raw.Com0comParameters["CNCB0"] = new() { ["HiddenMode"] = "yes" };
        Assert.Equal("CNCB0", new PortInventory(raw).PartnerOf("COM9"));
    }

    [Fact]
    public void Automatic_com_numbers_use_the_real_port_name()
    {
        var raw = new RawPortData();
        raw.Com0comParameters["CNCA3"] = new() { ["PortName"] = "COM#", ["RealPortName"] = "COM14" };
        raw.Com0comParameters["CNCB3"] = new() { ["PortName"] = "-" };
        Assert.Equal([new Com0comPair(3, "COM14", "CNCB3")], new PortInventory(raw).Pairs);
    }

    [Fact]
    public void Unplugged_devices_from_the_device_tree_are_not_mistaken_for_virtual_ports()
    {
        var raw = new RawPortData();
        raw.DevicePortNames[@"COM0COM\PORT\CNCA0"] = "COM9";
        raw.DevicePortNames[@"USB\VID_10C4&PID_EA60\0001"] = "COM12";
        var inventory = new PortInventory(raw);
        Assert.Equal("CNCB0", inventory.PartnerOf("COM9"));
        Assert.Null(inventory.ForeignUse("COM9"));
        Assert.Contains("unplugged", inventory.ForeignUse("COM12"));
    }

    [Fact]
    public void New_ports_are_allocated_above_every_port_in_use()
    {
        var inventory = new PortInventory(ManagementHost());
        Assert.Equal(["COM9", "COM10"], inventory.SuggestFreePorts([], 2));
        Assert.Equal(["COM13"], inventory.SuggestFreePorts(["COM12", "COM10"], 1));
    }

    [Fact]
    public void Allocation_skips_reserved_numbers_and_existing_virtual_ports()
    {
        var raw = ManagementHost();
        raw.ComDb = [0b1111_1101, 0b0000_0010]; // also COM10, e.g. an unplugged MCTRL300
        raw.SerialComm[@"\Device\com0com10"] = "COM11";
        Assert.Equal(["COM12"], new PortInventory(raw).SuggestFreePorts([], 1));
    }

    [Fact]
    public void Com1_is_always_treated_as_reserved()
    {
        var inventory = new PortInventory(new RawPortData());
        Assert.NotNull(inventory.ForeignUse("COM1"));
        Assert.Equal(["COM2"], inventory.SuggestFreePorts([], 1));
    }

    [Fact]
    public void Virtual_ports_reserved_in_comdb_are_not_foreign()
    {
        var raw = ManagementHost();
        raw.ComDb = [0b1111_1101, 0b0000_0001]; // COM9 claimed by com0com
        raw.SerialComm[@"\Device\com0com10"] = "COM9";
        var inventory = new PortInventory(raw);
        Assert.Null(inventory.ForeignUse("COM9"));
        Assert.StartsWith("Virtual port ready", inventory.Describe("COM9"));
        Assert.StartsWith("⚠", inventory.Describe("COM4"));
    }

    [Fact]
    public void A_pair_whose_driver_is_not_running_is_flagged()
    {
        // com0com's settings say the pair exists, but the port never reached SERIALCOMM (e.g. the driver was blocked).
        var raw = ManagementHost();
        raw.Com0comInstalled = true;
        raw.Com0comParameters["CNCA0"] = new() { ["PortName"] = "COM9" };
        raw.Com0comParameters["CNCB0"] = new() { ["HiddenMode"] = "yes" };
        var inventory = new PortInventory(raw);

        Assert.Equal("CNCB0", inventory.PartnerOf("COM9"));
        Assert.False(inventory.IsRunning("COM9"));
        Assert.StartsWith("⚠ Created but not running", inventory.Describe("COM9"));
    }

    [Fact]
    public void Vcns_ports_are_virtual_and_never_counted_as_another_devices()
    {
        var raw = ManagementHost();
        raw.SerialComm[@"\Device\00000125"] = "COM20";
        raw.DevicePortNames[@"ROOT\VCNSVCOM\0000"] = "COM20";
        raw.ComDb = [0b1111_1101, 0, 0b0000_1000]; // COM20 claimed too
        raw.VcnsPorts.Add(new VcnsPortInfo(@"ROOT\VCNSVCOM\0000", "COM20", Running: true, ProblemCode: 0));
        var inventory = new PortInventory(raw);

        Assert.True(inventory.IsVcnsPort("COM20"));
        Assert.True(inventory.IsVirtual("COM20"));
        Assert.True(inventory.IsRunning("COM20"));
        Assert.Null(inventory.ForeignUse("COM20"));
        Assert.StartsWith("Virtual port ready (VCNS", inventory.Describe("COM20"));
        Assert.Contains("COM20", inventory.VirtualComPorts);
        Assert.Equal(["COM21"], inventory.SuggestFreePorts([], 1));
    }

    [Fact]
    public void Port_targets_follow_the_kind_of_virtual_port()
    {
        var raw = ManagementHost();
        raw.VcnsPorts.Add(new VcnsPortInfo(@"ROOT\VCNSVCOM\0000", "COM20", Running: true, ProblemCode: 0));
        raw.VcnsPorts.Add(new VcnsPortInfo(@"ROOT\VCNSVCOM\0001", "COM21", Running: false, ProblemCode: 52));
        raw.SerialComm[@"\Device\com0com10"] = "COM9";
        raw.SerialComm[@"\Device\com0com20"] = "CNCB0";
        var inventory = new PortInventory(raw);
        ConnectionProfile Profile(string com, string? bridge = null) =>
            new() { TargetServer = "192.168.10.50", LocalComPort = com, BridgePort = bridge };

        Assert.True(PortTarget.TryResolve(Profile("COM20"), inventory, out var vcns, out _));
        Assert.Contains("VCNS", vcns.Description);
        Assert.True(PortTarget.TryResolve(Profile("COM9"), inventory, out var com0com, out _));
        Assert.Equal("CNCB0", com0com.Description);
        Assert.True(PortTarget.TryResolve(Profile("COM30", bridge: "COM31"), inventory, out var manual, out _));
        Assert.Equal("COM31", manual.Description);

        Assert.False(PortTarget.TryResolve(Profile("COM21"), inventory, out _, out var stopped));
        Assert.Contains("hasn't started", stopped);
        Assert.StartsWith("⚠ Created but not running", inventory.Describe("COM21"));
        Assert.False(PortTarget.TryResolve(Profile("COM40"), inventory, out _, out var missing));
        Assert.Contains("isn't a virtual COM port yet", missing);
    }

    [Fact]
    public void Live_registry_can_be_read_without_administrator_rights()
    {
        var inventory = PortInventory.Capture();
        Assert.NotNull(inventory.Pairs);
        Assert.NotEmpty(inventory.SuggestFreePorts([], 1));
    }
}
