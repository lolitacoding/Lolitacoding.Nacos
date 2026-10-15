using Microsoft.Extensions.Options;

namespace Lolitacoding.Nacos;

/// <summary>
/// Validates <see cref="NacosOptions"/> when they are resolved from an options container.
/// </summary>
/// <remarks>
/// The rules live in the core library rather than in each host integration so that every consumer
/// — dependency injection, the configuration provider, the ASP.NET Core registrar — rejects the
/// same invalid options with the same message, and a caller cannot bypass validation by choosing a
/// different entry point.
/// </remarks>
public sealed class NacosOptionsValidator : IValidateOptions<NacosOptions>
{
    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public ValidateOptionsResult Validate(string? name, NacosOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            options.Validate();
            return ValidateOptionsResult.Success;
        }
        catch (NacosConfigurationException ex)
        {
            // The options container turns this into an OptionsValidationException that names the
            // failing type, so the message itself only has to say what is wrong.
            return ValidateOptionsResult.Fail(ex.Message);
        }
    }
}
