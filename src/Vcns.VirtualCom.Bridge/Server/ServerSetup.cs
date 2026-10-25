using System.Diagnostics;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using Vcns.VirtualCom.Bridge.Configuration;

namespace Vcns.VirtualCom.Bridge.Server;

/// <summary>
/// Applies a server configuration: saves it, secures its folder, sets the firewall rules and installs, starts or
/// removes the service. Runs in the elevated helper.
/// </summary>
internal static class ServerSetup
{
    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(30);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier LocalService = new(WellKnownSidType.LocalServiceSid, null);

    public static bool Apply(ServerConfig config, BridgeEdition edition, StringBuilder log)
    {
        var locations = new ServerLocations(edition);
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't determine this program's path.");
        // The service runs a program file, so that file must be one standard users can't replace. Run from anywhere
        // else (Downloads, a build folder), the service gets its own copy in Program Files.
        var serviceExe = exe;
        if (config.Enabled && !IsProtectedLocation(exe))
        {
            serviceExe = InstallServiceCopy(exe, locations, log);
            if (serviceExe is null)
                return false;
        }

        SecureFolders(locations, log);
        ServerConfigStore.Save(locations.ConfigFile, config);
        log.AppendLine(config.Enabled
            ? $"Server settings saved: {config.Ports.Count(p => p.Enabled)} shared port(s)."
            : "Server settings saved: server mode is off.");

        var ok = ConfigureFirewall(config, locations, serviceExe, log);
        if (config.Enabled)
        {
            ok &= InstallAndStartService(locations, serviceExe, log);
        }
        else
        {
            var removed = RemoveService(locations, log);
            ok &= removed;
            if (removed)
                RemoveServiceCopy(exe, locations, log);
        }
        return ok;
    }

