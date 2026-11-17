using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lolitacoding.Nacos.Auth;

/// <summary>
/// Source-generated JSON contracts for the authentication HTTP API.
/// </summary>
/// <remarks>
/// This is deliberately separate from <c>NacosJsonContext</c>: the login endpoints are part of
/// the HTTP OpenAPI, not the gRPC envelope protocol, and they return a flat object rather than
/// the <c>{code, message, data}</c> envelope the rest of that API uses. Keeping the contract here
/// makes the authentication layer self-contained.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = false)]
[JsonSerializable(typeof(NacosLoginResponse))]
[JsonSerializable(typeof(OidcDiscoveryDocument))]
[JsonSerializable(typeof(OidcTokenResponse))]
internal sealed partial class NacosAuthJsonContext : JsonSerializerContext
{
}

/// <summary>
/// The part of an OIDC discovery document this client reads.
/// </summary>
/// <remarks>
/// The document is defined by OpenID Connect Discovery and carries much more than this; only the
/// token endpoint is needed to perform the client-credentials grant.
/// </remarks>
internal sealed class OidcDiscoveryDocument
{
    /// <summary>Gets or sets the endpoint that issues access tokens.</summary>
    [JsonPropertyName("token_endpoint")]
    public string? TokenEndpoint { get; set; }
}

/// <summary>
/// The token response of an OAuth2 client-credentials grant.
/// </summary>
internal sealed class OidcTokenResponse
{
    /// <summary>Gets or sets the access token.</summary>
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    /// <summary>
    /// Gets or sets the token lifetime in seconds.
    /// </summary>
    /// <remarks>
    /// Kept as a JSON element because identity providers disagree on whether <c>expires_in</c> is
    /// a number or a string, and both are accepted by the OAuth2 specification's consumers.
    /// </remarks>
    [JsonPropertyName("expires_in")]
    public JsonElement? ExpiresIn { get; set; }

    /// <summary>Gets or sets the token type, normally <c>Bearer</c>.</summary>
    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }
}
