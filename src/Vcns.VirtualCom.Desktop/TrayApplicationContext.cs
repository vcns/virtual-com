using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;
using Vcns.VirtualCom.Bridge;
using Vcns.VirtualCom.Bridge.Configuration;
using Vcns.VirtualCom.Bridge.Diagnostics;
using Vcns.VirtualCom.Bridge.Transport;
using Vcns.VirtualCom.Bridge.VirtualPorts;

namespace Vcns.VirtualCom.Desktop;

/// <summary>The notification-area icon, its menu, and the connection status shown to the user.</summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly BridgeEdition _edition;
    private readonly ConfigStore _store;
    private readonly SessionManager _sessions;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new() { ShowItemToolTips = true };
    private readonly Control _ui = new();
    private readonly Dictionary<TrayStatus, Icon> _trayIcons = [];
    private readonly Dictionary<TrayStatus, Bitmap> _dots = [];
    private readonly HashSet<ProxySession> _announcedConnected = [];
    private readonly HashSet<ProxySession> _announcedEnded = [];
    private readonly Icon _windowIcon;
    private AppConfig _config;
    private SettingsForm? _settings;
    private bool _lastAttemptFailed;
    private bool _exiting;

    public TrayApplicationContext(ConfigStore store, BridgeEdition edition)
    {
        // A window-less control on the UI thread, used to marshal session events from the thread pool.
        _ = _ui.Handle;
        _edition = edition;
        _store = store;
        _config = store.Load(out var loadWarning);
        _sessions = new SessionManager(() => _config.Options, edition);
        _sessions.SessionChanged += session => Post(() => OnSessionChanged(session));

        var iconSize = SystemInformation.SmallIconSize.Width;
        var dotSize = _menu.LogicalToDeviceUnits(16);
        foreach (var status in Enum.GetValues<TrayStatus>())
        {
            _trayIcons[status] = StatusIcons.CreateIcon(status, iconSize);
            _dots[status] = StatusIcons.CreateDot(status, dotSize);
        }
        _windowIcon = StatusIcons.CreateIcon(null, 32);

        _menu.Opening += (_, e) =>
        {
            BuildMenu();
            e.Cancel = false;
        };
        BuildMenu();

        _tray = new NotifyIcon { ContextMenuStrip = _menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowSettings();
        UpdateTrayStatus();

        SystemEvents.SessionEnding += OnWindowsSessionEnding;

        if (loadWarning is not null)
            Post(() => Dialogs.Error("Configuration problem", loadWarning));
    }

    /// <summary>Opens the settings window from any thread (used when the program is launched a second time).</summary>
    public void PostShowSettings() => Post(ShowSettings);

    public void ShowSettings()
    {
        if (_exiting)
            return;
        if (_settings is { IsDisposed: false })
        {
            if (_settings.WindowState == FormWindowState.Minimized)
                _settings.WindowState = FormWindowState.Normal;
            _settings.Activate();
            return;
        }

        var form = new SettingsForm(_config.Clone(), _store, _sessions, OnConfigSaved, _windowIcon, _edition);
        form.FormClosed += (_, _) =>
        {
            if (_settings == form)
                _settings = null;
        };
        _settings = form;
        form.Show();
        form.Activate();
    }

    private void OnConfigSaved(AppConfig config)
    {
        _config = config;
        UpdateTrayStatus();
    }

    private void BuildMenu()
    {
        var items = new List<ToolStripItem>();

        var settings = new ToolStripMenuItem("Settings");
        settings.DropDownItems.Add(new ToolStripMenuItem("GUI…", null, (_, _) => ShowSettings()) { Enabled = !_exiting });
        settings.DropDownItems.Add(new ToolStripSeparator());
        settings.DropDownItems.Add(new ToolStripMenuItem("Open log file", null, (_, _) => OpenLogFile()));
        settings.DropDownItems.Add(new ToolStripMenuItem("Open log folder", null, (_, _) => OpenFolder(Log.Directory)));
        settings.DropDownItems.Add(new ToolStripMenuItem("Open configuration folder", null,
            (_, _) => OpenFolder(Path.GetDirectoryName(_store.FilePath)!)));
        items.Add(settings);

        var start = new ToolStripMenuItem("Start");
        var profiles = _config.Profiles.Where(p => p.Enabled).OrderBy(p => ComOrder(p.LocalComPort)).ToList();
        if (profiles.Count == 0)
            start.DropDownItems.Add(new ToolStripMenuItem("No enabled profiles — add one in Settings → GUI") { Enabled = false });
        foreach (var profile in profiles)
        {
            var session = _sessions.Find(profile.Id);
            var last = session is null ? _sessions.GetLastOutcome(profile.Id) : null;
            var failed = last is not null && last.Outcome != SessionOutcome.StoppedByUser;
            var label = session is not null ? StateLabel(session) : last is not null ? OutcomeLabel(last.Outcome) : "Stopped";
            var id = profile.Id;
            start.DropDownItems.Add(new ToolStripMenuItem($"{profile.LocalComPort} → {profile.Endpoint}    ({label})", null, (_, _) => StartProfile(id))
            {
                Enabled = session is null && !_exiting,
                Image = _dots[session is not null ? StatusOf(session) : failed ? TrayStatus.Error : TrayStatus.Idle],
                ToolTipText = failed ? last!.Message : null,
            });
        }
        items.Add(start);

        var active = _sessions.ActiveSessions.OrderBy(s => ComOrder(s.Profile.LocalComPort)).ToList();
        if (active.Count > 0)
        {
            items.Add(new ToolStripSeparator());
            foreach (var session in active)
            {
                var id = session.Profile.Id;
                var item = new ToolStripMenuItem($"{session.Profile.LocalComPort} — {StateLabel(session)}") { Image = _dots[StatusOf(session)] };
                item.DropDownItems.Add(new ToolStripMenuItem(session.WasConnected ? "Disconnect" : "Cancel", null, (_, _) => _sessions.Stop(id))
                {
                    Enabled = session.State is SessionState.Starting or SessionState.Connected,
                });
                item.DropDownItems.Add(new ToolStripSeparator());
                item.DropDownItems.Add(InfoItem($"Target: {session.Profile.Endpoint} via {session.PortDescription}"));
                item.DropDownItems.Add(InfoItem(session.StatusText));
                if (session.DescribePeer() is { } peer)
                    item.DropDownItems.Add(InfoItem(peer));
                if (session.ConnectedAt is { } since)
                {
                    item.DropDownItems.Add(InfoItem($"Connected since {since:HH:mm:ss} ({FormatDuration(DateTimeOffset.Now - since)})"));
                    item.DropDownItems.Add(InfoItem(
                        $"Sent to {_edition.DeviceNoun} {FormatBytes(session.BytesToRemote)} · Received {FormatBytes(session.BytesFromRemote)}"));
                }
                items.Add(item);
            }
        }

        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Exit()) { Enabled = !_exiting });

        _menu.SuspendLayout();
        _menu.Items.Clear();
        _menu.Items.AddRange(items.ToArray());
        _menu.ResumeLayout();
    }

    private void StartProfile(Guid profileId)
    {
        var profile = _config.Profiles.FirstOrDefault(p => p.Id == profileId && p.Enabled);
        if (profile is null || _exiting)
            return;

        var errors = ConfigValidator.ValidateProfile(profile);
        if (errors.Count > 0)
        {
            Dialogs.Error($"Can't start {profile.LocalComPort}", string.Join("\n", errors) + "\n\nFix the profile in Settings → GUI.");
            return;
        }

        if (!PortTarget.TryResolve(profile, PortInventory.Capture(includeDeviceScan: false), out var target, out var problem))
        {
            Dialogs.Error($"Can't start {profile.LocalComPort}", problem);
            return;
        }

        if (_edition.ConnectReminder is { } reminder && _config.Options.ShowConnectReminder)
        {
            var (proceed, dontRemind) = Dialogs.ConnectReminder(reminder, profile);
            if (dontRemind)
                SaveReminderPreference();
            if (!proceed)
                return;
        }

        try
        {
            _lastAttemptFailed = false;
            _sessions.Start(profile, target);
        }
        catch (StartRejectedException ex)
        {
            Dialogs.Error($"Can't start {profile.LocalComPort}", ex.Message);
        }
        UpdateTrayStatus();
    }

    private void SaveReminderPreference()
    {
        _config.Options.ShowConnectReminder = false;
        try
        {
            _store.Save(_config.Clone());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigValidationException)
        {
            Log.Error("Could not save the reminder preference", ex);
        }
    }

    private void OnSessionChanged(ProxySession session)
    {
        var port = session.Profile.LocalComPort;
        if (!_exiting && session.State == SessionState.Connected && _announcedConnected.Add(session))
        {
            _tray.ShowBalloonTip(4000, $"{port} connected",
                $"{port} is connected to {session.Profile.Endpoint}. {_edition.SerialApplicationAtStart} can use {port} now.", ToolTipIcon.Info);
        }

        if (session.IsEnded && _announcedEnded.Add(session) && !_exiting)
        {
            var resume = _edition.ConnectReminder?.AfterDisconnect ?? "";
            if (session.Outcome == SessionOutcome.StoppedByUser)
            {
                if (session.WasConnected)
                    _tray.ShowBalloonTip(4000, $"{port} disconnected", $"The connection to {session.Profile.Endpoint} is closed. {resume}".TrimEnd(), ToolTipIcon.Info);
            }
            else if (!session.WasConnected)
            {
                _lastAttemptFailed = true;
                UpdateTrayStatus();
                Dialogs.Error($"{port} could not connect", session.OutcomeMessage ?? "The connection failed.");
            }
            else
            {
                _lastAttemptFailed = true;
                _tray.ShowBalloonTip(8000, $"{port} disconnected", $"{session.OutcomeMessage} {resume}".TrimEnd(), ToolTipIcon.Warning);
            }
        }
        UpdateTrayStatus();
    }

    private void UpdateTrayStatus()
    {
        if (_tray is null)
            return;
        var active = _sessions.ActiveSessions;
        var connected = active.Count(s => s.State == SessionState.Connected);
        var status = connected > 0 ? TrayStatus.Connected
            : active.Count > 0 ? TrayStatus.Starting
            : _lastAttemptFailed ? TrayStatus.Error
            : TrayStatus.Idle;

        var text = active.Count switch
        {
            0 when _lastAttemptFailed => $"{_edition.ProductName}\nLast connection attempt failed",
            0 => $"{_edition.ProductName}\nNo active connections",
            1 => $"{_edition.ProductName}\n{active[0].Profile.LocalComPort} {StateLabel(active[0]).ToLowerInvariant()}: {active[0].Profile.Endpoint}",
            _ => $"{_edition.ProductName}\n{connected} connected, {active.Count - connected} starting or closing",
        };
        _tray.Icon = _trayIcons[status];
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    private async void Exit()
    {
        try
        {
            if (_exiting)
                return;
            if (_settings is { IsDisposed: false } settings)
            {
                settings.Close();
                if (!settings.IsDisposed)
                    return;
            }

            _exiting = true;
            var count = _sessions.ActiveSessions.Count;
            Log.Info(count == 0 ? "Exit requested." : $"Exit requested; closing {count} active session(s).");
            _tray.Text = $"{_edition.ProductName}\nClosing…";
            await _sessions.StopAllAsync(TimeSpan.FromSeconds(5));
            _tray.Visible = false;
            ExitThread();
        }
        catch (Exception ex)
        {
            Log.Error("Error while exiting", ex);
            ExitThread();
        }
    }

    private void OnWindowsSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        Log.Info($"Windows session ending ({e.Reason}); closing active sessions.");
        // Run on the thread pool: this handler is on the UI thread, which the awaits must not need.
        Task.Run(() => _sessions.StopAllAsync(TimeSpan.FromSeconds(3))).Wait();
    }

    private void Post(Action action)
    {
        if (_ui.IsDisposed || !_ui.IsHandleCreated)
            return;
        try
        {
            _ui.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // The UI is shutting down.
        }
    }

    private static void OpenLogFile()
    {
        var file = Log.CurrentFile;
        if (!File.Exists(file))
            Log.Info("Log file opened from the tray menu.");
        try
        {
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            Process.Start("notepad.exe", $"\"{file}\"");
        }
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start("explorer.exe", $"\"{path}\"");
    }

    private static ToolStripMenuItem InfoItem(string text) => new(text) { Enabled = false };

    private static TrayStatus StatusOf(ProxySession session) => session.State switch
    {
        SessionState.Connected => TrayStatus.Connected,
        SessionState.Failed => TrayStatus.Error,
        SessionState.Stopped => TrayStatus.Idle,
        _ => TrayStatus.Starting,
    };

    private static string StateLabel(ProxySession session) => session.State switch
    {
        SessionState.Starting => "Starting…",
        SessionState.Connected => "Connected",
        SessionState.Stopping => session.WasConnected ? "Disconnecting…" : "Cancelling…",
        SessionState.Stopped => "Stopped",
        _ => "Failed",
    };

    private string OutcomeLabel(SessionOutcome outcome) => outcome switch
    {
        SessionOutcome.StoppedByUser => "Stopped",
        SessionOutcome.InUse => "Last attempt: in use",
        SessionOutcome.Refused => "Last attempt: refused",
        SessionOutcome.TimedOut => "Last attempt: timed out",
        SessionOutcome.HostNotFound => "Last attempt: host not found",
        SessionOutcome.Unreachable => "Last attempt: unreachable",
        SessionOutcome.VirtualPortError => "Last attempt: virtual port error",
        SessionOutcome.ClosedByRemote => $"Closed by {_edition.DeviceNoun}",
        SessionOutcome.ConnectionLost => "Connection lost",
        _ => "Last attempt failed",
    };

    private static int ComOrder(string port) => ConfigValidator.TryParseComNumber(port, out var n) ? n : int.MaxValue;

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / (1024.0 * 1024):0.0} MB",
    };

    private static string FormatDuration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes:00}m" : $"{span.Minutes}m {span.Seconds:00}s";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.SessionEnding -= OnWindowsSessionEnding;
            _tray.Visible = false;
            _tray.Dispose();
            _menu.Dispose();
            foreach (var icon in _trayIcons.Values)
                icon.Dispose();
            foreach (var dot in _dots.Values)
                dot.Dispose();
            _windowIcon.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}
