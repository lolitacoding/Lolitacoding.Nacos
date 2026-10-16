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

    /// <summary>The key the server signs its tokens with, and verifies them against.</summary>
    internal const string AuthToken = "NACOS_AUTH_TOKEN";

    /// <summary>The key that identifies the server to its peers.</summary>
    internal const string AuthIdentityKey = "NACOS_AUTH_IDENTITY_KEY";

    /// <summary>The secret that identifies the server to its peers.</summary>
    internal const string AuthIdentityValue = "NACOS_AUTH_IDENTITY_VALUE";
}
