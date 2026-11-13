using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.AI;

/// <summary>
/// Source-generated log messages for the AI registry client.
/// </summary>
internal static partial class NacosAILog
{
    /// <summary>Logs that an MCP server version was released.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="name">The MCP server name.</param>
    /// <param name="version">The version that was released.</param>
    /// <param name="id">The internal identifier the server assigned.</param>
    /// <remarks>
    /// A release is the only operation in the MCP registry that changes what clients are served, so
    /// it is the one worth recording at the default level. Reading a server is left to the caller.
    /// </remarks>
    [LoggerMessage(
        EventId = 8001,
        Level = LogLevel.Information,
        Message = "Released MCP server {Name} version {Version} as {Id}.")]
    public static partial void McpServerReleased(ILogger logger, string name, string version, string id);

    /// <summary>Logs that a successful query carried no server detail.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="name">The MCP server name that was queried.</param>
    /// <param name="version">The version that was queried.</param>
    /// <remarks>
    /// Nacos reports a missing server or version as a failure result code, so a success with no
    /// detail means the reply did not match the contract rather than that the server is absent.
    /// </remarks>
    [LoggerMessage(
        EventId = 8002,
        Level = LogLevel.Warning,
        Message = "The query for MCP server {Name} version {Version} succeeded but carried no server detail.")]
    public static partial void McpServerDetailMissing(ILogger logger, string name, string? version);

    /// <summary>Logs that an MCP server endpoint was registered.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="name">The MCP server name.</param>
    /// <param name="address">The endpoint address.</param>
    /// <param name="port">The endpoint port.</param>
    /// <remarks>
    /// Registering an endpoint is what makes a published server reachable, so it is recorded at the
    /// default level alongside the release.
    /// </remarks>
    [LoggerMessage(
        EventId = 8003,
        Level = LogLevel.Information,
        Message = "Registered MCP server {Name} endpoint {Address}:{Port}.")]
    public static partial void McpEndpointRegistered(ILogger logger, string name, string address, int port);

    /// <summary>Logs that an MCP server endpoint was removed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="name">The MCP server name.</param>
    /// <param name="address">The endpoint address.</param>
    /// <param name="port">The endpoint port.</param>
    [LoggerMessage(
        EventId = 8004,
        Level = LogLevel.Information,
        Message = "Deregistered MCP server {Name} endpoint {Address}:{Port}.")]
    public static partial void McpEndpointDeregistered(ILogger logger, string name, string address, int port);

    /// <summary>Logs that an endpoint could not be replayed after a reconnect.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="name">The MCP server name.</param>
    /// <param name="reason">The error the server or transport reported.</param>
    /// <remarks>
    /// The endpoint stays recorded, so the next reconnect tries again rather than dropping it.
    /// </remarks>
    [LoggerMessage(
        EventId = 8005,
        Level = LogLevel.Warning,
        Message = "Failed to replay MCP server {Name} endpoint after a reconnect: {Reason}")]
    public static partial void McpEndpointRestoreFailed(ILogger logger, string name, string reason);

    /// <summary>Logs how many MCP server endpoints were replayed after a reconnect.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="count">The number of endpoints the server accepted again.</param>
    [LoggerMessage(
        EventId = 8006,
        Level = LogLevel.Information,
        Message = "Replayed {Count} MCP server endpoint(s) after a reconnect.")]
    public static partial void McpEndpointsRestored(ILogger logger, int count);

    /// <summary>Logs that a subscribed MCP server changed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="name">The MCP server name.</param>
    /// <param name="version">The version whose detail changed.</param>
    /// <remarks>
    /// Debug level: the change is delivered to the subscriber, so this only helps when working out
    /// why a subscription did or did not fire.
    /// </remarks>
    [LoggerMessage(
        EventId = 8007,
        Level = LogLevel.Debug,
        Message = "MCP server {Name} version {Version} changed.")]
    public static partial void McpServerChanged(ILogger logger, string name, string version);

