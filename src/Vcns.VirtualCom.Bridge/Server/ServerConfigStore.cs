using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vcns.VirtualCom.Bridge.Configuration;

namespace Vcns.VirtualCom.Bridge.Server;

/// <summary>Reads and writes the machine-wide server configuration (server.json).</summary>
internal static class ServerConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Loads the configuration, or returns server mode switched off if there is none.</summary>
    /// <param name="warning">Set when the file exists but couldn't be read.</param>
    public static ServerConfig Load(string path, out string? warning)
    {
        warning = null;
        if (!File.Exists(path))
            return new ServerConfig();
        try
        {
            var config = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), JsonOptions)
                ?? throw new JsonException("The file is empty.");
            Normalise(config);
            return config;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            warning = $"The server configuration {path} could not be read ({ex.Message}).";
            return new ServerConfig();
        }
    }

    /// <summary>
    /// Validates and writes the configuration. The new file is moved into place rather than replacing the old one's
    /// contents, so it takes its permissions from the folder, not from whatever file was there before.
    /// </summary>
    /// <exception cref="ConfigValidationException">The configuration is invalid; nothing was written.</exception>
    public static void Save(string path, ServerConfig config)
    {
        Normalise(config);
        var errors = ServerConfigValidator.Validate(config);
        if (errors.Count > 0)
            throw new ConfigValidationException(errors);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(config, JsonOptions));
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    internal static void Normalise(ServerConfig config)
    {
        config.Ports ??= [];
        foreach (var p in config.Ports)
        {
            p.ComPort = (p.ComPort ?? "").Trim().ToUpperInvariant();
            p.ListenAddress = (p.ListenAddress ?? "").Trim();
            p.AllowedClients = (p.AllowedClients ?? [])
                .Select(c => c.Trim())
                .Where(c => c.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            p.Serial ??= new SerialSettings();
            if (p.Id == Guid.Empty)
                p.Id = Guid.NewGuid();
        }
    }
}
