using System.Reflection;
using System.Runtime.InteropServices;
using NovaStarProxy.Configuration;
using NovaStarProxy.Diagnostics;
using NovaStarProxy.UI;
using NovaStarProxy.VirtualPorts;

namespace NovaStarProxy;

internal static class Program
{
    private const string InstanceMutexName = @"Local\NovaStarProxy.Instance";
    private const string ShowSettingsEventName = @"Local\NovaStarProxy.ShowSettings";

    [STAThread]
    private static int Main(string[] args)
    {
        // Elevated helper mode: create or remove virtual ports, then exit. No UI, no single-instance lock.
        if (args.Length == 2 && args[0] == VirtualPortSetup.TaskArgument)
            return VirtualPortSetup.RunTask(args[1]);

        using var instance = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Already running in the tray: ask that copy to open its settings window instead.
            if (EventWaitHandle.TryOpenExisting(ShowSettingsEventName, out var signal))
            {
                using (signal)
                    signal.Set();
            }
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        var localData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaStarProxy");
        var roamingData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NovaStarProxy");
        Log.Initialise(Path.Combine(localData, "logs"));

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

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        Log.Info($"Application start: version {version}, {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}.");
        try
        {
            using var context = new TrayApplicationContext(new ConfigStore(Path.Combine(roamingData, "config.json")));
            using var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);
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
