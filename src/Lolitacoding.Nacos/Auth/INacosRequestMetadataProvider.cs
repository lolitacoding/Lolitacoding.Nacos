using Lolitacoding.Nacos.Protocol.Messages;

namespace Lolitacoding.Nacos.Auth;

/// <summary>
/// Supplies per-request authentication metadata in addition to a token.
/// </summary>
/// <remarks>
/// <para>
/// A token identifies the caller for the whole connection, so it is fetched once and attached to
/// every request. RAM signing is different: the signature covers a resource string and a
/// timestamp, so it has to be recomputed for each request. The transport asks a provider that
/// implements this interface to decorate the request after the token has been attached.
/// </para>
/// <para>
/// This is internal because the only implementation is the built-in RAM signer. A caller that needs
/// another scheme can implement <see cref="INacosAuthProvider"/> for token-based authentication.
/// </para>
/// </remarks>
internal interface INacosRequestMetadataProvider
{
    /// <summary>
    /// Attaches the authentication headers a single request needs.
    /// </summary>
    /// <param name="request">The request about to be sent.</param>
    void ApplyRequestMetadata(NacosRequest request);
}
