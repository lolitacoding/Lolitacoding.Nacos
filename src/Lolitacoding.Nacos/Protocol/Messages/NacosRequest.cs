using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Lolitacoding.Nacos.Protocol.Messages;

/// <summary>
/// Names of the <c>module</c> discriminator that Nacos embeds in every request body.
/// </summary>
/// <remarks>
/// The same strings double as the <c>module</c> connection label, which Nacos uses to route a
/// connection to the manager that owns it; see
/// <see cref="Transport.NacosConnectionModuleExtensions.ToLabel"/>.
/// </remarks>
internal static class NacosModule
{
    /// <summary>The configuration centre module.</summary>
    public const string Config = "config";

    /// <summary>The naming (service discovery) module.</summary>
    public const string Naming = "naming";

    /// <summary>The distributed lock module.</summary>
    public const string Lock = "lock";

    /// <summary>The AI registry module.</summary>
    public const string AI = "ai";

    /// <summary>The transport-internal module.</summary>
    public const string Internal = "internal";
}

/// <summary>
/// Base type for every request the client sends to a Nacos server.
/// </summary>
/// <remarks>
/// <para>
/// Nacos serialises requests as plain Java beans, so the inherited <c>requestId</c> and
/// <c>module</c> getters land in the JSON body in addition to the values carried in the gRPC
/// metadata. Those are reproduced here so the wire form matches what the official clients send.
/// </para>
/// <para>
/// The request identifier doubles as the correlation key the client uses to match a response to
/// the call that produced it.
/// </para>
/// </remarks>
internal abstract class NacosRequest
{
    /// <summary>
    /// Gets or sets the request headers.
    /// </summary>
    /// <remarks>
    /// Not serialised as part of the body. The official client sends headers only in the gRPC
    /// metadata, and empties the bean's header map immediately before serialising the body so
    /// that the body carries an empty object. <see cref="WireHeaders"/> reproduces that empty
    /// object without mutating this collection, which keeps encoding thread-safe.
    /// </remarks>
    [JsonIgnore]
    public Dictionary<string, string> Headers { get; set; } = [];

    /// <summary>
    /// Gets the always-empty header object Nacos expects inside a request body.
    /// </summary>
    /// <remarks>
    /// This exists purely for wire fidelity. Nacos' own tests assert the presence of
    /// <c>"headers"</c> in the serialised request, and the server ignores the value because it
    /// reads the authoritative headers from the gRPC metadata.
    /// </remarks>
    [JsonPropertyName("headers")]
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "System.Text.Json only serialises instance properties, so this must stay an instance member.")]
    public Dictionary<string, string> WireHeaders => [];

    /// <summary>Gets or sets the correlation identifier for this request.</summary>
    public string? RequestId { get; set; }

    /// <summary>Gets or sets the Nacos module discriminator.</summary>
    public string Module { get; set; } = NacosModule.Internal;

    /// <summary>
    /// Looks up a header without regard to case.
    /// </summary>
    /// <param name="name">The header name.</param>
    /// <returns>The header value, or <see langword="null"/> when absent.</returns>
    /// <remarks>
    /// Nacos stores headers in a case-insensitive map, so a request that round-trips through the
    /// wire must not become sensitive to the casing a peer happened to use.
    /// </remarks>
    public string? GetHeader(string name)
    {
        foreach (KeyValuePair<string, string> pair in Headers)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Sets a header, replacing any existing entry that differs only by case.
    /// </summary>
    /// <param name="name">The header name.</param>
    /// <param name="value">The header value.</param>
    public void SetHeader(string name, string value)
    {
        string? existing = null;
        foreach (string key in Headers.Keys)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                existing = key;
                break;
            }
        }

        if (existing is not null)
        {
            Headers[existing] = value;
        }
        else
        {
            Headers[name] = value;
        }
    }

    /// <summary>
    /// Removes a header without regard to case.
    /// </summary>
    /// <param name="name">The header name.</param>
    /// <remarks>
    /// Needed when a credential is invalidated: a request object that is retried would otherwise
    /// re-send the token that was just rejected.
    /// </remarks>
    public void RemoveHeader(string name)
    {
        List<string>? matches = null;

        foreach (string key in Headers.Keys)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                (matches ??= []).Add(key);
            }
        }

        if (matches is null)
        {
            return;
        }

        foreach (string key in matches)
        {
            Headers.Remove(key);
        }
    }
}