    /// <summary>Logs that a subscription refresh failed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="name">The MCP server name.</param>
    /// <param name="version">The version being refreshed.</param>
    /// <param name="reason">The error that was reported.</param>
    /// <remarks>
    /// A refresh that fails leaves the cached detail in place and is retried on the next interval,
    /// because a subscription is meant to survive an outage rather than fail with it.
    /// </remarks>
    [LoggerMessage(
        EventId = 8008,
        Level = LogLevel.Warning,
        Message = "Refreshing the subscription for MCP server {Name} version {Version} failed: {Reason}")]
    public static partial void McpServerRefreshFailed(ILogger logger, string name, string? version, string reason);

    /// <summary>Logs that a subscribed prompt changed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="promptKey">The prompt key.</param>
    /// <param name="version">The resolved version whose content changed.</param>
    /// <remarks>
    /// Debug level: the change is delivered to the subscriber, so this only helps when working out
    /// why a subscription did or did not fire.
    /// </remarks>
    [LoggerMessage(
        EventId = 8009,
        Level = LogLevel.Debug,
        Message = "Prompt {PromptKey} version {Version} changed.")]
    public static partial void PromptChanged(ILogger logger, string promptKey, string version);

    /// <summary>Logs that a subscribed prompt stopped being served.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="promptKey">The prompt key.</param>
    /// <remarks>
    /// A withdrawal is a change like any other and is delivered as one, so it is recorded at the same
    /// level. Unlike an MCP server, a prompt the server no longer serves is distinguishable from one
    /// that was never published, so it is reported rather than waited out.
    /// </remarks>
    [LoggerMessage(
        EventId = 8010,
        Level = LogLevel.Debug,
        Message = "Prompt {PromptKey} is no longer served.")]
    public static partial void PromptWithdrawn(ILogger logger, string promptKey);

    /// <summary>Logs that a successful prompt query carried no prompt.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="promptKey">The prompt key that was queried.</param>
    /// <param name="version">The version that was named, if any.</param>
    /// <param name="label">The label that was named, if any.</param>
    /// <remarks>
    /// A missing prompt or version is reported as a failure result code and an unchanged hash as the
    /// "not modified" one, so a success with no prompt means the reply did not match the contract
    /// rather than that the prompt is absent.
    /// </remarks>
    [LoggerMessage(
        EventId = 8011,
        Level = LogLevel.Warning,
        Message = "The query for prompt {PromptKey} version {Version} label {Label} succeeded but carried no prompt.")]
    public static partial void PromptDetailMissing(ILogger logger, string promptKey, string? version, string? label);

    /// <summary>Logs that a prompt subscription refresh failed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="promptKey">The prompt key being refreshed.</param>
    /// <param name="version">The version being refreshed, if one was named.</param>
    /// <param name="reason">The error that was reported.</param>
    /// <remarks>
    /// A refresh that fails leaves the cached prompt in place and is retried on the next interval,
    /// because a subscription is meant to survive an outage rather than fail with it.
    /// </remarks>
    [LoggerMessage(
        EventId = 8012,
        Level = LogLevel.Warning,
        Message = "Refreshing the subscription for prompt {PromptKey} version {Version} failed: {Reason}")]
    public static partial void PromptRefreshFailed(ILogger logger, string promptKey, string? version, string reason);

    /// <summary>Logs that a subscribed skill changed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="skillName">The skill name.</param>
    /// <param name="version">The version the server resolved, when it stated one.</param>
    /// <remarks>
    /// Debug level: the change is delivered to the subscriber, so this only helps when working out
    /// why a subscription did or did not fire.
    /// </remarks>
    [LoggerMessage(
        EventId = 8013,
        Level = LogLevel.Debug,
        Message = "Skill {SkillName} version {Version} changed.")]
    public static partial void SkillChanged(ILogger logger, string skillName, string? version);

    /// <summary>Logs that a subscribed skill stopped being served.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="skillName">The skill name.</param>
    /// <remarks>
    /// A withdrawal is a change like any other and is delivered as one, so it is recorded at the same
    /// level. A skill the server no longer serves is distinguishable from one that was never
    /// published, so it is reported rather than waited out.
    /// </remarks>
    [LoggerMessage(
        EventId = 8014,
        Level = LogLevel.Debug,
        Message = "Skill {SkillName} is no longer served.")]
    public static partial void SkillWithdrawn(ILogger logger, string skillName);

