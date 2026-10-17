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
/// health checking and load balancing apply to the MCP backend.
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
}
