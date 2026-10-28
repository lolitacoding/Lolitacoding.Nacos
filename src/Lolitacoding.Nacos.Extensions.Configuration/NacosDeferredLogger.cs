using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Extensions.Configuration;

/// <summary>
/// A logger that buffers until <see cref="NacosDeferredLoggerFactory"/> is bound and forwards to
/// the host's logger afterwards.
/// </summary>
/// <remarks>
/// The category is captured at creation and used to resolve the matching logger from the host's
/// factory at bind time, so the category an application filters on is preserved.
/// </remarks>
internal sealed class NacosDeferredLogger : ILogger
{
    private readonly NacosDeferredLoggerFactory _factory;
    private readonly string _category;

    private volatile ILogger? _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosDeferredLogger"/> class.
    /// </summary>
    /// <param name="factory">The factory that owns the buffer.</param>
    /// <param name="category">The category name.</param>
    internal NacosDeferredLogger(NacosDeferredLoggerFactory factory, string category)
    {
        _factory = factory;
        _category = category;
    }

    /// <summary>
    /// Gets the host's logger once the factory is bound, otherwise <see langword="null"/>.
    /// </summary>
    internal ILogger? Inner => _inner;

    /// <summary>
    /// Connects this logger to the host's factory.
    /// </summary>
    /// <param name="factory">The host's factory.</param>
    internal void Bind(ILoggerFactory factory) => _inner = factory.CreateLogger(_category);

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull =>
        // A scope is ambient state that only applies to calls made inside it. Replaying one would
        // attach it to the wrong moment, so it is simply not established before the bind.
        _inner?.BeginScope(state);

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel)
    {
        if (_inner is { } inner)
        {
            return inner.IsEnabled(logLevel);
        }

        // Before the bind only what explains a startup problem is worth keeping.
        return logLevel != LogLevel.None && logLevel >= LogLevel.Information;
    }

    /// <inheritdoc />
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

        if (_inner is { } inner)
        {
            inner.Log(logLevel, eventId, state, exception, formatter);
            return;
        }

        _factory.Write(this, logLevel, eventId, state, exception, formatter);
    }

    /// <summary>
    /// Writes a buffered entry through the host's logger.
    /// </summary>
    /// <typeparam name="TState">The type of the state object.</typeparam>
    /// <param name="logLevel">The level of the entry.</param>
    /// <param name="eventId">The event id of the entry.</param>
    /// <param name="state">The structured state.</param>
    /// <param name="exception">The exception, when one was logged.</param>
    /// <param name="formatter">The formatter that produced the message.</param>
    internal void Forward<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        // The bind can be undone only by disposal, and a disposed factory clears the buffer before
        // it can be replayed, so a missing inner here means there is nothing left to write to.
        _inner?.Log(logLevel, eventId, state, exception, formatter);
    }
}