    /// <summary>Logs that a skill subscription refresh failed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="skillName">The skill being refreshed.</param>
    /// <param name="version">The version being refreshed, if one was named.</param>
    /// <param name="label">The label being refreshed, if one was named.</param>
    /// <param name="reason">The error that was reported.</param>
    /// <remarks>
    /// A refresh that fails leaves the cached archive in place and is retried on the next interval,
    /// because a subscription is meant to survive an outage rather than fail with it.
    /// </remarks>
    [LoggerMessage(
        EventId = 8015,
        Level = LogLevel.Warning,
        Message = "Refreshing the subscription for skill {SkillName} version {Version} label {Label} failed: {Reason}")]
    public static partial void SkillRefreshFailed(
        ILogger logger,
        string skillName,
        string? version,
        string? label,
        string reason);

    /// <summary>Logs that a subscribed agent specification changed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="agentSpecName">The specification name.</param>
    /// <param name="version">The version the server resolved, when it stated one.</param>
    /// <remarks>
    /// Debug level: the change is delivered to the subscriber, so this only helps when working out
    /// why a subscription did or did not fire.
    /// </remarks>
    [LoggerMessage(
        EventId = 8016,
        Level = LogLevel.Debug,
        Message = "AgentSpec {AgentSpecName} version {Version} changed.")]
    public static partial void AgentSpecChanged(ILogger logger, string agentSpecName, string? version);

    /// <summary>Logs that a subscribed agent specification stopped being served.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="agentSpecName">The specification name.</param>
    /// <remarks>
    /// A withdrawal is a change like any other and is delivered as one, so it is recorded at the same
    /// level. A specification the server no longer serves is distinguishable from one that was never
    /// published, so it is reported rather than waited out.
    /// </remarks>
    [LoggerMessage(
        EventId = 8017,
        Level = LogLevel.Debug,
        Message = "AgentSpec {AgentSpecName} is no longer served.")]
    public static partial void AgentSpecWithdrawn(ILogger logger, string agentSpecName);

    /// <summary>Logs that an agent-specification answer carried no specification.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="agentSpecName">The specification that was queried.</param>
    /// <param name="version">The version that was named, if any.</param>
    /// <param name="label">The label that was named, if any.</param>
    /// <param name="code">The result code the envelope carried, or zero when there was no envelope.</param>
    /// <remarks>
    /// A missing specification is reported as a not-found status and an unchanged hash as the
    /// not-modified one, so a successful status whose envelope carries no specification means the
    /// reply did not match the contract rather than that the specification is absent.
    /// </remarks>
    [LoggerMessage(
        EventId = 8018,
        Level = LogLevel.Warning,
        Message = "The query for agent spec {AgentSpecName} version {Version} label {Label} carried no specification (code {Code}).")]
    public static partial void AgentSpecDetailMissing(
        ILogger logger,
        string agentSpecName,
        string? version,
        string? label,
        int code);

    /// <summary>Logs that an agent-specification subscription refresh failed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="agentSpecName">The specification being refreshed.</param>
    /// <param name="version">The version being refreshed, if one was named.</param>
    /// <param name="label">The label being refreshed, if one was named.</param>
    /// <param name="reason">The error that was reported.</param>
    /// <remarks>
    /// A refresh that fails leaves the cached copy in place and is retried on the next interval,
    /// because a subscription is meant to survive an outage rather than fail with it.
    /// </remarks>
    [LoggerMessage(
        EventId = 8019,
        Level = LogLevel.Warning,
        Message = "Refreshing the subscription for agent spec {AgentSpecName} version {Version} label {Label} failed: {Reason}")]
    public static partial void AgentSpecRefreshFailed(
        ILogger logger,
        string agentSpecName,
        string? version,
        string? label,
        string reason);

