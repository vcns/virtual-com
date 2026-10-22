using NovaStarProxy.Configuration;

namespace NovaStarProxy.UI;

/// <summary>
/// Task dialogs. A tray application has no window of its own, so when no owner is given the dialog is parented to a
/// hidden top-most form; otherwise it can open behind other windows and go unnoticed.
/// </summary>
internal static class Dialogs
{
    private const string Caption = "NovaStar Proxy";

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
    public static (bool Proceed, bool DontRemind) ZabbixReminder(ConnectionProfile profile)
    {
        var connect = new TaskDialogButton("Connect");
        var page = new TaskDialogPage
        {
            Caption = Caption,
            Heading = "Pause Zabbix polling first",
            Text = $"The controller at {profile.Endpoint} accepts only one TCP session at a time. "
                + "Pause Zabbix polling for this controller before connecting, and resume it after you disconnect "
                + $"{profile.LocalComPort}.\n\nIf Zabbix is still polling, the connection will be reported as \"in use\".",
            Icon = TaskDialogIcon.Warning,
            Buttons = { connect, TaskDialogButton.Cancel },
            DefaultButton = connect,
            Verification = new TaskDialogVerificationCheckBox("Don't show this reminder again"),
        };
        var result = Show(page, null);
        return (result == connect, page.Verification.Checked);
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
