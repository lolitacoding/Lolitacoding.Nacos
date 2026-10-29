using Lolitacoding.Nacos.Protocol.Messages;

namespace Lolitacoding.Nacos.Transport;

/// <summary>
/// The module a gRPC connection is bound to.
/// </summary>
/// <remarks>
/// <para>
/// Nacos routes a connection by the <c>module</c> label sent with
/// <c>ConnectionSetupRequest</c>. The naming, lock and AI servers each keep a registry of the
/// connections that declared them and ignore every other connection, so a connection can serve
/// exactly one module. The client therefore opens one connection per module rather than
/// multiplexing every feature over a single stream.
/// </para>
/// <para>
/// The label values are the same strings Nacos uses for the <c>module</c> field of a request body;
/// they come from <c>com.alibaba.nacos.api.common.Constants</c>.
/// </para>
/// </remarks>
internal enum NacosConnectionModule
{
    /// <summary>
    /// The configuration centre. Nacos does not require the label, but the official client sends
    /// it so the connection is attributed to the right module.
    /// </summary>
    Config,

    /// <summary>The naming service. The server discards connections that do not declare it.</summary>
    Naming,

    /// <summary>The distributed lock service. The server discards connections that do not declare it.</summary>
    Lock,

    /// <summary>The AI registry. The server discards connections that do not declare it.</summary>
    AI,
}

/// <summary>
/// Translates a <see cref="NacosConnectionModule"/> to the value Nacos expects on the wire.
/// </summary>
internal static class NacosConnectionModuleExtensions
{
    /// <summary>
    /// Gets the value of the <c>module</c> connection label for a module.
    /// </summary>
    /// <param name="module">The module.</param>
    /// <returns>The label value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The module is not one this client knows.</exception>
    public static string ToLabel(this NacosConnectionModule module) => module switch
    {
        NacosConnectionModule.Config => NacosModule.Config,
        NacosConnectionModule.Naming => NacosModule.Naming,
        NacosConnectionModule.Lock => NacosModule.Lock,
        NacosConnectionModule.AI => NacosModule.AI,
        _ => throw new ArgumentOutOfRangeException(
            nameof(module), module, "The Nacos connection module has no known label."),
    };
}
