using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace InvoiceAssistant.Evals;

/// <summary>
/// Keeps the application's warning-and-worse log entries, exceptions included. When a turn dies,
/// the SSE stream deliberately says only "check the server logs" — correct for production, a dead
/// end in CI, where those logs are not captured anywhere. This is the harness's copy, so a case
/// failure can carry the real reason instead of a pointer to nothing.
/// </summary>
public sealed class ServerLogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    /// <summary>The most recent captured entry, or null when the turn logged nothing noteworthy.</summary>
    public string? Latest => _entries.LastOrDefault();

    public void Clear() => _entries.Clear();

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CaptureLogger(string category, ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

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

            var entry = $"{logLevel} {category}: {formatter(state, exception)}";

            if (exception is not null)
            {
                entry += $"\n{exception}";
            }

            entries.Enqueue(entry);

            // A bound, not a log store: only the tail can matter to a failure message.
            while (entries.Count > 20 && entries.TryDequeue(out _))
            {
            }
        }
    }
}
