using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Vcns.VirtualCom.Bridge.Configuration;

namespace Vcns.VirtualCom.Bridge.VirtualPorts;

/// <summary>
/// com0com support (the fallback virtual port driver): finding its setup tool and creating or removing port pairs.
/// Pair changes need administrator rights and run in the elevated helper (see <see cref="VirtualPortSetup"/>).
/// </summary>
internal static class Com0comSetup
{
    public const string DownloadUrl = "https://sourceforge.net/projects/com0com/files/com0com/2.2.2.0/";

    private static readonly TimeSpan SetupcTimeout = TimeSpan.FromMinutes(2);

    public static string? FindSetupc(string? overridePath)
    {
        if (overridePath is not null)
            return File.Exists(overridePath) ? Path.GetFullPath(overridePath) : null;

        return CandidateDirectories()
            .Select(dir => Path.Combine(dir, "setupc.exe"))
            .FirstOrDefault(File.Exists);
    }

    internal static bool CreatePair(string setupc, string port, StringBuilder log)
    {
        if (!ConfigValidator.TryParseComNumber(port, out _))
        {
            log.AppendLine($"Refused to create invalid port name '{port}'.");
            return false;
        }
        port = port.ToUpperInvariant();
        if (PortInventory.Capture(includeDeviceScan: false).PairOf(port) is not null)
        {
            log.AppendLine($"{port} already exists.");
            return true;
        }

        // Side A is the port the serial application sees. Side B, opened by the proxy, is hidden from port lists so the
        // application doesn't offer or probe it. AllDataBits (com0com 3.x only) passes all 8 bits whatever the data-bits
        // setting; 2.2.2.0 rejects it, so fall back to options that version understands.
        string[][] attempts =
        [
            [$"PortName={port},AllDataBits=yes", "HiddenMode=yes,AllDataBits=yes"],
            [$"PortName={port}", "HiddenMode=yes"],
            [$"PortName={port}", "-"],
        ];
        foreach (var options in attempts)
        {
            var exitCode = RunSetupc(setupc, log, ["install", .. options]);
            var created = exitCode == 0
                ? WaitFor(() => PortInventory.Capture(includeDeviceScan: false).PairOf(port) is not null)
                : PortInventory.Capture(includeDeviceScan: false).PairOf(port) is not null;
            if (!created)
                continue;

            // The pair exists in com0com's settings; it only appears in SERIALCOMM once Windows has loaded the driver.
            if (!WaitFor(() => PortInventory.Capture(includeDeviceScan: false).IsRunning(port)))
                log.AppendLine($"{port} was created, but Windows has not started it. Check Device Manager for a warning on the com0com ports.");
            return true;
        }

        log.AppendLine($"{port} was not created.");
        return false;
    }

    internal static bool RemovePair(string setupc, int index, StringBuilder log)
    {
        if (index is < 0 or > 999)
        {
            log.AppendLine($"Refused to remove invalid pair number {index}.");
            return false;
        }
        _ = RunSetupc(setupc, log, "remove", index.ToString());
        if (WaitFor(() => PortInventory.Capture(includeDeviceScan: false).Pairs.All(p => p.Index != index)))
            return true;
        log.AppendLine($"com0com pair {index} was not removed.");
        return false;
    }

    /// <returns>setupc's exit code, or -1 if it had to be stopped.</returns>
    private static int RunSetupc(string setupc, StringBuilder log, params string[] args)
    {
        var start = new ProcessStartInfo(setupc)
        {
            // setupc locates the driver's INF files relative to its own folder.
            WorkingDirectory = Path.GetDirectoryName(setupc)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--silent");
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        log.AppendLine("> setupc " + string.Join(' ', start.ArgumentList));
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(SetupcTimeout))
        {
            process.Kill(entireProcessTree: true);
            log.AppendLine("setupc did not finish in time and was stopped.");
            return -1;
        }
        log.Append(stdout.Result).Append(stderr.Result);
        log.AppendLine($"(setupc exit code {process.ExitCode})");
        return process.ExitCode;
    }

    private static bool WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            if (condition())
                return true;
            if (DateTime.UtcNow > deadline)
                return false;
            Thread.Sleep(500);
        }
    }

    private static List<string> CandidateDirectories()
    {
        var directories = new List<string>();
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using (var key = hklm.OpenSubKey(@"SOFTWARE\com0com"))
                {
                    if (key?.GetValue("Install_Dir") is string dir)
                        directories.Add(dir);
                }
                using (var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\com0com"))
                {
                    if (key?.GetValue("UninstallString") is string uninstall && Path.GetDirectoryName(uninstall.Trim('"')) is { } dir)
                        directories.Add(dir);
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
        directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "com0com"));
        directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "com0com"));
        return directories;
    }
}
