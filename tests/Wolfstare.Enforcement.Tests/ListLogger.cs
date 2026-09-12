using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Wolfstare.Enforcement.Tests;

/// <summary>
/// Captures log entries. The network servers swallow per-connection failures so one bad client
/// cannot affect another, which also hides bugs — a test can use this to assert nothing went
/// wrong quietly.
/// </summary>
public sealed class ListLogger : ILogger
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> AtLeast(LogLevel level) => _entries.Where(e => e.Level >= level).ToList();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
}

public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception)
{
    public override string ToString() => $"[{Level}] {Message} {Exception}";
}
