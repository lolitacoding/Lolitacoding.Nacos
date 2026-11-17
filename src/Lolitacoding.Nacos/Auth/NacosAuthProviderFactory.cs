using Lolitacoding.Nacos.Internal;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Auth;

/// <summary>
/// Builds the authentication provider a connection should use for a given set of options.
/// </summary>
/// <remarks>
/// This exists so the decision "does this configuration need authentication at all?" lives in one
/// place. Every request asks its provider for a token, so returning the anonymous provider for an
/// unauthenticated deployment is what keeps that path free of both a login round trip and a null
/// check.
/// </remarks>
internal static class NacosAuthProviderFactory
{
    /// <summary>
    /// Creates the auth provider appropriate for the supplied options.
    /// </summary>
    /// <param name="options">The client options.</param>
    /// <param name="servers">The servers that may be used for authentication.</param>
    /// <param name="httpClient">The client used for login requests.</param>
    /// <param name="timeProvider">The clock used for token expiry decisions.</param>
    /// <param name="logger">The logger for authentication diagnostics.</param>
    /// <param name="random">The random source used to pick the token refresh window.</param>
    /// <returns>
    /// The caller's provider when one is configured, a static provider when a pre-issued token is
    /// set, an OpenID Connect provider when OIDC is configured, a RAM signer when RAM is
    /// configured, a logging-in provider when credentials are configured, otherwise the stateless
    /// anonymous provider.
    /// </returns>
    public static INacosAuthProvider Create(
        NacosOptions options,
        IReadOnlyList<NacosServerAddress> servers,
        HttpClient httpClient,
        TimeProvider timeProvider,
        ILogger logger,
        Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        if (options.AuthProvider is not null)
        {
            // The caller owns the provider and its lifetime, so it is handed back unchanged rather
            // than wrapped: wrapping would hide the instance from a caller that wants to dispose it.
            return options.AuthProvider;
        }

        if (options.HasAccessToken)
        {
            // A pre-issued token needs no login round trip and no server list to log in against.
            return new StaticNacosAuthProvider(options.AccessToken!);
        }

        if (options.Oidc is not null)
        {
            // OpenID Connect mints the token at the identity provider, so the Nacos server list
            // plays no part in authentication.
            return new NacosOidcAuthProvider(options.Oidc, httpClient, timeProvider, logger, random);
        }

        if (options.Ram is not null)
        {
            // RAM signs each request with the access key, so there is no token to obtain and no
            // server list to obtain it from.
            return new NacosRamAuthProvider(options.Ram, timeProvider, options.Namespace);
        }

        if (!options.HasCredentials)
        {
            // Nacos defaults the client OpenAPI scope to unauthenticated, so this is the common
            // case and must not cost a login request.
            return AnonymousNacosAuthProvider.Instance;
        }

        if (servers.Count == 0)
        {
            throw new ArgumentException(
                "At least one Nacos server address is required to authenticate.", nameof(servers));
        }

        return new NacosTokenProvider(
            options,
            servers,
            new NacosHttpLoginClient(httpClient),
            timeProvider,
            logger,
            random);
    }
}
