namespace Vcns.VirtualCom.Bridge;

/// <summary>
/// The names, folders, defaults and wording that distinguish one branded build of the bridge from another, such as
/// NovaStar Proxy. Behaviour is the same in every edition.
/// </summary>
public sealed record BridgeEdition
{
    /// <summary>Shown in the tray, window titles and dialogs.</summary>
    public required string ProductName { get; init; }

    /// <summary>
    /// Folder under %APPDATA% (configuration) and %LOCALAPPDATA% (logs). Also names the single-instance lock, so two
    /// editions can run side by side. Changing it leaves existing users' settings behind.
    /// </summary>
    public required string DataFolderName { get; init; }

    /// <summary>Log files are named <c>&lt;prefix&gt;-YYYYMMDD.log</c>.</summary>
    public required string LogFilePrefix { get; init; }

    /// <summary>The application that opens the COM port, as written mid-sentence, e.g. "NovaLCT".</summary>
    public string SerialApplication { get; init; } = "your serial application";

    /// <summary>What sits at the TCP end of a profile, e.g. "controller".</summary>
    public string DeviceNoun { get; init; } = "device";

    /// <summary>Filled in for new profiles. Null leaves Target Port blank.</summary>
    public int? DefaultTargetPort { get; init; }

    /// <summary>Makes and models offered when typing in the Device column, alongside ones already used.</summary>
    public IReadOnlyList<string> DeviceSuggestions { get; init; } = [];

    /// <summary>A typical other client that may hold the device's only session, e.g. "Zabbix polling". Null for none.</summary>
    public string? OtherClientExample { get; init; }

    /// <summary>Shown before each connection unless the user turns it off. Null for none.</summary>
    public ConnectReminder? ConnectReminder { get; init; }

    /// <summary>
    /// Server mode suggests this plus the COM number as each shared port's TCP port: with 4000, COM3 is shared on
    /// 4003, the numbering serial device servers commonly use.
    /// </summary>
    public int ServerPortBase { get; init; } = 4000;

    public static BridgeEdition Generic { get; } = new()
    {
        ProductName = "VCNS Virtual COM Bridge",
        DataFolderName = "VcnsVirtualComBridge",
        LogFilePrefix = "vcom-bridge",
    };

    internal string SerialApplicationAtStart => char.ToUpperInvariant(SerialApplication[0]) + SerialApplication[1..];

    /// <summary>Completes "Another client… holds the session": ", such as Zabbix polling," or nothing.</summary>
    internal string OtherClientClause => OtherClientExample is null ? "" : $", such as {OtherClientExample},";
}

/// <summary>
/// A reminder shown before a connection starts, and a follow-up after it ends. In <see cref="Text"/>, "{endpoint}" and
/// "{port}" are replaced with the profile's target and COM port.
/// </summary>
/// <param name="OptionLabel">The Settings check box that turns the reminder on or off.</param>
/// <param name="AfterDisconnect">Appended to the notification when a connection ends.</param>
public sealed record ConnectReminder(string OptionLabel, string Heading, string Text, string AfterDisconnect)
{
    internal string Format(string endpoint, string port) => Text.Replace("{endpoint}", endpoint).Replace("{port}", port);
}
