using System.Reflection;
using System.Security.Principal;
using System.ServiceProcess;
using Vcns.VirtualCom.Bridge.Configuration;
using Vcns.VirtualCom.Bridge.Diagnostics;

namespace Vcns.VirtualCom.Bridge.Server;

/// <summary>
/// The Windows service that runs server mode, so shared ports stay available with nobody logged in. It runs as
/// LocalService, reads server.json, and applies changes to it as soon as they're saved.
/// </summary>
internal sealed class BridgeService : ServiceBase
{
    public const string Argument = "--service";

    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(500);

    private readonly ServerLocations _locations;
    private readonly Timer _reload;
    private ServerHost? _host;
    private FileSystemWatcher? _watcher;

    public BridgeService(BridgeEdition edition)
    {
        _locations = new ServerLocations(edition);
        ServiceName = _locations.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = false;
        _reload = new Timer(_ => Reload());
    }

    protected override void OnStart(string[] args)
    {
        Log.Initialise(_locations.LogDirectory, _locations.LogFilePrefix);
        var version = (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Version;
        Log.Info($"{_locations.ServiceDisplayName} starting: version {version}, account {WindowsIdentity.GetCurrent().Name}.");

        _host = new ServerHost(statusFile: _locations.StatusFile);
        Reload();
        _watcher = new FileSystemWatcher(_locations.Root, Path.GetFileName(_locations.ConfigFile))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        // A save is a temporary file renamed over server.json; wait for it to settle, then reload once.
        FileSystemEventHandler changed = (_, _) => _reload.Change(ReloadDelay, Timeout.InfiniteTimeSpan);
        _watcher.Changed += changed;
        _watcher.Created += changed;
        _watcher.Deleted += changed;
        _watcher.Renamed += (_, _) => _reload.Change(ReloadDelay, Timeout.InfiniteTimeSpan);
        _watcher.EnableRaisingEvents = true;
    }

    private void Reload()
    {
        var host = _host;
        if (host is null)
            return;
        ServerConfig config;
        if (!IsTrusted(_locations.ConfigFile, out var why))
        {
            Log.Error($"Ignoring {_locations.ConfigFile}: {why} Save the server settings again from the Settings window.");
            config = new ServerConfig();
        }
        else
        {
            config = ServerConfigStore.Load(_locations.ConfigFile, out var warning);
            if (warning is not null)
                Log.Error(warning + " No ports are shared until it's fixed.");
        }
        try
        {
            host.ApplyAsync(config).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error("Applying the server configuration failed", ex);
        }
    }

    /// <summary>Only a file owned by administrators or the system is obeyed, so a standard user can't plant one.</summary>
    private static bool IsTrusted(string path, out string why)
    {
        why = "";
        if (!File.Exists(path))
            return true;
        try
        {
            var owner = new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is not null && (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid)))
                return true;
            why = $"it is owned by {owner?.Translate(typeof(NTAccount))}, not by administrators.";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or IdentityNotMappedException)
        {
            why = $"its owner can't be checked ({ex.Message}).";
            return false;
        }
    }

    protected override void OnStop() => Shutdown();

    protected override void OnShutdown() => Shutdown();

    private void Shutdown()
    {
        _watcher?.Dispose();
        _watcher = null;
        _reload.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var host = _host;
        _host = null;
        host?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Log.Info($"{_locations.ServiceDisplayName} stopped.");
        Log.Shutdown();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _reload.Dispose();
        base.Dispose(disposing);
    }
}
