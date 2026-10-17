using System.Text.Json.Serialization.Metadata;
using Lolitacoding.Nacos.Protocol.Messages;

namespace Lolitacoding.Nacos.Protocol;

/// <summary>
/// Maps a Nacos payload type name to its JSON contract.
/// </summary>
/// <remarks>
/// <para>
/// Nacos routes payloads by <c>metadata.type</c>, which the server sets to the simple Java class
/// name of the message. There is no annotation and no enum involved; the simple name <em>is</em>
/// the routing key. This registry is the C# equivalent of the server's payload registry.
/// </para>
/// <para>
/// Entries are explicit rather than reflection-derived so the mapping survives trimming and
/// Native AOT compilation, where the type names of unreferenced members would otherwise be
/// removed.
/// </para>
/// </remarks>
internal static class PayloadTypeRegistry
{
    private static readonly Dictionary<string, JsonTypeInfo> Types = new(StringComparer.Ordinal)
    {
        // Configuration centre.
        ["ConfigQueryRequest"] = NacosJsonContext.Default.ConfigQueryRequest,
        ["ConfigQueryResponse"] = NacosJsonContext.Default.ConfigQueryResponse,
        ["ConfigPublishRequest"] = NacosJsonContext.Default.ConfigPublishRequest,
        ["ConfigPublishResponse"] = NacosJsonContext.Default.ConfigPublishResponse,
        ["ConfigRemoveRequest"] = NacosJsonContext.Default.ConfigRemoveRequest,
        ["ConfigRemoveResponse"] = NacosJsonContext.Default.ConfigRemoveResponse,
        ["ConfigBatchListenRequest"] = NacosJsonContext.Default.ConfigBatchListenRequest,
        ["ConfigChangeBatchListenResponse"] = NacosJsonContext.Default.ConfigChangeBatchListenResponse,
        ["ConfigChangeNotifyRequest"] = NacosJsonContext.Default.ConfigChangeNotifyRequest,
        ["ConfigChangeNotifyResponse"] = NacosJsonContext.Default.ConfigChangeNotifyResponse,

        // Naming (service discovery).
        ["InstanceRequest"] = NacosJsonContext.Default.InstanceRequest,
        ["PersistentInstanceRequest"] = NacosJsonContext.Default.PersistentInstanceRequest,
        ["InstanceResponse"] = NacosJsonContext.Default.InstanceResponse,
        ["BatchInstanceRequest"] = NacosJsonContext.Default.BatchInstanceRequest,
        ["BatchInstanceResponse"] = NacosJsonContext.Default.BatchInstanceResponse,
        ["ServiceQueryRequest"] = NacosJsonContext.Default.ServiceQueryRequest,
        ["QueryServiceResponse"] = NacosJsonContext.Default.QueryServiceResponse,
        ["ServiceListRequest"] = NacosJsonContext.Default.ServiceListRequest,
        ["ServiceListResponse"] = NacosJsonContext.Default.ServiceListResponse,
        ["SubscribeServiceRequest"] = NacosJsonContext.Default.SubscribeServiceRequest,
        ["SubscribeServiceResponse"] = NacosJsonContext.Default.SubscribeServiceResponse,
        ["NotifySubscriberRequest"] = NacosJsonContext.Default.NotifySubscriberRequest,
        ["NotifySubscriberResponse"] = NacosJsonContext.Default.NotifySubscriberResponse,

        // Distributed lock.
        ["LockOperationRequest"] = NacosJsonContext.Default.LockOperationRequest,
        ["LockOperationResponse"] = NacosJsonContext.Default.LockOperationResponse,
        ["LockNotificationRequest"] = NacosJsonContext.Default.LockNotificationRequest,
        ["LockNotificationResponse"] = NacosJsonContext.Default.LockNotificationResponse,

        // AI registry (MCP servers).
        ["QueryMcpServerRequest"] = NacosJsonContext.Default.QueryMcpServerRequest,
        ["QueryMcpServerResponse"] = NacosJsonContext.Default.QueryMcpServerResponse,
        ["McpServerEndpointRequest"] = NacosJsonContext.Default.McpServerEndpointRequest,
        ["McpServerEndpointResponse"] = NacosJsonContext.Default.McpServerEndpointResponse,
        ["ReleaseMcpServerRequest"] = NacosJsonContext.Default.ReleaseMcpServerRequest,
        ["ReleaseMcpServerResponse"] = NacosJsonContext.Default.ReleaseMcpServerResponse,

        // AI registry (prompts).
        ["QueryPromptRequest"] = NacosJsonContext.Default.QueryPromptRequest,
        ["QueryPromptResponse"] = NacosJsonContext.Default.QueryPromptResponse,

        // AI registry (A2A agent cards).
        ["QueryAgentCardRequest"] = NacosJsonContext.Default.QueryAgentCardRequest,
        ["QueryAgentCardResponse"] = NacosJsonContext.Default.QueryAgentCardResponse,
        ["ReleaseAgentCardRequest"] = NacosJsonContext.Default.ReleaseAgentCardRequest,
        ["ReleaseAgentCardResponse"] = NacosJsonContext.Default.ReleaseAgentCardResponse,
        ["AgentEndpointRequest"] = NacosJsonContext.Default.AgentEndpointRequest,
        ["AgentEndpointResponse"] = NacosJsonContext.Default.AgentEndpointResponse,

        // Transport and connection management.
        ["ServerCheckRequest"] = NacosJsonContext.Default.ServerCheckRequest,
        ["ServerCheckResponse"] = NacosJsonContext.Default.ServerCheckResponse,
        ["ConnectionSetupRequest"] = NacosJsonContext.Default.ConnectionSetupRequest,
        ["SetupAckRequest"] = NacosJsonContext.Default.SetupAckRequest,
        ["HealthCheckRequest"] = NacosJsonContext.Default.HealthCheckRequest,
        ["HealthCheckResponse"] = NacosJsonContext.Default.HealthCheckResponse,
        ["ClientDetectionRequest"] = NacosJsonContext.Default.ClientDetectionRequest,
        ["ClientDetectionResponse"] = NacosJsonContext.Default.ClientDetectionResponse,
        ["ConnectResetRequest"] = NacosJsonContext.Default.ConnectResetRequest,
        ["ConnectResetResponse"] = NacosJsonContext.Default.ConnectResetResponse,
        ["PushAckRequest"] = NacosJsonContext.Default.PushAckRequest,

        // Transport-level failures.
        ["ErrorResponse"] = NacosJsonContext.Default.ErrorResponse,
    };

    /// <summary>
    /// Resolves the JSON contract for a payload type name.
    /// </summary>
    /// <param name="typeName">The value of <c>metadata.type</c>.</param>
    /// <returns>The contract, or <see langword="null"/> when the type is unknown.</returns>
    public static JsonTypeInfo? Resolve(string typeName)
        => Types.TryGetValue(typeName, out JsonTypeInfo? typeInfo) ? typeInfo : null;

    /// <summary>
    /// Gets the wire type name Nacos expects for a message instance.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>The simple type name the server routes on.</returns>
    /// <remarks>
    /// The name is read from the CLR type rather than a lookup table keyed by instance, because
    /// the CLR type name already matches the Java simple name for every registered payload.
    /// </remarks>
    public static string GetTypeName(NacosRequest message) => message.GetType().Name;

    /// <summary>
    /// Gets the wire type name Nacos expects for a response instance.
    /// </summary>
    /// <param name="message">The response.</param>
    /// <returns>The simple type name the server routes on.</returns>
    public static string GetTypeName(NacosResponse message) => message.GetType().Name;
}
