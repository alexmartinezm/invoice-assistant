using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Api.Tests.Support;

/// <summary>
/// Keeps the server's log records so a failing test can show why the server said no, instead of
/// only showing the status code it answered with.
/// </summary>
public sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _records = new();

    public IEnumerable<string> Records => _records;

    public IEnumerable<string> Failures => _records.Where(r => r.StartsWith("Error", StringComparison.Ordinal));

    public ILogger CreateLogger(string categoryName) => new QueueLogger(_records, categoryName);

    public void Dispose()
    {
    }

    private sealed class QueueLogger(ConcurrentQueue<string> records, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

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

            var message = $"{logLevel} {category}: {formatter(state, exception)}";
            records.Enqueue(exception is null ? message : $"{message}\n{exception}");
        }
    }
}
