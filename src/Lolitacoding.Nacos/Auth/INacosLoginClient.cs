using Lolitacoding.Nacos.Internal;

namespace Lolitacoding.Nacos.Auth;

/// <summary>
/// Performs the Nacos login handshake that exchanges credentials for an access token.
/// </summary>
/// <remarks>
/// This is separate from <see cref="INacosAuthProvider"/> so the caching and refresh policy —
/// which is where the subtle protocol rules live — can be tested without any network, while the
/// HTTP shape stays in one small implementation.
/// </remarks>
internal interface INacosLoginClient
{
    /// <summary>
    /// Requests a token from one server.
    /// </summary>
    /// <param name="server">The server to authenticate against.</param>
    /// <param name="userName">The configured user name.</param>
    /// <param name="password">The configured password.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>
    /// The parsed login response, or <see langword="null"/> when the server rejected the
    /// credentials.
    /// </returns>
    /// <exception cref="NacosConnectionException">The server could not be reached.</exception>
    ValueTask<NacosLoginResponse?> LoginAsync(
        NacosServerAddress server,
        string userName,
        string password,
        CancellationToken ct = default);
}
