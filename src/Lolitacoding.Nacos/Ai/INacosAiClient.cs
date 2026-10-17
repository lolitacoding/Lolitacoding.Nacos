namespace Lolitacoding.Nacos.Ai;

/// <summary>
/// The entry point for programmatic access to the Nacos AI registry.
/// </summary>
/// <remarks>
/// <para>
/// The AI registry is where an application finds the models, prompts, skills and agents it is built
/// on. Like the distributed lock the client is opened on first use rather than when the
/// <see cref="Client.NacosClient"/> is created, so an application that never asks the registry for
/// anything pays nothing for it.
/// </para>
/// <para>
/// The registry is grouped by resource because each resource has its own lifecycle and its own
/// listener contract; <see cref="Mcp"/> is the MCP server registry.
/// </para>
/// <para>
/// A client is safe for concurrent use and is owned by the <see cref="Client.NacosClient"/> that
/// created it.
/// </para>
/// </remarks>
public interface INacosAiClient : IAsyncDisposable
{
    /// <summary>Gets the MCP server registry.</summary>
    INacosMcpRegistry Mcp { get; }
}

/// <summary>
/// The MCP server registry: where MCP servers are published and discovered.
/// </summary>
/// <remarks>
/// <para>
/// An MCP server is registered under a name and served one version at a time. Releasing a version
/// is what makes it available; the version labelled <c>latest</c> is what a query without a version
/// resolves to, and Nacos moves that label when a new version is released.
/// </para>
/// <para>
/// The registry also routes MCP calls: a server is published with either a fixed address or a Nacos
/// service reference, and Nacos resolves the reference through service discovery so that ordinary
/// health checking and load balancing apply to the MCP backend. Publishing the address an instance
/// runs at is <see cref="RegisterServerEndpointAsync"/>, which is how a server that is already
/// running tells Nacos where to reach it.
/// </para>
/// <para>
/// Nacos has no MCP change notification, so following a server is a re-read on an interval rather
/// than a server-initiated push; see <see cref="SubscribeServerAsync"/>.
/// </para>
/// </remarks>
public interface INacosMcpRegistry
{
    /// <summary>
    /// Reads one version of an MCP server.
    /// </summary>
    /// <param name="name">The server name.</param>
    /// <param name="version">The version to read, or <see langword="null"/> for the latest one.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The resolved server detail.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or whitespace.</exception>
    /// <exception cref="NacosServerException">
    /// The server is unknown, the version does not exist, or the server does not implement the MCP
    /// registry.
    /// </exception>
    Task<NacosMcpServerDetail> GetServerAsync(
        string name,
        string? version = null,
        CancellationToken ct = default);

    /// <summary>
    /// Creates an MCP server, or a new version of an existing one.
    /// </summary>
    /// <param name="server">The server specification.</param>
    /// <param name="tools">The tools the version publishes, or <see langword="null"/> for none.</param>
    /// <param name="resources">The resources the version publishes, or <see langword="null"/> for none.</param>
    /// <param name="endpoint">
    /// How Nacos reaches the backend, or <see langword="null"/> to have Nacos register a service
    /// reference for the server.
    /// </param>
    /// <param name="createDraft">
    /// Whether to store the version as a lifecycle draft rather than serving it.
    /// </param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>
    /// The internal identifier Nacos assigned to the server, or <see langword="null"/> when the
    /// reply carried none.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="server"/> is null, its name is null or whitespace, or it carries no version.
    /// </exception>
    /// <exception cref="NacosServerException">
    /// The version already exists, the specification is rejected, or the server does not implement
    /// the MCP registry — or the draft ability, when a draft was requested.
    /// </exception>
    /// <remarks>
    /// The version is taken from <see cref="NacosMcpServer.VersionDetail"/> and falls back to
    /// <see cref="NacosMcpServer.Version"/>. Releasing a version that already exists is refused
    /// rather than overwritten, so a published version cannot be replaced under the clients that
    /// are already calling it.
    /// </remarks>
    Task<string?> ReleaseServerAsync(
        NacosMcpServer server,
        NacosMcpToolSpecification? tools = null,
        NacosMcpResourceSpecification? resources = null,
        NacosMcpEndpointSpec? endpoint = null,
        bool createDraft = false,
        CancellationToken ct = default);

