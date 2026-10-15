namespace Lolitacoding.Aspire.Hosting.Nacos;

/// <summary>
/// Environment variables understood by the <c>nacos/nacos-server</c> container image.
/// </summary>
internal static class NacosEnvironmentVariables
{
    /// <summary>Selects standalone or cluster mode.</summary>
    internal const string Mode = "MODE";

    /// <summary>Enables the authentication plugin when set to <see langword="true"/>.</summary>
    internal const string AuthEnable = "NACOS_AUTH_ENABLE";
}
