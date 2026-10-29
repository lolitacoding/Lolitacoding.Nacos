namespace Lolitacoding.Nacos.AI;

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
public interface INacosAIClient : IAsyncDisposable
{
    /// <summary>Gets the MCP server registry.</summary>
    INacosMcpRegistry Mcp { get; }

    /// <summary>Gets the prompt registry.</summary>
    INacosPromptRegistry Prompt { get; }

    /// <summary>Gets the skill registry.</summary>
    INacosSkillRegistry Skill { get; }

    /// <summary>Gets the agent-specification registry.</summary>
    INacosAgentSpecRegistry AgentSpec { get; }

    /// <summary>Gets the A2A agent card registry.</summary>
    INacosAgentCardRegistry AgentCard { get; }

    /// <summary>Gets the RAD agent registry.</summary>
    INacosAgentRegistry Agent { get; }
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
    /// the interval configured by <see cref="NacosAIOptions.CacheRefreshInterval"/> and yielding the
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
    /// interval configured by <see cref="NacosAIOptions.CacheRefreshInterval"/> and yielding the
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
    /// specification on the interval configured by <see cref="NacosAIOptions.CacheRefreshInterval"/>
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
    /// interval configured by <see cref="NacosAIOptions.CacheRefreshInterval"/> and yielding the
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

/// <summary>
/// The A2A agent card registry: where an agent advertises itself and where its runtime endpoints are
/// registered.
/// </summary>
/// <remarks>
/// <para>
/// A card is the A2A protocol's own document — what the agent is, what it can do, and where to call
/// it. Nacos stores it under the agent's name and the card's own version, and serves it back
/// unchanged. Publishing the same agent with a new version adds a version rather than replacing what
/// other agents are already resolving.
/// </para>
/// <para>
/// A card registered as a <c>SERVICE</c> carries no address: the agent is reached through the
/// endpoints registered against it, which is how an agent that runs inside a Nacos-registered process
/// is callable at an address only known at runtime. A card registered as a <c>URL</c> carries the
/// address itself and needs no endpoint.
/// </para>
/// <para>
/// This is the contract Nacos has served since 3.1. Nacos 3.3 adds a second, richer agent contract —
/// the RAD registry — which is a separate surface; a card remains how an A2A agent advertises itself.
/// </para>
/// </remarks>
public interface INacosAgentCardRegistry
{
    /// <summary>
    /// Reads one version of an agent card.
    /// </summary>
    /// <param name="agentName">The agent name.</param>
    /// <param name="version">The card version to read, or <see langword="null"/> for the latest one.</param>
    /// <param name="registrationType">
    /// The registration type to read, <c>URL</c> or <c>SERVICE</c>, or <see langword="null"/> to let
    /// the server answer with the card as stored.
    /// </param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The resolved card.</returns>
    /// <exception cref="ArgumentException"><paramref name="agentName"/> is null or whitespace.</exception>
    /// <exception cref="NacosAuthException">The server rejected the request's credentials.</exception>
    /// <exception cref="NacosServerException">
    /// The agent or version does not exist, the reply carried no card, or the server does not
    /// implement the agent registry.
    /// </exception>
    Task<NacosAgentCardDetail> GetAgentCardAsync(
        string agentName,
        string? version = null,
        string? registrationType = null,
        CancellationToken ct = default);

    /// <summary>
    /// Publishes one version of an agent card.
    /// </summary>
    /// <param name="card">The card to publish. Its name and version are required.</param>
    /// <param name="registrationType">
    /// How the card is registered, <c>URL</c> or <c>SERVICE</c>. Unset registers it as a service,
    /// which is what an agent whose address is only known at runtime wants.
    /// </param>
    /// <param name="setAsLatest">
    /// Whether this version becomes the one a read without a version resolves to. Unset publishes the
    /// card without moving that pointer, which is how a version is staged without redirecting
    /// callers to it.
    /// </param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server accepted the card.</returns>
    /// <exception cref="ArgumentException">The card, its name, or its version is missing.</exception>
    /// <exception cref="NacosServerException">The server refused the release.</exception>
    Task ReleaseAgentCardAsync(
        NacosAgentCard card,
        string? registrationType = null,
        bool setAsLatest = false,
        CancellationToken ct = default);

