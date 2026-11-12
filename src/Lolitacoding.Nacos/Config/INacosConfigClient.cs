namespace Lolitacoding.Nacos.Config;

/// <summary>
/// Reads, publishes and listens for Nacos configurations.
/// </summary>
/// <remarks>
/// <para>
/// All members accept an optional namespace through <see cref="NacosConfigKey"/> and fall back to
/// <see cref="NacosOptions.Namespace"/> when one is not supplied, so the common single-namespace
/// deployment does not have to repeat itself on every call.
/// </para>
/// <para>
/// Implementations are expected to be safe for concurrent use, since a hosted application will
/// serve many requests through one client.
/// </para>
/// </remarks>
public interface INacosConfigClient : IAsyncDisposable
{
    /// <summary>
    /// Gets whether the client currently has a usable connection to a Nacos server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The answer is read from the transport's state, so asking costs nothing and never blocks on
    /// the network: this is the property to poll from a health check instead of issuing a read.
    /// <see cref="NacosServerStatus.Up"/> means the connection is established and the handshake
    /// completed, not that a particular configuration exists or that every request will succeed.
    /// </para>
    /// <para>
    /// This is the counterpart of the Java client's <c>ConfigService.getServerStatus()</c>, which
    /// reports <c>UP</c> when at least one server connection is running.
    /// </para>
    /// </remarks>
    NacosServerStatus ServerStatus { get; }

    /// <summary>
    /// Reads a configuration.
    /// </summary>
    /// <param name="key">The configuration to read.</param>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>
    /// The result, whose <see cref="NacosConfigResult.Content"/> is <see langword="null"/> when
    /// the configuration does not exist.
    /// </returns>
    /// <exception cref="NacosConnectionException">
    /// The server could not be reached and no local snapshot was available.
    /// </exception>
    /// <exception cref="NacosServerException">The server refused the request.</exception>
    Task<NacosConfigResult> GetConfigAsync(NacosConfigKey key, CancellationToken ct = default);

    /// <summary>
    /// Reads a configuration as a conditional read: the server skips the content when the caller
    /// already holds it.
    /// </summary>
    /// <param name="key">The configuration to read.</param>
    /// <param name="localMd5">
    /// The hash of the content the caller already holds, as reported by
    /// <see cref="NacosConfigResult.Md5"/>.
    /// </param>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>
    /// The result. When the server confirms that its content still hashes to
    /// <paramref name="localMd5"/>, <see cref="NacosConfigResult.Content"/> is the copy the caller
    /// already holds, restored from the local snapshot.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="localMd5"/> is <see langword="null"/>.</exception>
    /// <exception cref="NacosConnectionException">
    /// The server could not be reached and no local snapshot was available.
    /// </exception>
    /// <exception cref="NacosServerException">The server refused the request.</exception>
    /// <remarks>
    /// <para>
    /// This is the conditional read of the official Java client's
    /// <c>ConfigService.getConfig(GetConfigRequest)</c>, whose <c>localMd5</c> says "I already hold
    /// this version". The server answers <c>304 Not Modified</c> without the content when the hash
    /// still matches, and the content is then returned from the local snapshot instead of being
    /// downloaded again.
    /// </para>
    /// <para>
    /// The conditional request is sent only when this client can restore the content the hash
    /// belongs to: the local snapshot must exist and hash to <paramref name="localMd5"/>. Otherwise
    /// the read is unconditional, because a "not modified" answer with nothing to return would be
    /// worse than transferring the content. A snapshot requires
    /// <see cref="NacosOptions.LocalCacheDirectory"/> to be set.
    /// </para>
    /// <para>
    /// A caller that does not track hashes should use <see cref="GetConfigAsync(NacosConfigKey, CancellationToken)"/>;
    /// it never sends a conditional request.
    /// </para>
    /// </remarks>
    Task<NacosConfigResult> GetConfigConditionalAsync(
        NacosConfigKey key,
        string localMd5,
        CancellationToken ct = default);

    /// <summary>
    /// Creates or replaces a configuration.
    /// </summary>
    /// <param name="key">The configuration to publish.</param>
    /// <param name="content">The content to store.</param>
    /// <param name="contentType">
    /// The content type, or <see langword="null"/> to use the configured default.
    /// </param>
    /// <param name="casMd5">
    /// An optional compare-and-swap guard: when supplied, the publish is rejected unless the
    /// stored content still hashes to this value, making the write an optimistic-concurrency
    /// update.
    /// </param>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>A task that completes when the configuration is stored.</returns>
    /// <exception cref="NacosServerException">
    /// The server rejected the publish, for example because the compare-and-swap guard did not
    /// match.
    /// </exception>
    Task PublishConfigAsync(
        NacosConfigKey key,
        string content,
        string? contentType = null,
        string? casMd5 = null,
        CancellationToken ct = default);

    /// <summary>
    /// Adds a filter that transforms configuration content on the way in and out.
    /// </summary>
    /// <param name="filter">The filter to add.</param>
    /// <remarks>
    /// <para>
    /// Filters run in <see cref="INacosConfigFilter.Order"/> order, lowest first, on every publish
    /// and on every read that yields content — including a read served from a local snapshot. This
    /// is the counterpart of the official Java client's
    /// <c>ConfigService.addConfigFilter(IConfigFilter)</c>.
    /// </para>
    /// <para>
    /// A filter with a <see cref="INacosConfigFilter.Name"/> that is already registered is ignored,
    /// so the first registration wins. The chain is replaced atomically, so adding a filter while
    /// reads or publishes are in flight is safe.
    /// </para>
    /// <para>
    /// Encryption is normally enabled through <see cref="NacosConfigOptions.EncryptionProviders"/>,
    /// which seeds this chain with the built-in <see cref="NacosConfigEncryptionFilter"/>. Use this
    /// method for other transformations, or when the filter chain has to be assembled explicitly.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="filter"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="filter"/> has an empty <see cref="INacosConfigFilter.Name"/>.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    void AddConfigFilter(INacosConfigFilter filter);