    /// <summary>Logs that an A2A agent card was published.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="agentName">The agent name.</param>
    /// <param name="version">The card version that was published.</param>
    /// <param name="registrationType">How the card was registered.</param>
    /// <remarks>
    /// Publishing changes what other agents resolve, so it is recorded at the default level; reading
    /// a card is left to the caller.
    /// </remarks>
    [LoggerMessage(
        EventId = 8020,
        Level = LogLevel.Information,
        Message = "Published agent card {AgentName} version {Version} as {RegistrationType}.")]
    public static partial void AgentCardReleased(
        ILogger logger,
        string agentName,
        string version,
        string registrationType);

    /// <summary>Logs that a successful card query carried no card.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="agentName">The agent name that was queried.</param>
    /// <param name="version">The version that was queried, if one was named.</param>
    /// <param name="registrationType">The registration type that was named, if any.</param>
    /// <remarks>
    /// A missing agent or version is reported as a failure result code, so a success with no card
    /// means the reply did not match the contract rather than that the agent is absent.
    /// </remarks>
    [LoggerMessage(
        EventId = 8021,
        Level = LogLevel.Warning,
        Message = "The query for agent card {AgentName} version {Version} registration type {RegistrationType} carried no card.")]
    public static partial void AgentCardDetailMissing(
        ILogger logger,
        string agentName,
        string? version,
        string? registrationType);

    /// <summary>Logs that a subscribed agent card changed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="agentName">The agent name.</param>
    /// <param name="version">The card version that is now served.</param>
    [LoggerMessage(
        EventId = 8022,
        Level = LogLevel.Debug,
        Message = "Agent card {AgentName} changed to version {Version}.")]
    public static partial void AgentCardChanged(ILogger logger, string agentName, string? version);

    /// <summary>Logs that a subscribed agent card stopped being served.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="agentName">The agent name.</param>
    [LoggerMessage(
        EventId = 8023,
        Level = LogLevel.Debug,
        Message = "Agent card {AgentName} is no longer served.")]
    public static partial void AgentCardWithdrawn(ILogger logger, string agentName);

    /// <summary>Logs that an agent card subscription refresh failed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="agentName">The agent being refreshed.</param>
    /// <param name="version">The version being refreshed, if one was named.</param>
    /// <param name="registrationType">The registration type being refreshed, if any.</param>
    /// <param name="reason">The error that was reported.</param>
    /// <remarks>
    /// A refresh that fails leaves the cached copy in place and is retried on the next interval,
    /// because a subscription is meant to survive an outage rather than fail with it.
    /// </remarks>
    [LoggerMessage(
        EventId = 8024,
        Level = LogLevel.Warning,
        Message = "Refreshing the subscription for agent card {AgentName} version {Version} registration type {RegistrationType} failed: {Reason}")]
    public static partial void AgentCardRefreshFailed(
        ILogger logger,
        string agentName,
        string? version,
        string? registrationType,
        string reason);

    /// <summary>Logs that an agent endpoint was registered.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="agentName">The agent name.</param>
    /// <param name="address">The endpoint address.</param>
    /// <param name="port">The endpoint port.</param>
    [LoggerMessage(
        EventId = 8025,
        Level = LogLevel.Information,
        Message = "Registered agent endpoint {Address}:{Port} for {AgentName}.")]
    public static partial void AgentEndpointRegistered(
        ILogger logger,
        string agentName,
        string address,
        int port);

    /// <summary>Logs that an agent endpoint was removed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="agentName">The agent name.</param>
    /// <param name="address">The endpoint address.</param>
    /// <param name="port">The endpoint port.</param>
    [LoggerMessage(
        EventId = 8026,
        Level = LogLevel.Information,
        Message = "Removed agent endpoint {Address}:{Port} of {AgentName}.")]
    public static partial void AgentEndpointDeregistered(
        ILogger logger,
        string agentName,
        string address,
        int port);

    /// <summary>Logs that the agent endpoints were replayed after a reconnect.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="count">How many endpoints were republished.</param>
    [LoggerMessage(
        EventId = 8027,
        Level = LogLevel.Information,
        Message = "Republished {Count} agent endpoint(s) after reconnecting.")]
    public static partial void AgentEndpointsRestored(ILogger logger, int count);

