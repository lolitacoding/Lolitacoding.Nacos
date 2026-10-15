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
internal sealed partial class NacosAuthJsonContext : JsonSerializerContext
{
}
