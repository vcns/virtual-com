using System.Text;

namespace Vcns.VirtualCom.Bridge.Diagnostics;

/// <summary>
/// Minimal thread-safe file logger. One file per day, named after the edition (e.g. novastar-proxy-YYYYMMDD.log),
/// kept for <see cref="RetentionDays"/> days. Payload bytes are only written via <see cref="Payload"/>,
/// which callers must gate on the user's explicit opt-in.
/// </summary>
internal static class Log
{
    private const int RetentionDays = 30;
    private static readonly object Gate = new();
    private static string? _directory;
    private static string _filePrefix = BridgeEdition.Generic.LogFilePrefix;
    private static StreamWriter? _writer;
    private static DateOnly _writerDate;

    public static string Directory => _directory ?? throw new InvalidOperationException("Log not initialised.");

    public static string CurrentFile => Path.Combine(Directory, FileNameFor(DateOnly.FromDateTime(DateTime.Now)));

    public static void Initialise(string directory, string filePrefix)
    {
        lock (Gate)
        {
            _directory = directory;
            _filePrefix = filePrefix;
            System.IO.Directory.CreateDirectory(directory);
        }
        PurgeOldFiles();
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");

    public static void Payload(string port, string direction, ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length * 3 + 48);
        sb.Append(port).Append(' ').Append(direction).Append(' ').Append(data.Length).Append(" bytes:");
        foreach (var b in data)
            sb.Append(' ').Append(b.ToString("X2"));
        Write("DATA ", sb.ToString());
    }

    private static void Write(string level, string message)
    {
        if (_directory is null)
            return;

        var now = DateTimeOffset.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";
        lock (Gate)
        {
            try
            {
                var today = DateOnly.FromDateTime(now.DateTime);
                if (_writer is null || _writerDate != today)
                {
                    _writer?.Dispose();
                    var stream = new FileStream(Path.Combine(_directory, FileNameFor(today)),
                        FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                    _writerDate = today;
                }
                _writer.WriteLine(line);
            }
            catch (IOException)
            {
                // Logging must never take the proxy down.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static string FileNameFor(DateOnly date) => $"{_filePrefix}-{date:yyyyMMdd}.log";

    private static void PurgeOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in new DirectoryInfo(Directory).EnumerateFiles($"{_filePrefix}-*.log"))
            {
                if (file.LastWriteTime < cutoff)
                    file.Delete();
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
