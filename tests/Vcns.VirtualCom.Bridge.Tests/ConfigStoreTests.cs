using NovaStarProxy.Configuration;

namespace NovaStarProxy.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "NovaStarProxyTests", Guid.NewGuid().ToString("N"));

    private string ConfigPath => Path.Combine(_directory, "config.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static AppConfig Sample() => new()
    {
        Profiles =
        [
            new ConnectionProfile { TargetServer = "192.168.10.50", TargetPort = 5200, LocalComPort = "com9" },
            new ConnectionProfile { Enabled = false, TargetServer = "controller.example.net", TargetPort = 5200, LocalComPort = "COM10" },
        ],
    };

    [Fact]
    public void Missing_file_loads_an_empty_configuration()
    {
        var config = new ConfigStore(ConfigPath).Load(out var warning);
        Assert.Empty(config.Profiles);
        Assert.Null(warning);
    }

    [Fact]
    public void Configuration_round_trips_and_is_normalised()
    {
        var store = new ConfigStore(ConfigPath);
        var saved = Sample();
        store.Save(saved);

        var loaded = new ConfigStore(ConfigPath).Load(out var warning);
        Assert.Null(warning);
        Assert.Equal(2, loaded.Profiles.Count);
        Assert.Equal("COM9", loaded.Profiles[0].LocalComPort);
        Assert.Equal(saved.Profiles[1].Id, loaded.Profiles[1].Id);
        Assert.False(loaded.Profiles[1].Enabled);
        Assert.Contains("\"targetServer\": \"controller.example.net\"", File.ReadAllText(ConfigPath));
        Assert.False(File.Exists(ConfigPath + ".tmp"));
    }

    [Fact]
    public void Invalid_configuration_is_not_written()
    {
        var store = new ConfigStore(ConfigPath);
        store.Save(Sample());
        var before = File.ReadAllText(ConfigPath);

        var invalid = Sample();
        invalid.Profiles[1].LocalComPort = "COM9";
        var ex = Assert.Throws<ConfigValidationException>(() => store.Save(invalid));

        Assert.Contains(ex.Errors, e => e.Contains("more than one profile"));
        Assert.Equal(before, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void A_failed_write_leaves_the_previous_file_intact()
    {
        var store = new ConfigStore(ConfigPath);
        store.Save(Sample());
        var before = File.ReadAllText(ConfigPath);

        // Hold the temporary file open so the write fails part-way.
        using (new FileStream(ConfigPath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            Assert.ThrowsAny<IOException>(() => store.Save(Sample()));

        Assert.Equal(before, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void Corrupt_file_is_kept_aside_and_defaults_are_used()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ConfigPath, "{ not json");

        var config = new ConfigStore(ConfigPath).Load(out var warning);

        Assert.Empty(config.Profiles);
        Assert.NotNull(warning);
        Assert.Single(Directory.GetFiles(_directory, "config.invalid-*.json"));
    }
}
