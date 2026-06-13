using System.Diagnostics;

namespace VardyParty.Platforms.Windows;

/// <summary>
/// Writes to the daily log file (and Windows Application event log when available).
/// </summary>
internal static class WindowsEventLogger
{
    private static readonly object WriteLock = new();
    private static string? _filePath;
    private static EventLog? _eventLog;
    private static bool _eventLogUnavailable;

    public static string LogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VardyParty", "logs");

    public static string LogFilePath => _filePath ??= Path.Combine(LogDirectory, $"vardyparty-{DateTime.UtcNow:yyyyMMdd}.log");

    public static void RegisterFilePath(string filePath) => _filePath = filePath;

    public static void Info(string source, string message) => Write("Information", source, message);

    public static void Warning(string source, string message, Exception? ex = null) =>
        Write("Warning", source, message, ex);

    public static void Error(string source, string message, Exception? ex = null) =>
        Write("Error", source, message, ex);

    public static void Fatal(string source, string message, Exception? ex = null) =>
        Write("Critical", source, message, ex);

    private static void Write(string level, string source, string message, Exception? ex = null)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {source}: {message}";
            if (ex != null)
            {
                line += Environment.NewLine + ex;
            }

            lock (WriteLock)
            {
                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }

            WriteEventLog(level, source, message, ex);
        }
        catch
        {
            // Last-resort logging must never throw.
        }
    }

    private static void WriteEventLog(string level, string source, string message, Exception? ex)
    {
        if (_eventLogUnavailable)
        {
            return;
        }

        try
        {
            _eventLog ??= TryCreateEventLog();
            if (_eventLog == null)
            {
                _eventLogUnavailable = true;
                return;
            }

            var entry = $"[{source}] {message}";
            if (ex != null)
            {
                entry += Environment.NewLine + ex;
            }

            var type = level switch
            {
                "Critical" => EventLogEntryType.Error,
                "Error" => EventLogEntryType.Error,
                "Warning" => EventLogEntryType.Warning,
                _ => EventLogEntryType.Information
            };

            _eventLog.WriteEntry(entry, type);
        }
        catch
        {
            _eventLogUnavailable = true;
        }
    }

    private static EventLog? TryCreateEventLog()
    {
        const string source = "VardyParty";

        try
        {
            if (!EventLog.SourceExists(source))
            {
                EventLog.CreateEventSource(source, "Application");
            }

            return new EventLog { Source = source };
        }
        catch
        {
            return null;
        }
    }
}
