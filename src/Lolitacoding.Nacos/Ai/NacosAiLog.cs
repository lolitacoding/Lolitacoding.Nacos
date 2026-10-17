using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Ai;

/// <summary>
/// Source-generated log messages for the AI registry client.
/// </summary>
internal static partial class NacosAiLog
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
}
