using System.Text.Json.Serialization;
using Lolitacoding.Nacos.Protocol.Messages;

namespace Lolitacoding.Nacos.Protocol;

/// <summary>
/// Source-generated JSON contracts for every Nacos protocol message.
/// </summary>
/// <remarks>
/// <para>
/// Nacos serialises message bodies with Jackson configured for
/// <c>Include.NON_NULL</c> and with unknown properties ignored. The settings below reproduce
/// that contract exactly: <see cref="JsonIgnoreCondition.WhenWritingNull"/> drops null fields
/// while still emitting empty collections, <c>false</c> and <c>0</c>, and unknown members are
/// skipped on read so a newer server can add fields without breaking this client.
/// </para>
/// <para>
/// The camel-case policy produces the exact wire names Nacos uses. The two documented naming
/// traps fall out correctly: <c>Md5</c> becomes <c>md5</c>, and <c>AllIPs</c> becomes
/// <c>allIPs</c> with the capitalised <c>IP</c> the Java side expects.
/// </para>
/// <para>
/// Source generation is used exclusively: there is no reflection-based serialization anywhere in
/// this library, which is what keeps it trim- and Native AOT-safe.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    WriteIndented = false)]
[JsonSerializable(typeof(ConfigQueryRequest))]
[JsonSerializable(typeof(ConfigQueryResponse))]
[JsonSerializable(typeof(ConfigPublishRequest))]
[JsonSerializable(typeof(ConfigPublishResponse))]
[JsonSerializable(typeof(ConfigRemoveRequest))]
[JsonSerializable(typeof(ConfigRemoveResponse))]
[JsonSerializable(typeof(ConfigBatchListenRequest))]
[JsonSerializable(typeof(ConfigChangeBatchListenResponse))]
[JsonSerializable(typeof(ConfigChangeNotifyRequest))]
[JsonSerializable(typeof(ConfigChangeNotifyResponse))]
[JsonSerializable(typeof(InstanceRequest))]
[JsonSerializable(typeof(PersistentInstanceRequest))]
[JsonSerializable(typeof(InstanceResponse))]
[JsonSerializable(typeof(BatchInstanceRequest))]
[JsonSerializable(typeof(BatchInstanceResponse))]
[JsonSerializable(typeof(ServiceQueryRequest))]
[JsonSerializable(typeof(QueryServiceResponse))]
[JsonSerializable(typeof(ServiceListRequest))]
[JsonSerializable(typeof(ServiceListResponse))]
[JsonSerializable(typeof(SubscribeServiceRequest))]
[JsonSerializable(typeof(SubscribeServiceResponse))]
[JsonSerializable(typeof(NotifySubscriberRequest))]
[JsonSerializable(typeof(NotifySubscriberResponse))]
[JsonSerializable(typeof(LockOperationRequest))]
[JsonSerializable(typeof(LockOperationResponse))]
[JsonSerializable(typeof(LockNotificationRequest))]
[JsonSerializable(typeof(LockNotificationResponse))]
[JsonSerializable(typeof(QueryMcpServerRequest))]
[JsonSerializable(typeof(QueryMcpServerResponse))]
[JsonSerializable(typeof(McpServerEndpointRequest))]
[JsonSerializable(typeof(McpServerEndpointResponse))]
[JsonSerializable(typeof(ReleaseMcpServerRequest))]
[JsonSerializable(typeof(ReleaseMcpServerResponse))]
[JsonSerializable(typeof(QueryPromptRequest))]
[JsonSerializable(typeof(QueryPromptResponse))]
[JsonSerializable(typeof(Ai.NacosAgentSpecResult))]
[JsonSerializable(typeof(ServerCheckRequest))]
[JsonSerializable(typeof(ServerCheckResponse))]
[JsonSerializable(typeof(ConnectionSetupRequest))]
[JsonSerializable(typeof(SetupAckRequest))]
[JsonSerializable(typeof(HealthCheckRequest))]
[JsonSerializable(typeof(HealthCheckResponse))]
[JsonSerializable(typeof(ClientDetectionRequest))]
[JsonSerializable(typeof(ClientDetectionResponse))]
[JsonSerializable(typeof(ConnectResetRequest))]
[JsonSerializable(typeof(ConnectResetResponse))]
[JsonSerializable(typeof(PushAckRequest))]
[JsonSerializable(typeof(ErrorResponse))]
internal sealed partial class NacosJsonContext : JsonSerializerContext
{
}
