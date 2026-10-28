using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Extensions.Configuration;

/// <summary>
/// A logger factory that starts buffering and is connected to the host's real factory once the
/// container exists.
/// </summary>
/// <remarks>
/// <para>
/// A configuration source is built while the host is still being assembled, which is before any
/// service — and in particular before <see cref="ILoggerFactory"/> — can be resolved. The source
/// still has to report why it could not reach the server, so this factory accepts the log calls,
/// keeps the ones that matter for startup, and forwards them to the host's factory as soon as the
/// host hands it over.
/// </para>
/// <para>
/// Only <see cref="LogLevel.Information"/> and above are buffered: those are the lines that explain
/// a startup problem (an unreachable server, a subscription that failed, the initial load), while
/// the per-request debug traffic would only fill the buffer and never be missed.
/// </para>
/// <para>
/// The inner factory is never disposed here. It belongs to the host, and disposing it from a
/// configuration source would tear down logging for the whole process.
/// </para>
/// </remarks>
internal sealed class NacosDeferredLoggerFactory : ILoggerFactory
{
    /// <summary>
    /// The number of startup entries kept while waiting for the host's factory.
    /// </summary>
    /// <remarks>
    /// The bound is what keeps a server that is down for a long time — and therefore keeps failing
    /// while the host is still starting — from growing the buffer without limit. The oldest entries
    /// are dropped first, because the newest ones describe the state the host is about to run in.
    /// </remarks>
    internal const int MaxBufferedEntries = 64;

    private readonly object _sync = new();
    private readonly List<NacosDeferredLogger> _loggers = [];
    private readonly Queue<BufferedEntry> _buffered = new();

    private ILoggerFactory? _inner;
    private bool _disposed;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
    {
        ArgumentNullException.ThrowIfNull(categoryName);

        lock (_sync)
        {
            var logger = new NacosDeferredLogger(this, categoryName);

            // A logger created after the bind must not wait for the next one, so it is connected
            // straight away.
            if (_inner is not null)
            {
                logger.Bind(_inner);
            }

            _loggers.Add(logger);

            return logger;
        }
    }

    /// <inheritdoc />
    public void AddProvider(ILoggerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        // Before the bind there is nothing to attach a provider to; after it, the host's factory
        // stays the single owner of the providers.
        lock (_sync)
        {
            _inner?.AddProvider(provider);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _buffered.Clear();
            _loggers.Clear();
        }

        // The inner factory is deliberately left alone: the container created it and the container
        // disposes it.
    }

    /// <summary>
    /// Connects this factory to the host's factory and replays everything buffered so far.
    /// </summary>
    /// <param name="inner">The host's logger factory.</param>
    /// <remarks>
    /// Replaying through the inner factory rather than writing to a console means the entries obey
    /// the filters the application configured, and end up in the same sinks as every other line.
    /// The state and formatter of each entry are kept, so structured fields survive the detour.
    /// </remarks>
    internal void Bind(ILoggerFactory inner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        BufferedEntry[] replay;

        lock (_sync)
        {
            // A second bind would either replay the same lines twice or silently replace the
            // factory the host already connected, so it is ignored.
            if (_disposed || _inner is not null)
            {
                return;
            }

            _inner = inner;

            foreach (NacosDeferredLogger logger in _loggers)
            {
                logger.Bind(inner);
            }

            replay = [.. _buffered];
            _buffered.Clear();
        }

        foreach (BufferedEntry entry in replay)
        {
            entry.Replay();
        }
    }

    /// <summary>
    /// Buffers one entry, or forwards it when the bind won the race.
    /// </summary>
    /// <typeparam name="TState">The type of the state object.</typeparam>
    /// <param name="logger">The logger the entry came from.</param>
    /// <param name="logLevel">The level of the entry.</param>
    /// <param name="eventId">The event id of the entry.</param>
    /// <param name="state">The structured state.</param>
    /// <param name="exception">The exception, when one was logged.</param>
    /// <param name="formatter">The formatter that produced the message.</param>
    internal void Write<TState>(
        NacosDeferredLogger logger,
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            // The logger may have read a stale null before the bind completed. Checking again under
            // the lock is what keeps such an entry from being queued after the replay already ran,
            // which would leave it in the buffer forever.
            if (logger.Inner is { } inner)
            {
                inner.Log(logLevel, eventId, state, exception, formatter);
                return;
            }

            if (_buffered.Count == MaxBufferedEntries)
            {
                _buffered.Dequeue();
            }

            _buffered.Enqueue(
                new BufferedEntry<TState>(logger, logLevel, eventId, state, exception, formatter));
        }
    }

    /// <summary>
    /// One log call kept until the host's factory is available.
    /// </summary>
    private abstract class BufferedEntry
    {
        /// <summary>Writes the entry through the logger it came from.</summary>
        public abstract void Replay();
    }

    /// <summary>
    /// A buffered log call that keeps the caller's state and formatter, not just the rendered text.
    /// </summary>
    /// <typeparam name="TState">The type of the state object.</typeparam>
    private sealed class BufferedEntry<TState>(
        NacosDeferredLogger logger,
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) : BufferedEntry
    {
        /// <inheritdoc />
        public override void Replay() => logger.Forward(logLevel, eventId, state, exception, formatter);
    }
}