    private static bool IsProtectedLocation(string exe)
    {
        var path = Path.GetFullPath(exe);
        return new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows }
            .Select(Environment.GetFolderPath)
            .Where(folder => folder.Length > 0)
            .Any(folder => path.StartsWith(folder.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Puts a copy of this program in Program Files for the service and returns its path, or null if that failed.
    /// A copy that's already identical is kept; a different one (an older version) is replaced, stopping the service
    /// first because it holds the file open.
    /// </summary>
    private static string? InstallServiceCopy(string exe, ServerLocations locations, StringBuilder log)
    {
        var target = Path.Combine(locations.ProgramDirectory, Path.GetFileName(exe));
        try
        {
            if (File.Exists(target) && SameContent(exe, target))
            {
                log.AppendLine($"The service uses the copy of this program in {locations.ProgramDirectory}.");
                return target;
            }

            var replacing = File.Exists(target);
            var marker = Path.Combine(locations.ProgramDirectory, ServiceCopyMarker);
            // A program someone installed here by hand is updated for the service, but stays theirs: only a copy
            // this method created is marked for removal when server mode is switched off.
            var ours = !replacing || File.Exists(marker);
            if (replacing)
                StopService(locations, log);
            var folder = new DirectoryInfo(locations.ProgramDirectory);
            folder.Create();
            SecureProgramFolder(folder);
            // Copy beside the target, then swap, so a failed copy never leaves a half-written program for the service.
            var partial = target + ".new";
            File.Copy(exe, partial, overwrite: true);
            MoveWhenReleased(partial, target);
            if (ours)
            {
                File.WriteAllText(marker,
                    $"This copy of {Path.GetFileName(exe)} runs the {locations.ServiceDisplayName} service. Turning server mode off removes it.\r\n");
            }
            log.AppendLine(replacing
                ? $"Updated the service's copy of this program in {locations.ProgramDirectory}."
                : $"Copied this program to {locations.ProgramDirectory} for the service, because it's running from {Path.GetDirectoryName(exe)}, "
                    + "a folder standard users can change.");
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.AppendLine($"Couldn't copy this program to {locations.ProgramDirectory} for the service: {ex.Message} Nothing else was changed.");
            return null;
        }
    }

    /// <summary>Removes the copy <see cref="InstallServiceCopy"/> made, but never a copy someone put there and runs.</summary>
    private static void RemoveServiceCopy(string exe, ServerLocations locations, StringBuilder log)
    {
        var marker = Path.Combine(locations.ProgramDirectory, ServiceCopyMarker);
        var target = Path.Combine(locations.ProgramDirectory, Path.GetFileName(exe));
        if (!File.Exists(marker) || string.Equals(Path.GetFullPath(exe), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            File.Delete(target);
            File.Delete(marker);
            if (!Directory.EnumerateFileSystemEntries(locations.ProgramDirectory).Any())
                Directory.Delete(locations.ProgramDirectory);
            log.AppendLine($"Removed the service's copy of this program from {locations.ProgramDirectory}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.AppendLine($"The service's copy in {locations.ProgramDirectory} couldn't be removed: {ex.Message}");
        }
    }

    private const string ServiceCopyMarker = "service-copy.txt";

    // A stopped service's process can hold its program file open for a moment after Windows reports it stopped.
    private static void MoveWhenReleased(string source, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, target, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(250);
            }
        }
    }

    private static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length)
            return false;
        using var first = File.OpenRead(a);
        using var second = File.OpenRead(b);
        return System.Security.Cryptography.SHA256.HashData(first).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(second));
    }

    /// <summary>Only administrators and the system may change the program folder; everyone else may only run it.</summary>
    private static void SecureProgramFolder(DirectoryInfo folder) => folder.SetAccessControl(AdministratorsOnly());

    /// <summary>Administrators and the system have full control; users and the service may read and run.</summary>
    private static DirectorySecurity AdministratorsOnly()
    {
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(LocalService, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// Administrators and the system may change the configuration; everyone may read it; the service may write only
    /// its own state folder (logs and status). A folder someone else created first is replaced, not trusted.
    /// </summary>
    private static void SecureFolders(ServerLocations locations, StringBuilder log)
    {
        var root = new DirectoryInfo(locations.Root);
        if (root.Exists)
        {
            var owner = root.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is null || !(owner.Equals(Administrators) || owner.Equals(LocalSystem)))
            {
                log.AppendLine($"{root.FullName} was created by {owner?.Translate(typeof(NTAccount))}, not by an administrator; replacing it.");
                root.Delete(recursive: true);
            }
        }
        root.Create();
        root.SetAccessControl(AdministratorsOnly());

        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var state = new DirectoryInfo(locations.StateDirectory);
        state.Create();
        var stateSecurity = state.GetAccessControl();
        stateSecurity.AddAccessRule(new FileSystemAccessRule(LocalService, FileSystemRights.Modify, inherit, PropagationFlags.None, AccessControlType.Allow));
        state.SetAccessControl(stateSecurity);
        Directory.CreateDirectory(locations.LogDirectory);
    }

    /// <summary>One inbound rule per shared port, limited to its listen address, its allowed clients and this program.</summary>
    private static bool ConfigureFirewall(ServerConfig config, ServerLocations locations, string exe, StringBuilder log)
    {
        // Deleting by name removes every rule with that name; "no rules match" is fine.
        Run("netsh.exe", ["advfirewall", "firewall", "delete", "rule", $"name={locations.FirewallRuleName}"], log: null);
        if (!config.Enabled)
        {
            log.AppendLine("Firewall rules removed.");
            return true;
        }

        var ok = true;
        foreach (var port in config.Ports.Where(p => p.Enabled))
        {
            var remote = port.AllowedClients.Any(c => ClientFilter.TryParse(c, out _, out var prefix) && prefix == 0)
                ? "any"
                : string.Join(",", port.AllowedClients);
            var local = IPAddress.TryParse(port.ListenAddress, out var listen) && !listen.Equals(IPAddress.Any) && !listen.Equals(IPAddress.IPv6Any)
                ? port.ListenAddress
                : "any";
            ok &= Run("netsh.exe",
            [
                "advfirewall", "firewall", "add", "rule",
                $"name={locations.FirewallRuleName}",
                "dir=in", "action=allow", "protocol=TCP",
                $"localport={port.TcpPort}", $"localip={local}", $"remoteip={remote}",
                $"program={exe}", "enable=yes",
                $"description=Shares {port.Title} on TCP port {port.TcpPort}.",
            ], log) == 0;
        }
        log.AppendLine(ok ? "Firewall rules set." : "Some firewall rules couldn't be set; see above.");
        return ok;
    }

    private static bool InstallAndStartService(ServerLocations locations, string exe, StringBuilder log)
    {
        var name = locations.ServiceName;
        var binPath = $"\"{exe}\" {BridgeService.Argument}";
        var exists = ServiceExists(name);
        var ok = Run("sc.exe", exists
            ? ["config", name, "binPath=", binPath, "start=", "auto", "obj=", @"NT AUTHORITY\LocalService", "password=", "", "DisplayName=", locations.ServiceDisplayName]
            : ["create", name, "binPath=", binPath, "start=", "auto", "obj=", @"NT AUTHORITY\LocalService", "password=", "", "DisplayName=", locations.ServiceDisplayName],
            log) == 0;
        if (!ok)
        {
            log.AppendLine($"The {locations.ServiceDisplayName} service couldn't be {(exists ? "updated" : "installed")}.");
            return false;
        }
        Run("sc.exe", ["description", name, "Shares this PC's COM ports on TCP ports, one client at a time per port."], log: null);
        // Restart after a crash: 5 s, 5 s, then 1 minute; the failure count resets after a day.
        Run("sc.exe", ["failure", name, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/60000"], log: null);

        using var service = new ServiceController(name);
        try
        {
            if (service.Status != ServiceControllerStatus.Running)
            {
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, ServiceTimeout);
                log.AppendLine($"{locations.ServiceDisplayName} service {(exists ? "started" : "installed and started")}.");
            }
            else
            {
                log.AppendLine($"{locations.ServiceDisplayName} service is running and picks up the new settings by itself.");
            }
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
        {
            log.AppendLine($"The {locations.ServiceDisplayName} service didn't start: {ex.Message} Its log is in {locations.LogDirectory}.");
            return false;
        }
    }

    private static bool RemoveService(ServerLocations locations, StringBuilder log)
    {
        var name = locations.ServiceName;
        if (!ServiceExists(name))
            return true;
        StopService(locations, log);
        var ok = Run("sc.exe", ["delete", name], log) == 0;
        log.AppendLine(ok ? $"{locations.ServiceDisplayName} service removed." : $"The {locations.ServiceDisplayName} service couldn't be removed.");
        return ok;
    }

    private static void StopService(ServerLocations locations, StringBuilder log)
    {
        if (!ServiceExists(locations.ServiceName))
            return;
        using var service = new ServiceController(locations.ServiceName);
        try
        {
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, ServiceTimeout);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
        {
            log.AppendLine($"Stopping the {locations.ServiceDisplayName} service failed: {ex.Message}");
        }
    }

    private static bool ServiceExists(string name) =>
        ServiceController.GetServices().Any(s => string.Equals(s.ServiceName, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs a system tool, adds its output to <paramref name="log"/> when it fails, and returns its exit code.</summary>
    private static int Run(string tool, IEnumerable<string> arguments, StringBuilder? log)
    {
        var start = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{tool} didn't start.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 && log is not null)
            log.AppendLine($"{tool} {string.Join(' ', arguments.Take(4))}… failed ({process.ExitCode}): {output.Trim()}");
        return process.ExitCode;
    }
}
