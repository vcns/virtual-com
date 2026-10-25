using System.Text.Json;

namespace Vcns.VirtualCom.Bridge.Server;

/// <summary>What the service is doing, written to a file the tray application reads.</summary>
internal sealed class ServerStatus
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public DateTimeOffset UpdatedAt { get; set; }

    public int ProcessId { get; set; }

    public List<PortStatus> Ports { get; set; } = [];

    public static ServerStatus? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ServerStatus>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}

internal sealed class PortStatus
{
    public string ComPort { get; set; } = "";

    public string? Label { get; set; }

    public int TcpPort { get; set; }

    public string ListenAddress { get; set; } = "";

    /// <summary>A <see cref="PortServerState"/> name.</summary>
    public string State { get; set; } = "";

    /// <summary>The connected client's address and port, if any.</summary>
    public string? Client { get; set; }

    public DateTimeOffset? ClientSince { get; set; }

    public long BytesToDevice { get; set; }

    public long BytesFromDevice { get; set; }

    /// <summary>Why the port isn't listening, or why the last client couldn't be served.</summary>
    public string? Problem { get; set; }

    /// <summary>The latest notable event, such as a client being turned away, with its time.</summary>
    public string? LastEvent { get; set; }

    /// <summary>One line for the tray menu and the Settings window.</summary>
    public string Describe() => State switch
    {
        nameof(PortServerState.Connected) => $"{Client} connected since {ClientSince:HH:mm:ss}",
        nameof(PortServerState.Listening) => Problem is null ? "Listening" : $"Listening; last client: {Problem}",
        nameof(PortServerState.NotListening) => $"Not listening: {Problem}",
        _ => State,
    };
}
