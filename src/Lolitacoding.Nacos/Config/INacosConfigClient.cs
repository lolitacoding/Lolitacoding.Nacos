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
}