    /// <summary>
    /// Subscribes to one version of an agent card.
    /// </summary>
    /// <param name="agentName">The agent name.</param>
    /// <param name="version">The card version to watch, or <see langword="null"/> for the latest one.</param>
    /// <param name="registrationType">The registration type to watch, or <see langword="null"/> for any.</param>
    /// <param name="ct">A token that ends the subscription. Leaving the enumerable ends it too.</param>
    /// <returns>
    /// The current card as the first element — or <see langword="null"/> when it is not published —
    /// then the card again on every change, with <see langword="null"/> reporting that it stopped
    /// being served.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="agentName"/> is null or whitespace.</exception>
    /// <exception cref="NacosServerException">
    /// The read that seeds the subscription failed for a reason other than the agent not existing.
    /// </exception>
    /// <remarks>
    /// Nacos pushes no card changes, so a subscription is served by re-reading the card on the
    /// interval configured by <see cref="NacosAIOptions.CacheRefreshInterval"/> and yielding it when
    /// it differs from the cached copy. A card carries no content hash, so the read is always
    /// unconditional. A read that fails does not end the subscription: the last known copy stays
    /// cached and the read is retried.
    /// </remarks>
    IAsyncEnumerable<NacosAgentCardDetail?> SubscribeAgentCardAsync(
        string agentName,
        string? version = null,
        string? registrationType = null,
        CancellationToken ct = default);

    /// <summary>
    /// Registers one runtime endpoint of an agent.
    /// </summary>
    /// <param name="agentName">The agent name.</param>
    /// <param name="endpoint">The endpoint to register.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server accepted the endpoint.</returns>
    /// <exception cref="ArgumentException"><paramref name="agentName"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is null.</exception>
    /// <exception cref="NacosServerException">The server refused the registration.</exception>
    /// <remarks>
    /// The registration is a Naming instance owned by the connection that made it, so it is removed
    /// when that connection closes and republished after a reconnect. A registration the server
    /// refused is not replayed: the caller saw the failure.
    /// </remarks>
    Task RegisterAgentEndpointAsync(
        string agentName,
        NacosAgentEndpoint endpoint,
        CancellationToken ct = default);

