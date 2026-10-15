using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lolitacoding.Nacos.Extensions.Configuration;

/// <summary>
/// Configures the Nacos <see cref="Microsoft.Extensions.Configuration.IConfigurationProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately a smaller surface than <see cref="NacosOptions"/>: a configuration source
/// only needs to reach the server and name the data ids it layers, so exposing the transport
/// tuning here would invite applications to configure a second, divergent connection.
/// </para>
/// <para>
/// The source owns its own client rather than borrowing the dependency-injection one, because
/// configuration is read while the container is still being built — before any service, and in
/// particular before the hosted service that connects the shared client, could exist.
/// </para>
/// </remarks>
public sealed class NacosConfigurationSourceOptions
{
    /// <summary>
    /// Gets or sets the Nacos server addresses, for example <c>http://127.0.0.1:8848</c>.
    /// </summary>
    /// <remarks>At least one address is required.</remarks>
    public IList<string> ServerAddresses { get; set; } = [];

    /// <summary>
    /// Gets or sets the namespace (tenant) the data ids are read from.
    /// </summary>
    public string Namespace { get; set; } = NacosConstants.DefaultNamespace;

    /// <summary>
    /// Gets or sets the group the data ids belong to.
    /// </summary>
    public string GroupName { get; set; } = NacosConstants.DefaultGroup;

    /// <summary>
    /// Gets or sets the data ids to layer, in application order.
    /// </summary>
    /// <remarks>
    /// Later data ids override earlier ones for the same key, so a shared base document can be
    /// published first and an environment-specific override published after it.
    /// </remarks>
    public IList<string> DataIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the user name used to authenticate against a secured Nacos server.
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// Gets or sets the password used to authenticate against a secured Nacos server.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Gets or sets the directory used to store configuration snapshots for failover.
    /// </summary>
    /// <remarks>
    /// When the server is unreachable the last known value is served from here, so a process can
    /// still start during a control-plane outage.
    /// </remarks>
    public string? LocalCacheDirectory { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether an unreachable server is tolerated at startup.
    /// </summary>
    /// <remarks>
    /// When <see langword="false"/> (the default) a failure to read the configuration aborts the
    /// build, which is the correct default: an application that silently starts without the
    /// settings it was told to load is usually worse off than one that refuses to start.
    /// </remarks>
    public bool Optional { get; set; }

    /// <summary>
    /// Gets or sets the logger factory used for the provider's diagnostics.
    /// </summary>
    /// <remarks>
    /// Optional. A configuration provider has no container to resolve a logger from, so the host
    /// passes one here when it wants the source's connection failures to be visible.
    /// </remarks>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>
    /// Projects these settings onto the shared client options.
    /// </summary>
    /// <returns>The client options.</returns>
    internal NacosOptions ToClientOptions() => new()
    {
        // Copied rather than shared so a later mutation of this object cannot silently change the
        // options an already-created client is using.
        ServerAddresses = [.. ServerAddresses],
        Namespace = Namespace,
        UserName = UserName,
        Password = Password,
        LocalCacheDirectory = LocalCacheDirectory,
        Optional = Optional,
    };

    /// <summary>
    /// Validates the source configuration.
    /// </summary>
    /// <exception cref="NacosConfigurationException">A setting is missing or invalid.</exception>
    internal void Validate()
    {
        if (DataIds.Count == 0)
        {
            throw new NacosConfigurationException(
                "At least one data id must be configured on NacosConfigurationSourceOptions.DataIds.");
        }

        foreach (string dataId in DataIds)
        {
            if (string.IsNullOrWhiteSpace(dataId))
            {
                throw new NacosConfigurationException(
                    "NacosConfigurationSourceOptions.DataIds contains an empty entry.");
            }
        }

        if (string.IsNullOrWhiteSpace(GroupName))
        {
            throw new NacosConfigurationException(
                "NacosConfigurationSourceOptions.GroupName cannot be empty.");
        }

        // The client options own the server address and credential rules; reusing them here keeps
        // this source from drifting away from what the client itself accepts.
        ValidateOptionsResult result = new NacosOptionsValidator().Validate(name: null, ToClientOptions());

        if (result.Failed)
        {
            throw new NacosConfigurationException(
                string.Join(" ", result.Failures ?? []));
        }
    }
}
