using Microsoft.Extensions.Options;

namespace Lolitacoding.Nacos.AspNetCore;

/// <summary>
/// Validates <see cref="NacosServiceRegistrationOptions"/> when they are resolved.
/// </summary>
/// <remarks>
/// Validation is registered rather than performed inline so a misconfigured registration fails
/// when the options are first resolved — during startup — with the options system's standard
/// message that names the offending property.
/// </remarks>
internal sealed class NacosServiceRegistrationOptionsValidator
    : IValidateOptions<NacosServiceRegistrationOptions>
{
    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public ValidateOptionsResult Validate(string? name, NacosServiceRegistrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.GroupName))
        {
            return ValidateOptionsResult.Fail(
                "NacosServiceRegistrationOptions.GroupName cannot be empty.");
        }

        if (options.Port is < 1 or > 65535)
        {
            return ValidateOptionsResult.Fail(
                "NacosServiceRegistrationOptions.Port must be between 1 and 65535 when it is set.");
        }

        if (!double.IsFinite(options.Weight) || options.Weight <= 0)
        {
            return ValidateOptionsResult.Fail(
                "NacosServiceRegistrationOptions.Weight must be finite and greater than zero.");
        }

        if (options.ServiceName is not null && string.IsNullOrWhiteSpace(options.ServiceName))
        {
            return ValidateOptionsResult.Fail(
                "NacosServiceRegistrationOptions.ServiceName cannot be whitespace.");
        }

        return ValidateOptionsResult.Success;
    }
}
