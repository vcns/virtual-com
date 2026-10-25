using Vcns.VirtualCom.Bridge;
using Vcns.VirtualCom.Bridge.Configuration;

namespace Vcns.VirtualCom.Desktop;

internal enum TakeOverChoice
{
    TakeOver,
    UseRunningCopy,
    Cancel,
}

/// <summary>
/// Task dialogs. A tray application has no window of its own, so when no owner is given the dialog is parented to a
/// hidden top-most form; otherwise it can open behind other windows and go unnoticed.
/// </summary>
internal static class Dialogs
{
    /// <summary>The window title of every dialog: the edition's product name.</summary>
    public static string Caption { get; set; } = BridgeEdition.Generic.ProductName;

    public static void Error(string heading, string text, string? details = null, IWin32Window? owner = null) =>
        Show(new TaskDialogPage
        {
            Caption = Caption,
            Heading = heading,
            Text = text,
            Icon = TaskDialogIcon.Error,
            Buttons = { TaskDialogButton.OK },
            Expander = details is null ? null : new TaskDialogExpander(details) { CollapsedButtonText = "Show details" },
        }, owner);

    public static void Info(string heading, string text, IWin32Window? owner = null) =>
        Show(new TaskDialogPage
        {
            Caption = Caption,
            Heading = heading,
            Text = text,
            Icon = TaskDialogIcon.Information,
            Buttons = { TaskDialogButton.OK },
        }, owner);

    public static bool Confirm(string heading, string text, string confirmText, bool requiresAdmin = false, IWin32Window? owner = null)
    {
        var confirm = new TaskDialogButton(confirmText) { ShowShieldIcon = requiresAdmin };
        var page = new TaskDialogPage
        {
            Caption = Caption,
            Heading = heading,
            Text = text,
            Icon = TaskDialogIcon.Information,
            Buttons = { confirm, new TaskDialogButton("Not now") },
            DefaultButton = confirm,
        };
        return Show(page, owner) == confirm;
    }

    /// <returns>Whether to connect, and whether the user asked not to be reminded again.</returns>
    public static (bool Proceed, bool DontRemind) ConnectReminder(ConnectReminder reminder, ConnectionProfile profile)
    {
        var connect = new TaskDialogButton("Connect");
        var page = new TaskDialogPage
        {
            Caption = Caption,
            Heading = reminder.Heading,
            Text = reminder.Format(profile.Endpoint, profile.LocalComPort),
            Icon = TaskDialogIcon.Warning,
            Buttons = { connect, TaskDialogButton.Cancel },
            DefaultButton = connect,
            Verification = new TaskDialogVerificationCheckBox("Don't show this reminder again"),
        };
        var result = Show(page, null);
        return (result == connect, page.Verification.Checked);
    }

    /// <summary>Asked when this program starts while a different copy (another file or version) is already running.</summary>
    public static TakeOverChoice TakeOver(string runningVersion, string runningFolder, string thisVersion, string thisFolder)
    {
        var takeOver = new TaskDialogCommandLinkButton("Close it and start this copy", $"Version {thisVersion}, from {thisFolder}");
        var useRunning = new TaskDialogCommandLinkButton("Use the running copy", "Opens its Settings window");
        var page = new TaskDialogPage
        {
            Caption = Caption,
            Heading = $"{Caption} {runningVersion} is already running",
            Text = $"Only one copy can run at a time. The one running is version {runningVersion}, from {runningFolder}.\n\n"
                + "Closing it ends any connections it has open. If it has unsaved settings, it asks about them first.",
            Icon = TaskDialogIcon.Information,
            Buttons = { takeOver, useRunning, TaskDialogButton.Cancel },
            DefaultButton = takeOver,
        };
        var result = Show(page, null);
        return result == takeOver ? TakeOverChoice.TakeOver : result == useRunning ? TakeOverChoice.UseRunningCopy : TakeOverChoice.Cancel;
    }

    private static TaskDialogButton Show(TaskDialogPage page, IWin32Window? owner)
    {
        if (owner is not null)
            return TaskDialog.ShowDialog(owner, page, TaskDialogStartupLocation.CenterOwner);

        using var host = new Form
        {
            ShowInTaskbar = false,
            TopMost = true,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(1, 1),
        };
        host.Show();
        host.Activate();
        return TaskDialog.ShowDialog(host, page, TaskDialogStartupLocation.CenterScreen);
    }
}
