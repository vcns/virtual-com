using System.ServiceProcess;
using Vcns.VirtualCom.Bridge.Configuration;

namespace Vcns.VirtualCom.Bridge.Server;

/// <summary>Server mode as the tray and Settings window see it: the saved settings, the service and its status file.</summary>
internal sealed record ServerSnapshot(ServerConfig Config, string? ConfigWarning, ServiceControllerStatus? Service, ServerStatus? Status)
{
    public static ServerSnapshot Read(ServerLocations locations)
    {
        var config = ServerConfigStore.Load(locations.ConfigFile, out var warning);
        ServiceControllerStatus? service = null;
        try
        {
            using var controller = new ServiceController(locations.ServiceName);
            service = controller.Status;
        }
        catch (InvalidOperationException)
        {
            // Not installed.
        }
        return new ServerSnapshot(config, warning, service, ServerStatus.TryRead(locations.StatusFile));
    }

    public bool IsRunning => Service == ServiceControllerStatus.Running;

    /// <summary>Worth showing in the tray: server mode is on, or its service is still installed.</summary>
    public bool IsConfigured => Config.Enabled || Service is not null;

    public string ServiceText => Service switch
    {
        null => "not installed",
        ServiceControllerStatus.Running => "running",
        ServiceControllerStatus.Stopped => "stopped",
        ServiceControllerStatus.StartPending => "starting",
        ServiceControllerStatus.StopPending => "stopping",
        { } other => other.ToString().ToLowerInvariant(),
    };

    /// <summary>The live status of a shared port, if the service is running and reports it.</summary>
    public PortStatus? StatusOf(string comPort) =>
        IsRunning ? Status?.Ports.FirstOrDefault(p => string.Equals(p.ComPort, comPort, StringComparison.OrdinalIgnoreCase)) : null;
}
