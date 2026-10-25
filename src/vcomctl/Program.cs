// SPDX-License-Identifier: MS-PL
// Copyright (c) 2026 VCNS Tech Ltd
using System.ComponentModel;
using Vcns.VirtualCom;

const string Usage = """
    vcomctl - manage VCNS virtual COM ports

      vcomctl list                       List virtual ports
      vcomctl install-driver <inf>       Add the driver package to the driver store (admin)
      vcomctl create COMn                Create a port called COMn (admin)
      vcomctl remove COMn                Remove a port (admin)
      vcomctl label COMn ["text"]        Name the port in Device Manager, e.g. "Main hall (COM9)";
                                         no text restores the standard name (admin)
    """;

try
{
    switch (args)
    {
        case ["list"]:
            var ports = VirtualComDevices.List();
            if (ports.Count == 0)
                Console.WriteLine("No virtual COM ports.");
            foreach (var port in ports)
                Console.WriteLine($"{port.PortName ?? "(no name)",-8} {(port.IsRunning ? "running" : $"not running (problem {port.ProblemCode})"),-28} {port.InstanceId}");
            return 0;

        case ["install-driver", var inf]:
            var reboot = VirtualComDevices.InstallDriver(inf);
            Console.WriteLine(reboot ? "Driver package installed; a restart is needed." : "Driver package installed.");
            return 0;

        case ["create", var name]:
            var created = VirtualComDevices.Create(name);
            Console.WriteLine($"Created {created.PortName} ({created.InstanceId}): {(created.IsRunning ? "running" : $"not running (problem {created.ProblemCode})")}");
            return created.IsRunning ? 0 : 2;

        case ["remove", var target]:
            VirtualComDevices.Remove(target);
            Console.WriteLine($"Removed {target}.");
            return 0;

        case ["label", var port, .. var words] when words.Length <= 1:
            VirtualComDevices.SetDisplayName(port, words.FirstOrDefault());
            Console.WriteLine(words.Length == 0 ? $"{port} has its standard name again." : $"{port} is now shown as \"{words[0]} ({port.ToUpperInvariant()})\".");
            return 0;

        default:
            Console.Error.WriteLine(Usage);
            return 1;
    }
}
catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}
