using System.Text;
using System.Text.Json;
using Vcns.VirtualCom.Bridge.Diagnostics;

namespace Vcns.VirtualCom.Bridge.Configuration;

/// <summary>
/// Loads and saves the JSON configuration. Saves are validated first and written to a temporary file that
/// atomically replaces the previous one, so a failed save never damages the last valid configuration.
/// </summary>
internal sealed class ConfigStore(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string FilePath { get; } = filePath;

    /// <summary>Loads the configuration, or returns an empty one if none exists yet.</summary>
    /// <param name="warning">Set when an unreadable file was set aside and defaults were used instead.</param>
    public AppConfig Load(out string? warning)
    {
        warning = null;
        if (!File.Exists(FilePath))
        {
            Log.Info($"No configuration at {FilePath}; starting with no profiles.");
            return new AppConfig();
        }

        try
        {
            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), JsonOptions)
                ?? throw new JsonException("The file is empty.");
            Normalise(config);
            Log.Info($"Loaded {config.Profiles.Count} profile(s) from {FilePath}.");
            return config;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            var kept = "";
            if (ex is JsonException or NotSupportedException)
            {
                var aside = Path.Combine(Path.GetDirectoryName(FilePath)!,
                    $"{Path.GetFileNameWithoutExtension(FilePath)}.invalid-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                try
                {
                    File.Copy(FilePath, aside, overwrite: false);
                    kept = $" A copy was kept as {aside}.";
                }
                catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
                {
                }
            }
            warning = $"The configuration file {FilePath} could not be read ({ex.Message}).{kept} "
                + "The application started with no profiles; saving in Settings will replace the unreadable file.";
            Log.Error("Configuration unreadable." + kept, ex);
            return new AppConfig();
        }
    }

    /// <exception cref="ConfigValidationException">The configuration is invalid; nothing was written.</exception>
    public void Save(AppConfig config)
    {
        Normalise(config);
        var errors = ConfigValidator.Validate(config);
        if (errors.Count > 0)
            throw new ConfigValidationException(errors);

        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temp = FilePath + ".tmp";
        var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(config, JsonOptions));
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(FilePath))
                File.Replace(temp, FilePath, FilePath + ".bak", ignoreMetadataErrors: true);
            else
                File.Move(temp, FilePath);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        Log.Info($"Saved {config.Profiles.Count} profile(s) to {FilePath}.");
    }

    internal static void Normalise(AppConfig config)
    {
        config.Profiles ??= [];
        config.Options ??= new ProxyOptions();
        foreach (var p in config.Profiles)
        {
            p.TargetServer = (p.TargetServer ?? "").Trim();
            p.LocalComPort = (p.LocalComPort ?? "").Trim().ToUpperInvariant();
            p.BridgePort = string.IsNullOrWhiteSpace(p.BridgePort) ? null : p.BridgePort.Trim().ToUpperInvariant();
            p.Customer = Tidy(p.Customer);
            p.Label = Tidy(p.Label);
            p.Device = Tidy(p.Device);
            p.Notes = Tidy(p.Notes);
            if (p.Id == Guid.Empty)
                p.Id = Guid.NewGuid();
        }
        config.Options.SetupcPath = string.IsNullOrWhiteSpace(config.Options.SetupcPath) ? null : config.Options.SetupcPath.Trim();
        if (config.Options.ShowZabbixReminder is { } legacy)
        {
            config.Options.ShowConnectReminder = legacy;
            config.Options.ShowZabbixReminder = null;
        }
    }

    /// <summary>Trims free text and treats blank as absent, so optional fields are either null or meaningful.</summary>
    internal static string? Tidy(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class ConfigValidationException(IReadOnlyList<string> errors)
    : Exception("The configuration is invalid:" + Environment.NewLine + string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
