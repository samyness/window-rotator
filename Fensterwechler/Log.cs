using System.IO;

namespace Fensterwechler;

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss} [{Level}] {Message}";
}

/// <summary>Daily log files under %LocalAppData%\WindowRotator\logs, plus an event for the UI.</summary>
public static class Log
{
    const int RetentionDays = 14;
    static readonly object Gate = new();

    public static LogLevel Level { get; set; } = LogLevel.Info;
    public static string Directory { get; set; } = Path.Combine(AppPaths.LocalData, "logs");
    public static event Action<LogEntry>? Written;

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warning, message);
    public static void Error(string message, Exception? ex = null) => Write(LogLevel.Error, ex == null ? message : $"{message}: {ex}");

    public static void Write(LogLevel level, string message)
    {
        if (level < Level) return;
        var entry = new LogEntry(DateTime.Now, level, message);
        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                // UTF-8 with BOM (written once per file) so umlauts survive Windows PowerShell 5.1 and old editors.
                File.AppendAllText(Path.Combine(Directory, $"wwr-{entry.Time:yyyyMMdd}.log"), entry + Environment.NewLine, System.Text.Encoding.UTF8);
            }
            catch (IOException) { } // logging must never take the rotation down
            catch (UnauthorizedAccessException) { }
        }
        Written?.Invoke(entry);
    }

    public static void PruneOldFiles()
    {
        try
        {
            foreach (var file in new DirectoryInfo(Directory).EnumerateFiles("wwr-*.log"))
                if (file.LastWriteTime < DateTime.Now.AddDays(-RetentionDays)) file.Delete();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
