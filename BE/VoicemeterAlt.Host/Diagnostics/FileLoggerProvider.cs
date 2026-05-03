using Microsoft.Extensions.Logging;

namespace VoicemeterAlt.Host.Diagnostics;

/// <summary>
/// Minimal ILoggerProvider that fans Microsoft.Extensions.Logging output into
/// the rotating <see cref="CrashLog"/> file alongside the existing console
/// sink. Anything at or above <see cref="LogLevel.Information"/> is written
/// so the file captures the day's startup banner, refreshDevices events,
/// preset auto-load notices, etc. — not just crashes.
///
/// Lighter than reaching for Serilog/NLog: we already own the file logic in
/// <see cref="CrashLog"/> and the host has only a handful of categories.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName);

    public void Dispose() { /* nothing owned per-provider; CrashLog is process-static */ }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;

        public FileLogger(string category) => _category = category;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            CrashLog.Write(MapLevel(logLevel), $"[{_category}] {message}", exception);
        }

        private static string MapLevel(LogLevel level) => level switch
        {
            LogLevel.Critical    => "FATAL",
            LogLevel.Error       => "ERROR",
            LogLevel.Warning     => "WARN",
            LogLevel.Information => "INFO",
            _                    => "DEBUG",
        };
    }
}
