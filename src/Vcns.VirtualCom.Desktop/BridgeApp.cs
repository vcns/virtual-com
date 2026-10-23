using System.Reflection;
using System.Runtime.InteropServices;
using Vcns.VirtualCom.Bridge;
using Vcns.VirtualCom.Bridge.Configuration;
using Vcns.VirtualCom.Bridge.Diagnostics;
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
        // Elevated helper mode: create or remove virtual ports, then exit. No UI, no single-instance lock.
        if (args.Length == 2 && args[0] == VirtualPortSetup.TaskArgument)
            return VirtualPortSetup.RunTask(args[1]);

        using var instance = new Mutex(initiallyOwned: true, $@"Local\{edition.DataFolderName}.Instance", out var isFirstInstance);
        var showSettingsEventName = $@"Local\{edition.DataFolderName}.ShowSettings";
        if (!isFirstInstance)
        {
            // Already running in the tray: ask that copy to open its settings window instead.
            if (EventWaitHandle.TryOpenExisting(showSettingsEventName, out var signal))
            {
                using (signal)
                    signal.Set();
            }
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Dialogs.Caption = edition.ProductName;

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
        try
        {
            using var context = new TrayApplicationContext(new ConfigStore(Path.Combine(roamingData, "config.json")), edition);
            using var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, showSettingsEventName);
            var registration = ThreadPool.RegisterWaitForSingleObject(showSettings,
                (_, _) => context.PostShowSettings(), null, Timeout.Infinite, executeOnlyOnce: false);
            try
            {
                Application.Run(context);
            }
            finally
            {
                registration.Unregister(null);
            }
        }
        finally
        {
            Log.Info("Application exit.");
            Log.Shutdown();
        }
        return 0;
    }
}
