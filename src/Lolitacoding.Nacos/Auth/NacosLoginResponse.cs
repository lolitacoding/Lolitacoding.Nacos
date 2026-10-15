using System.Text.Json.Serialization;

namespace Lolitacoding.Nacos.Auth;

/// <summary>
/// The body returned by the Nacos login endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Nacos returns this object unwrapped — there is no <c>{code, message, data}</c> envelope on
/// the login endpoints, unlike the rest of the OpenAPI. That is why this type is decoded
/// directly rather than through the shared response envelope.
/// </para>
/// <para>
/// The same shape is returned by both the current <c>/v3/auth/user/login</c> endpoint and the
/// legacy <c>/v1/auth/users/login</c> endpoint, so one type covers both. Only
/// <see cref="AccessToken"/> and <see cref="TokenTtl"/> are required for operation; the
/// remaining fields are surfaced for diagnostics.
/// </para>
/// </remarks>
internal sealed class NacosLoginResponse
{
    /// <summary>Gets or sets the bearer token to present on subsequent requests.</summary>
    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; set; }

    /// <summary>Gets or sets the lifetime of <see cref="AccessToken"/>, in seconds.</summary>
    /// <remarks>
    /// The official client treats a response without this field as fatal rather than assuming a
    /// default, because guessing the lifetime risks either refreshing far too often or serving an
    /// expired token. This client follows that behaviour.
    /// </remarks>
    [JsonPropertyName("tokenTtl")]
    public long TokenTtl { get; set; }

    /// <summary>Gets or sets a value indicating whether the account is a global administrator.</summary>
    [JsonPropertyName("globalAdmin")]
    public bool GlobalAdmin { get; set; }

    /// <summary>Gets or sets the user name the server authenticated.</summary>
    [JsonPropertyName("username")]
    public string? UserName { get; set; }
}
