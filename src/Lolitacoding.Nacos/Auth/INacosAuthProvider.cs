namespace Lolitacoding.Nacos.Auth;

/// <summary>
/// Supplies the access token presented on outgoing Nacos requests.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam that keeps authentication out of the transport. The transport asks for a
/// token before each request and reports rejection, and never learns how the token was obtained.
/// That leaves room for credential schemes beyond user name and password — notably the
/// AccessKey/SecretKey signing used by Alibaba Cloud MSE — without touching the transport.
/// </para>
/// <para>
/// Implementations must be safe for concurrent use, because every request asks for the token.
/// </para>
/// </remarks>
internal interface INacosAuthProvider : IDisposable
{
    /// <summary>
    /// Gets a currently valid access token, logging in or refreshing when necessary.
    /// </summary>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>
    /// The token to present, or <see langword="null"/> when the client is configured for
    /// anonymous access.
    /// </returns>
    /// <exception cref="NacosAuthException">The server rejected the configured credentials.</exception>
    ValueTask<string?> GetAccessTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// Discards the cached token so the next request triggers a fresh login.
    /// </summary>
    /// <remarks>
    /// Called when the server rejects a token, which happens when it expired early — for instance
    /// because a cluster node was restarted with a different signing key.
    /// </remarks>
    void Invalidate();
}

/// <summary>
/// An <see cref="INacosAuthProvider"/> for servers that do not require authentication.
/// </summary>
/// <remarks>
/// Nacos defaults the client OpenAPI scope to unauthenticated, so this is the common case and
/// avoids an unnecessary login round trip on every startup.
/// </remarks>
internal sealed class AnonymousNacosAuthProvider : INacosAuthProvider
{
    /// <summary>Gets the shared instance, which holds no state.</summary>
    public static AnonymousNacosAuthProvider Instance { get; } = new();

    private AnonymousNacosAuthProvider()
    {
    }

    /// <inheritdoc />
    public ValueTask<string?> GetAccessTokenAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<string?>(null);

    /// <inheritdoc />
    public void Invalidate()
    {
        // Nothing is cached, so there is nothing to discard.
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // The anonymous provider holds no resources.
    }
}
