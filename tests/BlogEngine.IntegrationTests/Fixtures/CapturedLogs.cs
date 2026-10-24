using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace BlogEngine.IntegrationTests.Fixtures;

public sealed class CapturedLogs : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Level, string Category, string Message)> Entries { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);
    public void Dispose() { }

    private sealed class Capture(CapturedLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Entries.Enqueue((logLevel, category, formatter(state, exception)));
    }
}
