using Microsoft.Extensions.Configuration;

namespace Lolitacoding.Nacos.Extensions.Configuration;

/// <summary>
/// Adapts <see cref="NacosConfigurationSourceOptions"/> to the configuration system's source
/// abstraction.
/// </summary>
/// <remarks>
/// The source is a thin factory: it holds the validated options and creates a provider when the
/// configuration root is built. Keeping the state here rather than on the provider means the same
/// options can build a provider more than once, which is what happens when a builder is rebuilt.
/// </remarks>
internal sealed class NacosConfigurationSource : IConfigurationSource
{
    private readonly NacosConfigurationSourceOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosConfigurationSource"/> class.
    /// </summary>
    /// <param name="options">The validated source options.</param>
    public NacosConfigurationSource(NacosConfigurationSourceOptions options) => _options = options;

    /// <summary>
    /// Gets the validated options, exposed so tests can observe what the extension methods set.
    /// </summary>
    internal NacosConfigurationSourceOptions Options => _options;

    /// <inheritdoc />
    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new NacosConfigurationProvider(_options);
}
