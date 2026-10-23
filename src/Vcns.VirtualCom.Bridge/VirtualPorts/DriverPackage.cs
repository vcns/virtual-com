using System.Reflection;

namespace Vcns.VirtualCom.Bridge.VirtualPorts;

/// <summary>
/// The signed VCNS virtual COM driver package (vcnsvcom.inf, .dll, .cat), embedded at build time when a signed package
/// is available (see VirtualComPackageDir in the project file).
/// </summary>
internal static class DriverPackage
{
    private const string ResourcePrefix = "VirtualComDriver/";

    public static bool IsBundled => ResourceNames.Any(n => n.EndsWith("vcnsvcom.inf", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> ResourceNames =>
        Assembly.GetExecutingAssembly().GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal));

    /// <summary>Writes the package files to <paramref name="directory"/> and returns the INF's path.</summary>
    public static string ExtractTo(string directory)
    {
        Directory.CreateDirectory(directory);
        var assembly = Assembly.GetExecutingAssembly();
        string? inf = null;
        foreach (var name in ResourceNames)
        {
            var path = Path.Combine(directory, name[ResourcePrefix.Length..]);
            using (var source = assembly.GetManifestResourceStream(name)!)
            using (var target = File.Create(path))
                source.CopyTo(target);
            if (path.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
                inf = path;
        }
        return inf ?? throw new InvalidOperationException("This build doesn't include the virtual COM driver package.");
    }
}
