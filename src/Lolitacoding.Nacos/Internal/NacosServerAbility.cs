namespace Lolitacoding.Nacos.Internal;

/// <summary>
/// The ability keys a Nacos client and server negotiate that are not specific to one module.
/// </summary>
/// <remarks>
/// The names come from <c>com.alibaba.nacos.api.ability.constant.AbilityKey</c>. A key that is
/// absent from a server's handshake acknowledgement means the server did not state the ability,
/// which is not the same as stating it is unsupported, so a client may only refuse a request when
/// the key is present and <see langword="false"/>.
/// </remarks>
internal static class NacosServerAbility
{
    /// <summary>The server pushes fuzzy watch events for naming and configuration.</summary>
    public const string FuzzyWatch = "fuzzyWatch";
}