    /// <summary>Logs that one agent endpoint could not be replayed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="agentName">The agent the endpoint belongs to.</param>
    /// <param name="reason">The error that was reported.</param>
    /// <remarks>
    /// The endpoint stays recorded, so the next reconnect tries it again rather than losing it.
    /// </remarks>
    [LoggerMessage(
        EventId = 8028,
        Level = LogLevel.Warning,
        Message = "Republishing an endpoint of agent {AgentName} failed: {Reason}")]
    public static partial void AgentEndpointRestoreFailed(
        ILogger logger,
        string agentName,
        string reason);

    /// <summary>Logs that a RAD agent version was published.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="agentName">The agent name.</param>
    /// <param name="version">The version the registry created.</param>
    /// <param name="status">The status the registry assigned it.</param>
    /// <remarks>
    /// A publish changes what other callers can discover, so it is recorded at the default level.
    /// </remarks>
    [LoggerMessage(
        EventId = 8029,
        Level = LogLevel.Information,
        Message = "Published agent {AgentName} version {Version} as {Status}.")]
    public static partial void AgentPublished(
        ILogger logger,
        string agentName,
        string? version,
        string? status);

    /// <summary>Logs that a publish succeeded but carried no version detail.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="agentName">The agent name.</param>
    /// <param name="version">The version that was published.</param>
    [LoggerMessage(
        EventId = 8030,
        Level = LogLevel.Warning,
        Message = "The publish of agent {AgentName} version {Version} succeeded but carried no version detail.")]
    public static partial void AgentPublishDetailMissing(
        ILogger logger,
        string agentName,
        string? version);

    /// <summary>Logs that a discovery succeeded but carried no result.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="agentName">The agent name.</param>
    /// <param name="version">The version that was named, if any.</param>
    /// <param name="label">The label that was named, if any.</param>
    [LoggerMessage(
        EventId = 8031,
        Level = LogLevel.Warning,
        Message = "The discovery of agent {AgentName} version {Version} label {Label} carried no result.")]
    public static partial void AgentDiscoveryDetailMissing(
        ILogger logger,
        string agentName,
        string? version,
        string? label);

    /// <summary>Logs that a batch of agent endpoints was registered.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="agentName">The agent name.</param>
    /// <param name="protocol">The protocol the endpoints serve.</param>
    /// <param name="count">How many endpoints the batch carried.</param>
    [LoggerMessage(
        EventId = 8032,
        Level = LogLevel.Information,
        Message = "Registered {Count} endpoint(s) for agent {AgentName} over {Protocol}.")]
    public static partial void AgentEndpointsRegistered(
        ILogger logger,
        string agentName,
        string protocol,
        int count);

    /// <summary>Logs that a batch of agent endpoints was withdrawn.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="agentName">The agent name.</param>
    /// <param name="protocol">The protocol whose endpoints were removed.</param>
    [LoggerMessage(
        EventId = 8033,
        Level = LogLevel.Information,
        Message = "Removed the registered endpoints of agent {AgentName} over {Protocol}.")]
    public static partial void AgentEndpointsDeregistered(
        ILogger logger,
        string agentName,
        string protocol);

    /// <summary>Logs that a RAD watch was installed on the connection.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="agentName">The agent name.</param>
    /// <param name="version">The version that was named, if any.</param>
    /// <param name="label">The label that was named, if any.</param>
    [LoggerMessage(
        EventId = 8034,
        Level = LogLevel.Debug,
        Message = "Watching agent {AgentName} version {Version} label {Label}.")]
    public static partial void AgentWatchStarted(
        ILogger logger,
        string agentName,
        string? version,
        string? label);

    /// <summary>Logs that a RAD watch was removed from the connection.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="agentName">The agent name.</param>
    [LoggerMessage(
        EventId = 8035,
        Level = LogLevel.Debug,
        Message = "Stopped watching agent {AgentName}.")]
    public static partial void AgentWatchStopped(ILogger logger, string agentName);

    /// <summary>Logs that reading the state of a watched agent failed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="agentName">The agent name.</param>
    /// <param name="reason">The error that was reported.</param>
    /// <remarks>
    /// A failed read does not end the watch: the last state stays with the caller and the read is
    /// retried, because the server will push again when the state changes.
    /// </remarks>
    [LoggerMessage(
        EventId = 8036,
        Level = LogLevel.Warning,
        Message = "Reading the watched state of agent {AgentName} failed: {Reason}")]
    public static partial void AgentWatchRefreshFailed(
        ILogger logger,
        string agentName,
        string reason);

