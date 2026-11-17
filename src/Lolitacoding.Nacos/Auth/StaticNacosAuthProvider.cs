namespace Lolitacoding.Nacos.Auth;

/// <summary>
/// Presents an access token that was obtained outside the client.
/// </summary>
/// <remarks>
/// <para>
/// A token minted by a sidecar or an external identity service cannot be refreshed by logging in
/// again, so this provider returns the configured value unchanged. When the server rejects it, the
/// transport retries once and then surfaces the rejection: that is the correct contract for a
/// pre-issued credential, because the alternative would be an endless loop presenting the same
/// token.
/// </para>
/// <para>
/// Use <see cref="NacosOptions.AuthProvider"/> instead when the token has to be refreshed by the
/// caller's own logic.
/// </para>
/// </remarks>
internal sealed class StaticNacosAuthProvider(string accessToken) : INacosAuthProvider
{
    private readonly string _accessToken = accessToken;

    /// <inheritdoc />
    public ValueTask<string?> GetAccessTokenAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<string?>(_accessToken);

    /// <inheritdoc />
    public void Invalidate()
    {
        // Nothing to discard: the token belongs to the caller and cannot be re-minted here.
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // The token is a string, so there is nothing to release.
    }
}
