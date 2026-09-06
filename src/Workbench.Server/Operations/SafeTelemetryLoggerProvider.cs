// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Operations;

public sealed class SafeTelemetryLoggerProvider(TextWriter output) : ILoggerProvider
{
    private readonly object _sync = new();
    private readonly TextWriter _output = output;
    public ILogger CreateLogger(string categoryName) => new SafeLogger(this,
        categoryName.StartsWith("Workbench.", StringComparison.Ordinal) ? "Application" : "Dependency");
    public void Dispose() { }

    private sealed class SafeLogger(SafeTelemetryLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Information and < LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            // Never evaluate a formatter, exception message, event name, scope, or
            // arbitrary structured fields. The export schema is deliberately closed.
            var line = System.Text.Json.JsonSerializer.Serialize(new
            {
                time = DateTimeOffset.UtcNow,
                level = logLevel.ToString(),
                category,
                eventId = eventId.Id,
                traceId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
                failed = exception is not null,
            });
            lock (provider._sync)
            {
                provider._output.WriteLine(line);
            }
        }
    }
}