    /// <summary>
    /// Removes one runtime endpoint of an agent.
    /// </summary>
    /// <param name="agentName">The agent name.</param>
    /// <param name="endpoint">The endpoint to remove.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server acknowledged the removal.</returns>
    /// <exception cref="ArgumentException"><paramref name="agentName"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is null.</exception>
    /// <remarks>
    /// The endpoint is sent whole rather than by address alone, because the version and protocol it
    /// was registered for are what the server resolves the instance by. Removing an endpoint that is
    /// not there is not an error.
    /// </remarks>
    Task DeregisterAgentEndpointAsync(
        string agentName,
        NacosAgentEndpoint endpoint,
        CancellationToken ct = default);
}

/// <summary>
/// The RAD agent registry: where an agent is published as a versioned resource and discovered by the
/// callers that want to use it.
/// </summary>
/// <remarks>
/// <para>
/// This is the agent contract Nacos **3.3** adds, and it is a different surface from the A2A card
/// registry rather than a newer version of it. A card is a document an agent publishes about itself
/// and other agents read; here an agent is a resource with metadata, versions, call interfaces and
/// runtime endpoints, published and searched the way the other AI resources are.
/// </para>
/// <para>
/// Publishing is not idempotent in the way a release is: a publish creates the version it names, and a
/// version that already exists is refused rather than replaced. A version that is published without
/// being submitted stays editable and is not served, which is how a caller stages one.
/// </para>
/// <para>
/// Every operation needs the server to advertise the RAD ability. A server that implements only the
/// A2A card contract is refused with the not-implemented code rather than with an unexplained failure,
/// and a server that says nothing about either is reported as one the client cannot classify.
/// </para>
/// </remarks>
public interface INacosAgentRegistry
{
    /// <summary>
    /// Publishes one version of an agent.
    /// </summary>
    /// <param name="request">The version to publish. Its agent name and version are required.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The version detail the registry created.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="ArgumentException">The request names no agent or no version.</exception>
    /// <exception cref="NacosServerException">
    /// The registry refused the publish, the reply carried no version detail, or the server does not
    /// implement the RAD contract.
    /// </exception>
    Task<NacosAgentVersionDetail> PublishAgentAsync(
        NacosAgentPublishRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Searches the agents of the configured namespace.
    /// </summary>
    /// <param name="request">The query. Every filter is optional and they combine.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>One page of matching agents.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="NacosServerException">
    /// The server refused the search, or does not implement the RAD contract.
    /// </exception>
    Task<NacosAgentPage<NacosAgentSummary>> SearchAgentsAsync(
        NacosAgentSearchRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Discovers one version of an agent, narrowed to what the caller can use.
    /// </summary>
    /// <param name="reference">The agent and the version or label to resolve.</param>
    /// <param name="filter">What the caller can use, or <see langword="null"/> for everything.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>What was discovered, with the interfaces that survived the filter.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reference"/> is null.</exception>
    /// <exception cref="ArgumentException">The reference names no agent.</exception>
    /// <exception cref="NacosServerException">
    /// The agent or version does not exist, the reply carried no result, or the server does not
    /// implement the RAD contract.
    /// </exception>
    Task<NacosAgentDiscoveryResult> DiscoverAgentAsync(
        NacosAgentReference reference,
        NacosAgentDiscoveryFilter? filter = null,
        CancellationToken ct = default);

    /// <summary>
    /// Registers one batch of runtime endpoints for an agent.
    /// </summary>
    /// <param name="batch">The batch to register. It must name an agent, a protocol and at least one
    /// endpoint.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server accepted the batch.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="batch"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The batch names no agent, no protocol, or carries no endpoint.
    /// </exception>
    /// <exception cref="NacosServerException">The server refused the batch.</exception>
    /// <remarks>
    /// The batch replaces what the same publisher registered for the same agent and protocol before,
    /// which is what makes a redeploy safe: the new process supersedes the old set instead of
    /// accumulating beside it. That is also why an empty batch is refused — it would withdraw the
    /// endpoints rather than publish any.
    /// </remarks>
    Task RegisterEndpointsAsync(
        NacosAgentEndpointBatch batch,
        CancellationToken ct = default);

    /// <summary>
    /// Removes every endpoint a publisher registered for one agent and protocol.
    /// </summary>
    /// <param name="agentName">The agent name.</param>
    /// <param name="protocol">The protocol whose endpoints are removed.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server acknowledged the removal.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="agentName"/> or <paramref name="protocol"/> is null or whitespace.
    /// </exception>
    /// <exception cref="NacosServerException">The server refused the removal.</exception>
    /// <remarks>
    /// The removal is by protocol rather than by endpoint because the endpoints a process registered
    /// are only meaningful as a set.
    /// </remarks>
    Task DeregisterEndpointsAsync(
        string agentName,
        string protocol,
        CancellationToken ct = default);

    /// <summary>
    /// Follows one version of an agent, yielding the current state and then every change.
    /// </summary>
    /// <param name="reference">The agent and the version or label to follow.</param>
    /// <param name="filter">What the caller can use, or <see langword="null"/> for everything.</param>
    /// <param name="ct">A token that ends the watch. Leaving the enumerable ends it too.</param>
    /// <returns>
    /// The state the filter resolved as the first element — or <see langword="null"/> when the agent
    /// is not published — then the state again whenever the server reports the answer changed, with
    /// <see langword="null"/> reporting that it stopped being served.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="reference"/> is null.</exception>
    /// <exception cref="ArgumentException">The reference names no agent.</exception>
    /// <exception cref="NacosServerException">
    /// The server does not implement the RAD watch hint push, or stated nothing that says whether it
    /// does. The error is raised from the enumerable rather than from this method.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This is the push half of <see cref="DiscoverAgentAsync"/>: instead of the caller asking again
    /// on its own schedule, the client keeps one watch open on the connection and reads the state
    /// again when the server says the answer may have changed. Nothing about the state is carried by
    /// the hint — it says only that there is something to look at.
    /// </para>
    /// <para>
    /// The watch belongs to the connection. It is re-installed after a reconnect, and the state is
    /// read again rather than assumed, because the answer may have moved on while the client was
    /// disconnected. A watch also outlives the caller's own enumeration only as long as someone is
    /// reading it: callers that follow the same agent share one watch, and the last one to stop ends
    /// it.
    /// </para>
    /// <para>
    /// A read that fails does not end the watch — the last state stays with the caller and the read is
    /// tried again — but a server that cannot take the hint push is reported as such rather than
    /// watched silently.
    /// </para>
    /// </remarks>
    IAsyncEnumerable<NacosAgentDiscoveryResult?> WatchAgentAsync(
        NacosAgentReference reference,
        NacosAgentDiscoveryFilter? filter = null,
        CancellationToken ct = default);
}
