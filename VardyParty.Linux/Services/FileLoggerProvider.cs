using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace VardyParty.Linux.Services;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public FileLoggerProvider(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        _filePath = Path.Combine(logDirectory, $"vardyparty-{DateTime.UtcNow:yyyyMMdd}.log");
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new FileLogger(name, WriteLine));
    }

    private void WriteLine(string line)
    {
        lock (_writeLock)
        {
            File.AppendAllText(_filePath, line + Environment.NewLine);
        }
    }

    public void Dispose()
    {
        _loggers.Clear();
    }
}

internal sealed class FileLogger : ILogger
{
    private readonly string _category;
    private readonly Action<string> _write;

    public FileLogger(string category, Action<string> write)
    {
        _category = category;
        _write = write;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        return NullScope.Instance;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel != LogLevel.None;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
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

        public void Dispose()
        {
        }
    }
}