    /// <summary>Logs that a server ended a RAD watch.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="agentName">The agent name.</param>
    /// <param name="errorCode">The error code the server reported, if any.</param>
    [LoggerMessage(
        EventId = 8037,
        Level = LogLevel.Warning,
        Message = "The Nacos server ended the watch of agent {AgentName} with error code {ErrorCode}.")]
    public static partial void AgentWatchTerminated(
        ILogger logger,
        string agentName,
        int? errorCode);

    /// <summary>Logs that a watch hint named a watch this client does not own.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="watchKey">The watch key the hint carried.</param>
    /// <remarks>
    /// The hint is declined, which is how the server learns to stop sending it. A hint for a watch
    /// that was just closed races the removal over the wire, so this is expected at times.
    /// </remarks>
    [LoggerMessage(
        EventId = 8038,
        Level = LogLevel.Debug,
        Message = "Declined a RAD watch hint for the unknown watch {WatchKey}.")]
    public static partial void AgentWatchHintUnmatched(ILogger logger, string watchKey);

    /// <summary>Logs that a RAD watch could not be installed again after a reconnect.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="agentName">The agent name.</param>
    /// <param name="reason">The error that was reported.</param>
    [LoggerMessage(
        EventId = 8039,
        Level = LogLevel.Warning,
        Message = "Reinstalling the watch of agent {AgentName} after a reconnect failed: {Reason}")]
    public static partial void AgentWatchRestoreFailed(
        ILogger logger,
        string agentName,
        string reason);

    /// <summary>Logs that a RAD watch could not be removed from the connection.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="agentName">The agent name.</param>
    /// <param name="reason">The error that was reported.</param>
    /// <remarks>
    /// The removal is best effort: the watch dies with the connection whether or not the server was
    /// told, so a removal that could not be sent is recorded rather than retried.
    /// </remarks>
    [LoggerMessage(
        EventId = 8040,
        Level = LogLevel.Debug,
        Message = "Removing the watch of agent {AgentName} failed: {Reason}")]
    public static partial void AgentWatchUnsubscribeFailed(
        ILogger logger,
        string agentName,
        string reason);

    /// <summary>Logs that a resource was answered from the local failover cache.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="resource">The resource kind.</param>
    /// <param name="name">The resource name.</param>
    /// <param name="reason">Why the server could not be read.</param>
    /// <remarks>
    /// The subscription is handed the copy that was last read from the server, so it starts with
    /// what it had rather than with nothing while the control plane is unreachable.
    /// </remarks>
    [LoggerMessage(
        EventId = 8041,
        Level = LogLevel.Information,
        Message = "Serving {Resource} {Name} from the local cache: {Reason}")]
    public static partial void SnapshotRestored(
        ILogger logger,
        string resource,
        string name,
        string reason);

    /// <summary>Logs that a resource could not be written to the local failover cache.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="resource">The resource kind.</param>
    /// <param name="name">The resource name.</param>
    /// <param name="reason">The error that was reported.</param>
    /// <remarks>
    /// The read itself succeeded, so a failed write is reported rather than raised: the cache is an
    /// optimisation and losing it must not fail a caller.
    /// </remarks>
    [LoggerMessage(
        EventId = 8042,
        Level = LogLevel.Warning,
        Message = "Caching {Resource} {Name} on disk failed: {Reason}")]
    public static partial void SnapshotWriteFailed(
        ILogger logger,
        string resource,
        string name,
        string reason);

    /// <summary>Logs which transport a watch was installed on.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="agentName">The agent name.</param>
    /// <param name="transport">The selected transport.</param>
    [LoggerMessage(
        EventId = 8043,
        Level = LogLevel.Debug,
        Message = "The watch of agent {AgentName} is served over {Transport}.")]
    public static partial void AgentWatchTransportSelected(
        ILogger logger,
        string agentName,
        string transport);

