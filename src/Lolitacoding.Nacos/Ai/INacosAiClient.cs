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

    /// <summary>Gets the prompt registry.</summary>
    INacosPromptRegistry Prompt { get; }

    /// <summary>Gets the skill registry.</summary>
    INacosSkillRegistry Skill { get; }

    /// <summary>Gets the agent-specification registry.</summary>
    INacosAgentSpecRegistry AgentSpec { get; }
}

/// <summary>
/// The prompt registry: where the prompt templates an application renders are published and read.
/// </summary>
/// <remarks>
/// <para>
/// A prompt is registered under a key and served one version at a time. A read may name a version, or
/// name a label that Nacos moves between versions, or name neither and take the version labelled
/// <c>latest</c>; the version in the answer is the one that was resolved, so a caller can read a
/// label without knowing where it points.
/// </para>
/// <para>
/// Nacos has no prompt change notification either, so following a prompt is a re-read on an interval
/// rather than a server-initiated push; see <see cref="SubscribePromptAsync"/>. Unlike an MCP server
/// a prompt that stops being served is reported, because a missing prompt is distinguishable from
/// one that has not appeared yet.
/// </para>
/// </remarks>
public interface INacosPromptRegistry
{
    /// <summary>
    /// Reads one version of a prompt.
    /// </summary>
    /// <param name="promptKey">The prompt key.</param>
    /// <param name="version">The version to read, or <see langword="null"/> to resolve one.</param>
    /// <param name="label">
    /// The label to resolve, or <see langword="null"/> to resolve from the version and then from
    /// <c>latest</c>. Ignored when <paramref name="version"/> names a version.
    /// </param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The resolved prompt.</returns>
    /// <exception cref="ArgumentException"><paramref name="promptKey"/> is null or whitespace.</exception>
    /// <exception cref="NacosServerException">
    /// The prompt is unknown, the version is not published, or the server does not serve the prompt
    /// that was named.
    /// </exception>
    /// <remarks>
    /// The version wins when both a version and a label are given, matching the server, which
    /// resolves the two selectors in that order. An unknown label falls back to the latest version
    /// rather than failing, whereas an unknown version is an error.
    /// </remarks>
    Task<NacosPrompt> GetPromptAsync(
        string promptKey,
        string? version = null,
        string? label = null,
        CancellationToken ct = default);

    /// <summary>
    /// Subscribes to one version of a prompt.
    /// </summary>
    /// <param name="promptKey">The prompt key.</param>
    /// <param name="version">The version to watch, or <see langword="null"/> to resolve one.</param>
    /// <param name="label">
    /// The label to resolve, or <see langword="null"/> to resolve from the version and then from
    /// <c>latest</c>. Ignored when <paramref name="version"/> names a version.
    /// </param>
    /// <param name="ct">
    /// A token that ends the subscription. Leaving the enumerable ends it too.
    /// </param>
    /// <returns>
    /// The current prompt as the first element — or <see langword="null"/> when it is not published —
    /// then the prompt again on every change, with <see langword="null"/> reporting that it stopped
    /// being served.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="promptKey"/> is null or whitespace.</exception>
    /// <exception cref="NacosServerException">
    /// The read that seeds the subscription failed for a reason other than the prompt not existing.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Nacos does not push prompt changes, so a subscription is served by re-reading the prompt on
    /// the interval configured by <see cref="NacosAiOptions.CacheRefreshInterval"/> and yielding the
    /// result when it differs from the copy that was cached. The read is conditional on the hash of
    /// the cached copy, so an unchanged prompt costs a round trip rather than a body. A read that
    /// fails does not end the subscription: the last known prompt stays cached and the read is
    /// retried, so a watcher survives a control-plane outage instead of failing with it.
    /// </para>
    /// <para>
    /// A prompt the server stops serving is reported as a change whose element is
    /// <see langword="null"/>, so a subscriber does not keep rendering content that is gone.
    /// </para>
    /// </remarks>
    IAsyncEnumerable<NacosPrompt?> SubscribePromptAsync(
        string promptKey,
        string? version = null,
        string? label = null,
        CancellationToken ct = default);
}

