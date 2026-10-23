using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Vcns.VirtualCom.Bridge.Configuration;
using Vcns.VirtualCom.Bridge.Server;
using Vcns.VirtualCom;

namespace Vcns.VirtualCom.Bridge.VirtualPorts;

/// <summary>Changes that need administrator rights, made in one elevated run: virtual ports and server mode.</summary>
internal sealed class PortJob
{
    public List<string> CreateVcns { get; set; } = [];

    public List<string> RemoveVcns { get; set; } = [];

    public List<string> CreateCom0com { get; set; } = [];

    public List<int> RemoveCom0comPairs { get; set; } = [];

    public string? SetupcPath { get; set; }

    /// <summary>A server configuration to save and apply (service, firewall), or null to leave server mode alone.</summary>
    public ServerConfig? Server { get; set; }

    public bool IsEmpty => CreateVcns.Count + RemoveVcns.Count + CreateCom0com.Count + RemoveCom0comPairs.Count == 0 && Server is null;
}

internal sealed class PortJobResult
{
    public bool Succeeded { get; set; }

    public bool Declined { get; set; }

    public string Log { get; set; } = "";
}

/// <summary>
/// Creates and removes virtual ports. Driver changes need administrator rights, so the work runs in a second,
/// elevated copy of this executable: one UAC prompt per job. Starting and stopping connections never needs it.
/// </summary>
internal static class VirtualPortSetup
{
    public const string TaskArgument = "--port-task";

    private const int ErrorCancelled = 1223;

    /// <summary>Runs <paramref name="job"/> elevated and waits for it. Never throws for UAC refusal.</summary>
    public static async Task<PortJobResult> RunElevatedAsync(PortJob job)
    {
        var directory = Path.Combine(Path.GetTempPath(), "VcnsVirtualCom");
        Directory.CreateDirectory(directory);
        var jobFile = Path.Combine(directory, $"port-job-{Guid.NewGuid():N}.json");
        var resultFile = jobFile + ".result";
        await File.WriteAllTextAsync(jobFile, JsonSerializer.Serialize(job));
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't determine this program's path.");
            var start = new ProcessStartInfo(exe, $"{TaskArgument} \"{jobFile}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The elevated helper did not start.");
            await process.WaitForExitAsync();

            if (File.Exists(resultFile))
                return JsonSerializer.Deserialize<PortJobResult>(await File.ReadAllTextAsync(resultFile)) ?? new PortJobResult();
            return new PortJobResult { Log = $"The elevated helper exited with code {process.ExitCode} without reporting a result." };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new PortJobResult { Declined = true, Log = "Administrator approval was declined; no changes were made." };
        }
        finally
        {
            TryDelete(jobFile);
            TryDelete(resultFile);
        }
    }

    /// <summary>Entry point of the elevated helper process.</summary>
    public static int RunTask(string jobFile, BridgeEdition edition)
    {
        var log = new StringBuilder();
        var ok = true;
        try
        {
            var job = JsonSerializer.Deserialize<PortJob>(File.ReadAllText(jobFile))
                ?? throw new InvalidDataException("The job file is empty.");

            if (job.CreateVcns.Count > 0)
            {
                if (InstallBundledDriver(log))
                {
                    foreach (var port in job.CreateVcns)
                        ok &= CreateVcnsPort(port, log);
                }
                else
                {
                    ok = false;
                }
            }
            foreach (var port in job.RemoveVcns)
                ok &= RemoveVcnsPort(port, log);

            if (job.CreateCom0com.Count + job.RemoveCom0comPairs.Count > 0)
            {
                var setupc = Com0comSetup.FindSetupc(job.SetupcPath)
                    ?? throw new FileNotFoundException("com0com's setupc.exe was not found. Install com0com first.");
                foreach (var port in job.CreateCom0com)
                    ok &= Com0comSetup.CreatePair(setupc, port, log);
                foreach (var index in job.RemoveCom0comPairs)
                    ok &= Com0comSetup.RemovePair(setupc, index, log);
            }

            if (job.Server is { } server)
                ok &= ServerSetup.Apply(server, edition, log);
        }
        catch (Exception ex)
        {
            ok = false;
            log.AppendLine(ex.Message);
        }

        try
        {
            File.WriteAllText(jobFile + ".result", JsonSerializer.Serialize(new PortJobResult { Succeeded = ok, Log = log.ToString() }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 2;
        }
        return ok ? 0 : 1;
    }

    // Puts the embedded package in the driver store. Repeating it is harmless, and it upgrades existing ports when
    // this build carries a newer driver.
    private static bool InstallBundledDriver(StringBuilder log)
    {
        var directory = Path.Combine(Path.GetTempPath(), "VcnsVirtualCom", $"driver-{Guid.NewGuid():N}");
        try
        {
            var inf = DriverPackage.ExtractTo(directory);
            var reboot = VirtualComDevices.InstallDriver(inf);
            log.AppendLine(reboot ? "Virtual COM driver installed; Windows asks for a restart." : "Virtual COM driver installed.");
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            log.AppendLine($"Installing the virtual COM driver failed: {ex.Message}");
            return false;
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool CreateVcnsPort(string port, StringBuilder log)
    {
        if (!ConfigValidator.TryParseComNumber(port, out _))
        {
            log.AppendLine($"Refused to create invalid port name '{port}'.");
            return false;
        }
        try
        {
            var created = VirtualComDevices.Create(port.ToUpperInvariant());
            log.AppendLine(created.IsRunning
                ? $"{created.PortName} created ({created.InstanceId})."
                : $"{created.PortName} created, but Windows hasn't started it (problem code {created.ProblemCode}).");
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            log.AppendLine($"Creating {port} failed: {ex.Message}");
            return false;
        }
    }

    private static bool RemoveVcnsPort(string port, StringBuilder log)
    {
        try
        {
            VirtualComDevices.Remove(port);
            log.AppendLine($"{port} removed.");
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            log.AppendLine($"Removing {port} failed: {ex.Message}");
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