    /// <summary>
    /// Subscribes to one version of an MCP server.
    /// </summary>
    /// <param name="name">The server name.</param>
    /// <param name="version">The version to watch, or <see langword="null"/> for the latest one.</param>
    /// <param name="ct">
    /// A token that ends the subscription. Leaving the enumerable ends it too.
    /// </param>
    /// <returns>
    /// The current detail, then the detail again on every change. A server that has not been
    /// released yet yields nothing until it appears.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or whitespace.</exception>
    /// <exception cref="NacosServerException">
    /// The read that seeds the subscription failed for a reason other than the server not existing,
    /// or the server does not implement the MCP registry.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Nacos does not push MCP changes, so a subscription is served by re-reading the server on the
    /// interval configured by <see cref="NacosAiOptions.CacheRefreshInterval"/> and yielding the
    /// result when it differs from the copy that was cached. A read that fails does not end the
    /// subscription: the last known detail stays cached and the read is retried, so a watcher
    /// survives a control-plane outage instead of failing with it.
    /// </para>
    /// <para>
    /// Subscribing without a version watches the version Nacos labels <c>latest</c>, which moves
    /// when a newer version is released. Subscribing to an exact version watches that version, and
    /// reports when it stops being the latest one.
    /// </para>
    /// </remarks>
    IAsyncEnumerable<NacosMcpServerDetail> SubscribeServerAsync(
        string name,
        string? version = null,
        CancellationToken ct = default);

    /// <summary>
    /// Publishes a runtime endpoint for an MCP server.
    /// </summary>
    /// <param name="name">The server name.</param>
    /// <param name="address">The endpoint address.</param>
    /// <param name="port">The endpoint port.</param>
    /// <param name="version">
    /// The version the endpoint serves, or <see langword="null"/> to serve every version.
    /// </param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once Nacos accepted the endpoint.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> or <paramref name="address"/> is null or whitespace.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> is not 1 to 65535.</exception>
    /// <exception cref="NacosServerException">
    /// The server is unknown or publishes no service reference to attach the endpoint to, or the
    /// server does not implement the MCP registry.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Nacos stores the endpoint as an instance of the service the server's <c>REF</c> endpoint
    /// names, so it is discovered, health checked and load balanced like any other instance. That
    /// also means the endpoint belongs to the connection that registered it: Nacos removes it when
    /// the connection closes, and this client republishes it after a reconnect.
    /// </para>
    /// <para>
    /// The caller is the publisher, so a server that is registered with a fixed address
    /// (<c>DIRECT</c>) has no service reference and cannot take an endpoint this way.
    /// </para>
    /// </remarks>
    Task RegisterServerEndpointAsync(
        string name,
        string address,
        int port,
        string? version = null,
        CancellationToken ct = default);

    /// <summary>
    /// Removes a runtime endpoint of an MCP server.
    /// </summary>
    /// <param name="name">The server name.</param>
    /// <param name="address">The endpoint address.</param>
    /// <param name="port">The endpoint port.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once Nacos acknowledged the removal.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> or <paramref name="address"/> is null or whitespace.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> is not 1 to 65535.</exception>
    /// <exception cref="NacosServerException">The server does not implement the MCP registry.</exception>
    /// <remarks>
    /// The caller identifies the endpoint by address and port, and the version it was published for
    /// is repeated from what this client recorded, because Nacos resolves the service to remove the
    /// instance from by that version. Removing an endpoint that is not there is not an error.
    /// </remarks>
    Task DeregisterServerEndpointAsync(
        string name,
        string address,
        int port,
        CancellationToken ct = default);
}
