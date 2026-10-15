using Microsoft.Extensions.Options;

namespace Lolitacoding.Nacos.Extensions.ServiceDiscovery;

/// <summary>
/// Validates <see cref="NacosServiceDiscoveryOptions"/> when they are resolved.
/// </summary>
/// <remarks>
/// Validation is registered rather than performed inline so a misconfigured provider fails when
/// the options are first resolved, during startup, with the options system's standard message that
/// names the offending property.
/// </remarks>
internal sealed class NacosServiceDiscoveryOptionsValidator
    : IValidateOptions<NacosServiceDiscoveryOptions>
{
    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public ValidateOptionsResult Validate(string? name, NacosServiceDiscoveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.GroupName))
        {
            return ValidateOptionsResult.Fail(
                "NacosServiceDiscoveryOptions.GroupName cannot be empty.");
        }

        if (options.Schemes is null)
        {
            return ValidateOptionsResult.Fail(
                "NacosServiceDiscoveryOptions.Schemes cannot be null; use an empty list to resolve only bare service names.");
        }

        foreach (string scheme in options.Schemes)
        {
            if (string.IsNullOrWhiteSpace(scheme))
            {
                return ValidateOptionsResult.Fail(
                    "NacosServiceDiscoveryOptions.Schemes contains an empty entry; remove it or give it a value.");
            }
        }

        if (options.Cluster is not null && string.IsNullOrWhiteSpace(options.Cluster))
        {
            return ValidateOptionsResult.Fail(
                "NacosServiceDiscoveryOptions.Cluster cannot be whitespace when it is set.");
        }

        return ValidateOptionsResult.Success;
    }
}
