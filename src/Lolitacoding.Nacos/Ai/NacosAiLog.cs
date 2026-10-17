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
}
