using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Tests.Support;

/// <summary>
/// One entry a <see cref="RecordingLoggerFactory"/> captured.
/// </summary>
/// <param name="Category">The category the logger was created for.</param>
/// <param name="Level">The level of the entry.</param>
/// <param name="EventId">The event id of the entry.</param>
/// <param name="Message">The rendered message.</param>
/// <param name="Exception">The exception, when one was logged.</param>
/// <param name="State">The structured state, as key/value pairs when the state exposes them.</param>
internal sealed record RecordedLogEntry(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    Exception? Exception,
    IReadOnlyList<KeyValuePair<string, object?>> State);

/// <summary>
/// An <see cref="ILoggerFactory"/> that keeps every entry so a test can assert on it.
/// </summary>
/// <remarks>
/// The library logs through source-generated methods, so asserting on the event id and the rendered
/// message is what catches a message that was wired to the wrong event — the kind of mistake a
/// test that only counts lines cannot see.
/// </remarks>
internal sealed class RecordingLoggerFactory : ILoggerFactory
{
    private readonly object _sync = new();
    private readonly List<RecordedLogEntry> _entries = [];

    /// <summary>
    /// Gets or sets the minimum level the created loggers report as enabled.
    /// </summary>
    /// <remarks>
    /// Used to stand in for the filtering a host's configuration applies, so a test can prove that
    /// replaying through the inner factory honours it.
    /// </remarks>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Trace;

    /// <summary>Gets a value indicating whether the factory was disposed.</summary>
    public bool Disposed { get; private set; }

    /// <summary>Gets the entries captured so far, in the order they were written.</summary>
    public IReadOnlyList<RecordedLogEntry> Entries
    {
        get
        {
            lock (_sync)
            {
                return [.. _entries];
            }
        }
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);

    /// <inheritdoc />
    public void AddProvider(ILoggerProvider provider)
    {
    }

    /// <inheritdoc />
    public void Dispose() => Disposed = true;

    /// <summary>
    /// Gets the entries written for one event id.
    /// </summary>
    /// <param name="eventId">The event id to filter on.</param>
    /// <returns>The matching entries, in order.</returns>
    public IReadOnlyList<RecordedLogEntry> ForEvent(int eventId) =>
        [.. Entries.Where(entry => entry.EventId.Id == eventId)];

    /// <summary>Records one entry.</summary>
    /// <param name="entry">The entry to record.</param>
    internal void Record(RecordedLogEntry entry)
    {
        lock (_sync)
        {
            _entries.Add(entry);
        }
    }

    private sealed class RecordingLogger(RecordingLoggerFactory owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= owner.MinimumLevel;

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

            IReadOnlyList<KeyValuePair<string, object?>> values =
                state is IEnumerable<KeyValuePair<string, object?>> pairs ? [.. pairs] : [];

            owner.Record(new RecordedLogEntry(
                category, logLevel, eventId, formatter(state, exception), exception, values));
        }
    }
}
