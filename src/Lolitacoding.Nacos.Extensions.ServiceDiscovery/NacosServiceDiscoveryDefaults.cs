namespace Lolitacoding.Nacos.Extensions.ServiceDiscovery;

/// <summary>
/// Constants shared by the service discovery integration.
/// </summary>
internal static class NacosServiceDiscoveryDefaults
{
    /// <summary>The URI scheme that selects the Nacos provider.</summary>
    public const string Scheme = "nacos";

    /// <summary>The display name reported by the provider in logs and diagnostics.</summary>
    public const string ProviderName = "Nacos";
}
