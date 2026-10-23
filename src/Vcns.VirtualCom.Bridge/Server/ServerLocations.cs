namespace Vcns.VirtualCom.Bridge.Server;

/// <summary>Where an edition's server mode keeps its files, and what its service and firewall rule are called.</summary>
internal sealed class ServerLocations(BridgeEdition edition, string? root = null)
{
    /// <summary>%ProgramData%\&lt;edition folder&gt;: administrators can change it, everyone can read it.</summary>
    public string Root { get; } = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), edition.DataFolderName);

    public string ConfigFile => Path.Combine(Root, "server.json");

    /// <summary>The one folder the service may write to.</summary>
    public string StateDirectory => Path.Combine(Root, "service");

    public string LogDirectory => Path.Combine(StateDirectory, "logs");

    public string StatusFile => Path.Combine(StateDirectory, "status.json");

    public string LogFilePrefix => edition.LogFilePrefix + "-server";

    public string ServiceName => edition.DataFolderName + "Server";

    public string ServiceDisplayName => $"{edition.ProductName} server";

    public string FirewallRuleName => $"{edition.ProductName} server";
}