    /// <summary>Logs that a watch joined the HTTP batch long poll.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="clientWatchId">The watch identifier.</param>
    [LoggerMessage(
        EventId = 8044,
        Level = LogLevel.Debug,
        Message = "Registered the HTTP watch {ClientWatchId}.")]
    public static partial void AgentWatchHttpRegistered(
        ILogger logger,
        string clientWatchId);

    /// <summary>Logs that a watch left the HTTP batch long poll.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="clientWatchId">The watch identifier.</param>
    [LoggerMessage(
        EventId = 8045,
        Level = LogLevel.Debug,
        Message = "Removed the HTTP watch {ClientWatchId}.")]
    public static partial void AgentWatchHttpStopped(
        ILogger logger,
        string clientWatchId);

    /// <summary>Logs that an HTTP long poll reported changed watches.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="count">How many watches changed.</param>
    [LoggerMessage(
        EventId = 8046,
        Level = LogLevel.Debug,
        Message = "The HTTP agent watch reported {Count} changed watches.")]
    public static partial void AgentWatchHttpInvalidated(
        ILogger logger,
        int count);

    /// <summary>Logs that an HTTP long poll failed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="errorCode">The error code reported.</param>
    /// <param name="failures">How many failures happened in a row.</param>
    /// <param name="fallback">Whether the binding was abandoned.</param>
    [LoggerMessage(
        EventId = 8047,
        Level = LogLevel.Warning,
        Message = "The HTTP agent watch failed with error code {ErrorCode} after {Failures} consecutive failures; fallback={Fallback}.")]
    public static partial void AgentWatchHttpFailed(
        ILogger logger,
        int errorCode,
        int failures,
        bool fallback);

    /// <summary>Logs that a failed HTTP long poll will be tried again.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="errorCode">The error code reported.</param>
    /// <param name="failures">How many failures happened in a row.</param>
    [LoggerMessage(
        EventId = 8048,
        Level = LogLevel.Debug,
        Message = "The HTTP agent watch will retry after error code {ErrorCode} ({Failures} consecutive failures).")]
    public static partial void AgentWatchHttpRetryScheduled(
        ILogger logger,
        int errorCode,
        int failures);

    /// <summary>Logs that a watch is served by a fallback transport.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="agentName">The agent name.</param>
    /// <param name="transport">The transport the watch fell back to.</param>
    /// <param name="reason">Why the preferred transport could not serve the watch.</param>
    [LoggerMessage(
        EventId = 8049,
        Level = LogLevel.Warning,
        Message = "The watch of agent {AgentName} fell back to {Transport}: {Reason}")]
    public static partial void AgentWatchTransportFallback(
        ILogger logger,
        string agentName,
        string transport,
        string reason);

    /// <summary>Logs that a resource could not be read from the local failover cache.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="resource">The resource kind.</param>
    /// <param name="name">The selector the resource was cached for.</param>
    /// <param name="reason">The error that was reported.</param>
    /// <remarks>
    /// Warning, not Debug: the read only happens while the control plane is unreachable, so the
    /// snapshot was the last copy and its absence is the difference between starting and failing.
    /// </remarks>
    [LoggerMessage(
        EventId = 8050,
        Level = LogLevel.Warning,
        Message = "Reading the cached {Resource} {Name} from disk failed: {Reason}")]
    public static partial void SnapshotReadFailed(
        ILogger logger,
        string resource,
        string name,
        string reason);

    /// <summary>Logs that a batch of agent endpoints replaced the set published for a version.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="agentName">The agent name.</param>
    /// <param name="version">The card version the endpoints serve.</param>
    /// <param name="count">How many endpoints the batch carried.</param>
    /// <remarks>
    /// A batch is one request that replaces the whole set, so the count is what tells a reader how
    /// many transports the agent is now reachable on.
    /// </remarks>
    [LoggerMessage(
        EventId = 8051,
        Level = LogLevel.Information,
        Message = "Published {Count} endpoint(s) for agent {AgentName} version {Version}.")]
    public static partial void AgentEndpointBatchRegistered(
        ILogger logger,
        string agentName,
        string? version,
        int count);
}