    /// <summary>
    /// Removes a configuration.
    /// </summary>
    /// <param name="key">The configuration to remove.</param>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>
    /// A task that completes when the configuration is removed. Removing a configuration that does
    /// not exist succeeds, matching the server's behaviour.
    /// </returns>
    Task RemoveConfigAsync(NacosConfigKey key, CancellationToken ct = default);

    /// <summary>
    /// Observes a configuration until the caller stops enumerating or cancels.
    /// </summary>
    /// <param name="key">The configuration to observe.</param>
    /// <param name="ct">A token that ends the enumeration.</param>
    /// <returns>
    /// A stream of changes. The first element reports the value currently held, so a consumer can
    /// bind a default at startup and then react to later changes with one code path.
    /// </returns>
    /// <remarks>
    /// The stream never completes on its own: a configuration listener that quietly ended would
    /// leave an application serving stale values with no indication that anything was wrong.
    /// Transient server failures are retried rather than surfaced, and only cancellation or
    /// disposal ends the stream.
    /// </remarks>
    IAsyncEnumerable<NacosConfigChange> ListenAsync(NacosConfigKey key, CancellationToken ct = default);

    /// <summary>
    /// Registers a callback invoked on every change to a configuration.
    /// </summary>
    /// <param name="key">The configuration to observe.</param>
    /// <param name="onChange">The callback to invoke.</param>
    /// <param name="ct">A token that cancels the registration.</param>
    /// <returns>
    /// A handle that stops the observation when disposed. Dispose it during shutdown to release
    /// the registration promptly rather than waiting for the connection to close.
    /// </returns>
    /// <remarks>
    /// The callback is invoked on a background thread and may overlap with itself, so it must be
    /// safe for concurrent use. Any exception it throws is logged and swallowed: a faulty listener
    /// must not tear down the connection that other listeners depend on.
    /// </remarks>
    Task<IAsyncDisposable> AddListenerAsync(
        NacosConfigKey key,
        Func<NacosConfigChange, CancellationToken, Task> onChange,
        CancellationToken ct = default);

    /// <summary>
    /// Observes every configuration whose group and data id match a pair of patterns.
    /// </summary>
    /// <param name="dataIdPattern">
    /// The data id pattern. The supported forms are <c>*</c>, <c>prefix*</c>, <c>*suffix</c>,
    /// <c>*text*</c> and a literal; a <c>*</c> in the middle of the value matches nothing, which is
    /// the language the server itself implements.
    /// </param>
    /// <param name="groupPattern">The group pattern, in the same language.</param>
    /// <param name="ct">A token that ends the enumeration.</param>
    /// <returns>
    /// A stream of events. Every configuration that matches when the watch starts is reported as
    /// <see cref="NacosConfigWatchKind.Added"/>, followed by exactly one
    /// <see cref="NacosConfigWatchKind.InitialSyncCompleted"/>; afterwards a configuration that
    /// starts matching is reported as <see cref="NacosConfigWatchKind.Added"/> and one that stops
    /// matching as <see cref="NacosConfigWatchKind.Removed"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The namespace is <see cref="NacosOptions.Namespace"/>, because a fuzzy watch is scoped to one
    /// namespace. The stream never completes on its own: it is re-registered after a reconnect and
    /// resynchronised periodically, so only cancellation or disposal ends it.
    /// </para>
    /// <para>
    /// A fuzzy watch follows the matched set, not the content of its members. A Nacos server reports
    /// a configuration that starts or stops matching and does not push a content change to one that
    /// already matches, so <see cref="ListenAsync"/> remains the way to follow a value. The event
    /// carries coordinates only; a consumer that needs the value reads it with
    /// <see cref="GetConfigAsync"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="NacosServerException">
    /// The connected server states that it does not implement fuzzy watch, or refused the pattern
    /// because too many patterns or matches are already registered.
    /// </exception>
    /// <exception cref="NacosConnectionException">
    /// The server could not be reached and the watch could not be registered.
    /// </exception>
    IAsyncEnumerable<NacosConfigWatchEvent> WatchAsync(
        string dataIdPattern,
        string groupPattern,
        CancellationToken ct = default);

    /// <summary>
    /// Observes every configuration in a group, regardless of its data id.
    /// </summary>
    /// <param name="groupPattern">The group pattern.</param>
    /// <param name="ct">A token that ends the enumeration.</param>
    /// <returns>A stream of events, as described on the pattern overload.</returns>
    /// <remarks>
    /// This is the convenience form of <see cref="WatchAsync(string, string, CancellationToken)"/>
    /// with a data id pattern of <c>*</c>, matching the single-pattern entry point the official Java
    /// client exposes. The token is required rather than optional because the two overloads would
    /// otherwise be indistinguishable to callers who pass a pattern only; pass
    /// <see cref="CancellationToken.None"/> when no cancellation is needed.
    /// </remarks>
    IAsyncEnumerable<NacosConfigWatchEvent> WatchAsync(
        string groupPattern,
        CancellationToken ct);
}
