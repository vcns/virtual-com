using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using Vcns.VirtualCom.Bridge.Configuration;

namespace Vcns.VirtualCom.Bridge.Server;

/// <summary>
/// Suggests a TCP port for sharing a COM port: the base plus the COM number (COM3 on 5203 with base 5200), so the
/// numbers are easy to read and don't shift when other ports are added or removed. Ports already in use or reserved
/// on this PC are skipped.
/// </summary>
internal static partial class TcpPortSuggester
{
    [GeneratedRegex(@"^\s*(\d+)\s+(\d+)\b", RegexOptions.Multiline)]
    private static partial Regex RangeLine();

    public static int Suggest(string comPort, int basePort, IReadOnlySet<int> taken)
    {
        var start = ConfigValidator.TryParseComNumber(comPort, out var number) ? basePort + number : basePort + 1;
        for (var port = Math.Max(start, 1); port <= 65535; port++)
        {
            if (!taken.Contains(port))
                return port;
        }
        return 0;
    }

    /// <summary>TCP ports that something on this PC listens on, plus the ranges Windows reserves (Hyper-V and others).</summary>
    public static HashSet<int> TakenOnThisPc()
    {
        var taken = new HashSet<int>();
        try
        {
            foreach (var listener in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                taken.Add(listener.Port);
        }
        catch (NetworkInformationException)
        {
        }
        foreach (var (start, end) in ParseExcludedRanges(RunNetsh("int ipv4 show excludedportrange protocol=tcp")))
        {
            for (var port = start; port <= end; port++)
                taken.Add(port);
        }
        return taken;
    }

    /// <summary>Reads the "Start Port  End Port" table that netsh prints.</summary>
    internal static IEnumerable<(int Start, int End)> ParseExcludedRanges(string netshOutput)
    {
        foreach (Match match in RangeLine().Matches(netshOutput))
        {
            if (int.TryParse(match.Groups[1].Value, out var start) && int.TryParse(match.Groups[2].Value, out var end)
                && start is >= 1 and <= 65535 && end >= start && end <= 65535)
                yield return (start, end);
        }
    }

    private static string RunNetsh(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("netsh.exe", arguments)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
                return "";
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "";
        }
    }
}