/// <summary>
/// The skill registry: where the skill directories an agent loads are published and read.
/// </summary>
/// <remarks>
/// <para>
/// A skill is not a document but a directory — instructions in <c>SKILL.md</c> plus the files they
/// refer to — and Nacos serves it as a ZIP archive. The archive's entries are prefixed with the skill
/// name, so extracting it produces a directory named after the skill.
/// </para>
/// <para>
/// A read may name a version, or name a label that Nacos moves between versions, or name neither and
/// take the version labelled <c>latest</c>; the version in the answer is the one that was resolved.
/// Unlike a prompt, a label that does not resolve is not answered with the latest version — the
/// server reports the version as missing — so a typo is an error rather than a silent substitution.
/// </para>
/// <para>
/// Skills are served over the AI module's HTTP endpoint only; the server has no gRPC request for
/// them and the official client refuses one. Reading a skill therefore does not open the AI
/// connection, and needs only a reachable HTTP endpoint.
/// </para>
/// </remarks>
public interface INacosSkillRegistry
{
    /// <summary>
    /// Reads one version of a skill.
    /// </summary>
    /// <param name="skillName">The skill name.</param>
    /// <param name="version">The version to read, or <see langword="null"/> to resolve one.</param>
    /// <param name="label">
    /// The label to resolve, or <see langword="null"/> to resolve from the version and then from
    /// <c>latest</c>. Ignored when <paramref name="version"/> names a version.
    /// </param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The resolved skill, with its archive and the hash of the published content.</returns>
    /// <exception cref="ArgumentException"><paramref name="skillName"/> is null or whitespace.</exception>
    /// <exception cref="NacosAuthException">The server rejected the request's credentials.</exception>
    /// <exception cref="NacosServerException">
    /// The skill is unknown, the version is not published, the body is not a safe ZIP archive, or the
    /// server does not serve the skill registry (a server older than Nacos 3.2).
    /// </exception>
    /// <remarks>
    /// The version wins when both a version and a label are given, matching the server. The archive is
    /// validated before it is returned: a body that is not a ZIP at all, or one whose entries would
    /// extract outside the skill's directory, is refused rather than handed to an extractor.
    /// </remarks>
    Task<NacosSkill> GetSkillAsync(
        string skillName,
        string? version = null,
        string? label = null,
        CancellationToken ct = default);

    /// <summary>
    /// Subscribes to one version of a skill.
    /// </summary>
    /// <param name="skillName">The skill name.</param>
    /// <param name="version">The version to watch, or <see langword="null"/> to resolve one.</param>
    /// <param name="label">
    /// The label to resolve, or <see langword="null"/> to resolve from the version and then from
    /// <c>latest</c>. Ignored when <paramref name="version"/> names a version.
    /// </param>
    /// <param name="ct">A token that ends the subscription. Leaving the enumerable ends it too.</param>
    /// <returns>
    /// The current skill as the first element — or <see langword="null"/> when it is not published —
    /// then the skill again on every change, with <see langword="null"/> reporting that it stopped
    /// being served.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="skillName"/> is null or whitespace.</exception>
    /// <exception cref="NacosServerException">
    /// The read that seeds the subscription failed for a reason other than the skill not existing.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Nacos does not push skill changes, so a subscription is served by re-reading the skill on the
    /// interval configured by <see cref="NacosAiOptions.CacheRefreshInterval"/> and yielding the
    /// result when it differs from the archive that was cached. The read is conditional on the hash
    /// of the cached copy, so an unchanged skill costs a round trip rather than an archive. A read
    /// that fails does not end the subscription: the last known archive stays cached and the read is
    /// retried, so a watcher survives a control-plane outage instead of failing with it.
    /// </para>
    /// <para>
    /// Two archives are compared byte for byte rather than by their hashes, because a skill that
    /// arrived without one still has content and a change to it must not go unnoticed.
    /// </para>
    /// <para>
    /// A skill the server stops serving is reported as a change whose element is
    /// <see langword="null"/>, so a subscriber does not keep loading content that is gone.
    /// </para>
    /// </remarks>
    IAsyncEnumerable<NacosSkill?> SubscribeSkillAsync(
        string skillName,
        string? version = null,
        string? label = null,
        CancellationToken ct = default);
}

