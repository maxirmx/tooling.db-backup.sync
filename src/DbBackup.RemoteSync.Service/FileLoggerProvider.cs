// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

using System.Text;
using Microsoft.Extensions.Logging;

namespace DbBackup.RemoteSync.Service;

internal sealed class FileLoggerProvider(string path) : ILoggerProvider
{
    private const long MaximumLogBytes = 2L * 1024 * 1024;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly object _gate = new();
    private readonly string _path = Path.GetFullPath(path);

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(
        string category,
        LogLevel level,
        EventId eventId,
        string message,
        Exception? exception)
    {
        if (level < LogLevel.Information)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                var directory = Path.GetDirectoryName(_path)
                    ?? throw new InvalidOperationException("The diagnostic log must have a parent directory.");
                Directory.CreateDirectory(directory);
                if (File.Exists(_path) && new FileInfo(_path).Length >= MaximumLogBytes)
                {
                    File.Move(_path, _path + ".previous", overwrite: true);
                }

                var line = $"{DateTimeOffset.UtcNow:O} [{level}] {category}" +
                    (eventId.Id == 0 ? string.Empty : $" ({eventId.Id})") +
                    $": {message}";
                if (exception is not null)
                {
                    line += Environment.NewLine + exception;
                }

                File.AppendAllText(_path, line + Environment.NewLine, Utf8NoBom);
            }
        }
        catch
        {
            // Diagnostics must never stop the synchronization service.
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(category, logLevel, eventId, formatter(state, exception), exception);
            }
        }
    }
}
