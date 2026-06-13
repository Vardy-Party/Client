using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace VardyParty.Platforms.Windows;

public sealed class WindowsFileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<string, WindowsFileLogger> _loggers = new();

    public WindowsFileLoggerProvider(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        _filePath = Path.Combine(logDirectory, $"vardyparty-{DateTime.UtcNow:yyyyMMdd}.log");
        WindowsEventLogger.RegisterFilePath(_filePath);
    }

    public string FilePath => _filePath;

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new WindowsFileLogger(name, WriteLine));

    private void WriteLine(string line)
    {
        lock (_writeLock)
        {
            File.AppendAllText(_filePath, line + Environment.NewLine);
        }
    }

    public void Dispose() => _loggers.Clear();
}

internal sealed class WindowsFileLogger : ILogger
{
    private readonly string _category;
    private readonly Action<string> _write;

    public WindowsFileLogger(string category, Action<string> write)
    {
        _category = category;
        _write = write;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
        NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        if (string.IsNullOrWhiteSpace(message) && exception == null)
        {
            return;
        }

        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{logLevel}] {_category}: {message}";
        if (exception != null)
        {
            line += Environment.NewLine + exception;
        }

        _write(line);
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();
        public void Dispose() { }
    }
}