/// <summary>
/// The agent-specification registry: where the packages an agent worker is built from are published
/// and read.
/// </summary>
/// <remarks>
/// <para>
/// An agent specification is a package: a <c>manifest.json</c> that describes the worker, plus the
/// resource files it loads. Nacos serves one version at a time as a single JSON object, and the
/// version and the hash of the published content are reported in the response headers rather than in
/// the body.
/// </para>
/// <para>
/// A read may name a version, a label Nacos moves between versions, or neither. This resource is the
/// one that resolves a label <em>before</em> a version, and an unknown label falls through to the
/// version and then to <c>latest</c> instead of failing — the opposite of a skill and the opposite
/// order to a prompt. The version in the answer is the one that was resolved, so a caller never has
/// to know where a label points.
/// </para>
/// <para>
/// Agent specifications are served over the AI module's HTTP endpoint only, so reading one does not
/// open the AI connection.
/// </para>
/// </remarks>
public interface INacosAgentSpecRegistry
{
    /// <summary>
    /// Reads one version of an agent specification.
    /// </summary>
    /// <param name="agentSpecName">The specification name.</param>
    /// <param name="version">The version to read, or <see langword="null"/> to resolve one.</param>
    /// <param name="label">
    /// The label to resolve. It wins when it resolves, even alongside a named version; an unknown
    /// label falls through to the version and then to <c>latest</c>.
    /// </param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The resolved specification, with the version and hash the server reported.</returns>
    /// <exception cref="ArgumentException"><paramref name="agentSpecName"/> is null or whitespace.</exception>
    /// <exception cref="NacosAuthException">The server rejected the request's credentials.</exception>
    /// <exception cref="NacosServerException">
    /// The specification is unknown or disabled, the version is not online, the reply carried no
    /// specification, or the server does not serve the registry (a server older than Nacos 3.2).
    /// </exception>
    Task<NacosAgentSpec> GetAgentSpecAsync(
        string agentSpecName,
        string? version = null,
        string? label = null,
        CancellationToken ct = default);

    /// <summary>
    /// Subscribes to one version of an agent specification.
    /// </summary>
    /// <param name="agentSpecName">The specification name.</param>
    /// <param name="version">The version to watch, or <see langword="null"/> to resolve one.</param>
    /// <param name="label">The label to resolve, or <see langword="null"/> for none.</param>
    /// <param name="ct">A token that ends the subscription. Leaving the enumerable ends it too.</param>
    /// <returns>
    /// The current specification as the first element — or <see langword="null"/> when it is not
    /// published — then the specification again on every change, with <see langword="null"/> reporting
    /// that it stopped being served.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="agentSpecName"/> is null or whitespace.</exception>
    /// <exception cref="NacosServerException">
    /// The read that seeds the subscription failed for a reason other than the specification not
    /// existing.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Nacos does not push agent-specification changes, so a subscription is served by re-reading the
    /// specification on the interval configured by <see cref="NacosAiOptions.CacheRefreshInterval"/>
    /// and yielding the result when it differs from the copy that was cached. The read is made in full
    /// rather than conditionally on the cached hash, because a conditional read of this resource
    /// cannot report a withdrawal: the server answers "not modified" for a version it has taken
    /// offline, as long as its content has not changed. A read that fails does not end the
    /// subscription: the last known copy stays cached and the read is retried.
    /// </para>
    /// <para>
    /// A specification the server stops serving is reported as a change whose element is
    /// <see langword="null"/>, so a subscriber does not keep loading content that is gone.
    /// </para>
    /// </remarks>
    IAsyncEnumerable<NacosAgentSpec?> SubscribeAgentSpecAsync(
        string agentSpecName,
        string? version = null,
        string? label = null,
        CancellationToken ct = default);
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
