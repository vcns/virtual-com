using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Vcns.VirtualCom.Bridge;
using Vcns.VirtualCom.Bridge.Configuration;
using Vcns.VirtualCom.Bridge.Diagnostics;
using Vcns.VirtualCom.Bridge.Server;
using Vcns.VirtualCom.Bridge.VirtualPorts;

namespace Vcns.VirtualCom.Desktop;

/// <summary>
/// The whole tray application, for an edition's executable to run from its STA <c>Main</c>:
/// <code>[STAThread] static int Main(string[] args) => BridgeApp.Run(MyEdition, args);</code>
/// </summary>
public static class BridgeApp
{
    public static int Run(BridgeEdition edition, string[] args)
    {
        // Elevated helper mode: create or remove virtual ports, or apply server settings, then exit. No UI, no
        // single-instance lock.
        if (args.Length == 2 && args[0] == VirtualPortSetup.TaskArgument)
            return VirtualPortSetup.RunTask(args[1], edition);

        // Started by the service control manager: server mode, with no UI.
        if (args.Length == 1 && args[0] == BridgeService.Argument)
        {
            ServiceBase.Run(new BridgeService(edition));
            return 0;
        }

        // Before the single-instance check: taking over from another copy may need a dialog, and none of these can be
        // set once a window exists.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Dialogs.Caption = edition.ProductName;

        using var instance = new Mutex(initiallyOwned: true, $@"Local\{edition.DataFolderName}.Instance", out var isFirstInstance);
        var showSettingsEventName = $@"Local\{edition.DataFolderName}.ShowSettings";
        var exitEventName = $@"Local\{edition.DataFolderName}.Exit";
        string? tookOverFrom = null;
        if (!isFirstInstance && !TakeOver(instance, showSettingsEventName, exitEventName, out tookOverFrom))
            return 0;

        var localData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), edition.DataFolderName);
        var roamingData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), edition.DataFolderName);
        Log.Initialise(Path.Combine(localData, "logs"), edition.LogFilePrefix);

        Application.ThreadException += (_, e) =>
        {
            Log.Error("Unhandled UI exception", e.Exception);
            Dialogs.Error("Unexpected error", e.Exception.Message, e.Exception.ToString());
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        var version = (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Version;
        Log.Info($"Application start: {edition.ProductName} version {version}, {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}.");
        if (tookOverFrom is not null)
            Log.Info($"Took over from the copy that was running: {tookOverFrom}.");
        try
        {
            using var context = new TrayApplicationContext(new ConfigStore(Path.Combine(roamingData, "config.json")), edition);
            using var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, showSettingsEventName);
            using var exitRequest = new EventWaitHandle(false, EventResetMode.AutoReset, exitEventName);
            var registration = ThreadPool.RegisterWaitForSingleObject(showSettings,
                (_, _) => context.PostShowSettings(), null, Timeout.Infinite, executeOnlyOnce: false);
            // Another build taking over asks this one to exit, which closes connections and ports cleanly.
            var exitRegistration = ThreadPool.RegisterWaitForSingleObject(exitRequest,
                (_, _) => context.PostExit(), null, Timeout.Infinite, executeOnlyOnce: true);
            try
            {
                Application.Run(context);
            }
            finally
            {
                registration.Unregister(null);
                exitRegistration.Unregister(null);
            }
        }
        finally
        {
            Log.Info("Application exit.");
            Log.Shutdown();
        }
        return 0;
    }

    /// <summary>
    /// Another copy holds the single-instance lock. If it's this same program file, it's asked to open its Settings
    /// window. If it's a different one, typically an older version still running from another folder, the user can
    /// close it and start this one; otherwise starting the new file would only ever show the old copy's window.
    /// </summary>
    /// <param name="replaced">Which copy was closed, for the log.</param>
    /// <returns>True once the other copy has gone and this one holds the lock.</returns>
    private static bool TakeOver(Mutex instance, string showSettingsEventName, string exitEventName, out string? replaced)
    {
        replaced = null;
        var self = Environment.ProcessPath ?? "";
        var other = FindRunningCopy();
        if (other is null || string.Equals(other.Path, self, StringComparison.OrdinalIgnoreCase))
        {
            Signal(showSettingsEventName);
            return false;
        }

        var thisVersion = VersionOf(self);
        switch (Dialogs.TakeOver(other.Version, Path.GetDirectoryName(other.Path) ?? other.Path, thisVersion, Path.GetDirectoryName(self) ?? self))
        {
            case TakeOverChoice.UseRunningCopy:
                Signal(showSettingsEventName);
                return false;
            case TakeOverChoice.Cancel:
                return false;
        }

        // Newer copies exit cleanly when asked; one that doesn't listen (version 1.0) is stopped.
        var how = "it closed itself";
        try
        {
            using var process = Process.GetProcessById(other.ProcessId);
            if (!Signal(exitEventName) || !process.WaitForExit(TimeSpan.FromSeconds(15)))
            {
                process.Kill();
                process.WaitForExit(TimeSpan.FromSeconds(10));
                how = "it was stopped";
            }
        }
        catch (ArgumentException)
        {
            // It has already gone.
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Dialogs.Error("Couldn't close the running copy", $"{ex.Message}\n\nExit it from its tray icon, then start this one again.");
            return false;
        }

        try
        {
            if (!instance.WaitOne(TimeSpan.FromSeconds(10)))
            {
                Dialogs.Error("The running copy is still starting or closing", "Wait a moment, then start this one again.");
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // The stopped copy never released the lock; this one now holds it.
        }
        replaced = $"version {other.Version} from {other.Path} ({how})";
        return true;
    }

    private sealed record RunningCopy(int ProcessId, string Path, string Version);

    /// <summary>The other copy of this program in this Windows session; the service runs in another session.</summary>
    private static RunningCopy? FindRunningCopy()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                if (process.Id == current.Id || process.SessionId != current.SessionId)
                    continue;
                try
                {
                    var path = process.MainModule?.FileName ?? "";
                    return new RunningCopy(process.Id, path, VersionOf(path));
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Elevated (the port helper) or already exiting.
                }
            }
        }
        return null;
    }

    private static string VersionOf(string path)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "";
            var plus = version.IndexOf('+');
            return plus > 0 ? version[..plus] : version.Length > 0 ? version : "unknown";
        }
        catch (FileNotFoundException)
        {
            return "unknown";
        }
    }

    private static bool Signal(string eventName)
    {
        if (!EventWaitHandle.TryOpenExisting(eventName, out var signal))
            return false;
        using (signal)
            signal.Set();
        return true;
    }
}
