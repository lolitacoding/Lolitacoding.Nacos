namespace Lolitacoding.Nacos.Protocol.Messages;

/// <summary>
/// Base type for requests addressed to a service identified by name and group.
/// </summary>
internal abstract class NamingRequest : NacosRequest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NamingRequest"/> class.
    /// </summary>
    protected NamingRequest() => Module = NacosModule.Naming;

    /// <summary>Gets or sets the namespace id.</summary>
    /// <remarks>
    /// Naming uses <c>namespace</c> on the wire where configuration uses <c>tenant</c>; the two
    /// subsystems were designed independently and the field names never converged.
    /// </remarks>
    public string? Namespace { get; set; }

    /// <summary>Gets or sets the service name.</summary>
    public string? ServiceName { get; set; }

    /// <summary>Gets or sets the service group name.</summary>
    public string? GroupName { get; set; }
}

/// <summary>
/// A single service instance as exchanged with the Nacos naming service.
/// </summary>
/// <remarks>
/// The heartbeat-related properties are computed by the Java client from reserved metadata keys.
/// They are modelled as ordinary properties here because the resulting wire form is identical:
/// every field is emitted with its default when the caller does not set it.
/// </remarks>
internal sealed class Instance
{
    /// <summary>The metadata key that overrides <see cref="InstanceIdGenerator"/>.</summary>
    public const string InstanceIdGeneratorKey = "preserved.instance.id.generator";

    /// <summary>The metadata key that overrides <see cref="InstanceHeartBeatInterval"/>.</summary>
    public const string InstanceHeartBeatIntervalKey = "preserved.heart.beat.interval";

    /// <summary>The metadata key that overrides <see cref="InstanceHeartBeatTimeOut"/>.</summary>
    public const string InstanceHeartBeatTimeOutKey = "preserved.heart.beat.timeout";

    /// <summary>The metadata key that overrides <see cref="IpDeleteTimeout"/>.</summary>
    public const string IpDeleteTimeoutKey = "preserved.ip.delete.timeout";

    /// <summary>Gets or sets the server-assigned instance identifier.</summary>
    public string? InstanceId { get; set; }

    /// <summary>Gets or sets the instance IP address.</summary>
    public string? Ip { get; set; }

    /// <summary>Gets or sets the instance port.</summary>
    public int Port { get; set; }

    /// <summary>Gets or sets the load-balancing weight.</summary>
    public double Weight { get; set; } = 1.0;

    /// <summary>Gets or sets a value indicating whether the instance is healthy.</summary>
    public bool Healthy { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the instance accepts traffic.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the instance is ephemeral.</summary>
    public bool Ephemeral { get; set; } = true;

    /// <summary>Gets or sets the cluster the instance belongs to.</summary>
    public string? ClusterName { get; set; }

    /// <summary>Gets or sets the service the instance belongs to.</summary>
    public string? ServiceName { get; set; }

    /// <summary>Gets or sets the instance metadata.</summary>
    public Dictionary<string, string>? Metadata { get; set; } = [];

    /// <summary>Gets or sets the instance id generation strategy.</summary>
    public string InstanceIdGenerator { get; set; } = "simple";

    /// <summary>Gets or sets the heartbeat interval in milliseconds.</summary>
    public long InstanceHeartBeatInterval { get; set; } = 5000;

    /// <summary>Gets or sets the heartbeat timeout in milliseconds.</summary>
    public long InstanceHeartBeatTimeOut { get; set; } = 15000;

    /// <summary>Gets or sets the delay before an unreachable instance is removed, in milliseconds.</summary>
    public long IpDeleteTimeout { get; set; } = 30000;

    /// <summary>
    /// Copies any reserved metadata values onto the matching computed properties.
    /// </summary>
    /// <remarks>
    /// The Java client stores these as metadata entries and exposes them through derived getters.
    /// Honouring the metadata form keeps wire compatibility with clients that set them that way.
    /// </remarks>
    public void ApplyReservedMetadata()
    {
        if (Metadata is null)
        {
            return;
        }

        if (Metadata.TryGetValue(InstanceIdGeneratorKey, out string? generator) && !string.IsNullOrEmpty(generator))
        {
            InstanceIdGenerator = generator;
        }

        if (TryReadLong(InstanceHeartBeatIntervalKey, out long interval))
        {
            InstanceHeartBeatInterval = interval;
        }

        if (TryReadLong(InstanceHeartBeatTimeOutKey, out long timeout))
        {
            InstanceHeartBeatTimeOut = timeout;
        }

        if (TryReadLong(IpDeleteTimeoutKey, out long deleteTimeout))
        {
            IpDeleteTimeout = deleteTimeout;
        }
    }

    private bool TryReadLong(string key, out long value)
    {
        value = 0;
        return Metadata is not null
            && Metadata.TryGetValue(key, out string? raw)
            && long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>
/// Registers, deregisters or updates a single instance.
/// <c>metadata.type = "InstanceRequest"</c>.
/// </summary>
/// <remarks>
/// Not sealed: <see cref="PersistentInstanceRequest"/> extends it to reach a different server
/// handler while keeping the same payload shape.
/// </remarks>
internal class InstanceRequest : NamingRequest
{
    /// <summary>The <c>type</c> value that registers an instance.</summary>
    public const string RegisterType = "registerInstance";

    /// <summary>The <c>type</c> value that deregisters an instance.</summary>
    public const string DeregisterType = "deregisterInstance";

    /// <summary>Gets or sets the operation to perform.</summary>
    public string? Type { get; set; }

    /// <summary>Gets or sets the instance the operation applies to.</summary>
    public Instance? Instance { get; set; }
}

/// <summary>
/// Confirms an instance operation. <c>metadata.type = "InstanceResponse"</c>.
/// </summary>
/// <remarks>
/// Not sealed: <see cref="BatchInstanceResponse"/> extends it, matching the Nacos type hierarchy.
/// </remarks>
internal class InstanceResponse : NacosResponse
{
    /// <summary>Gets or sets the operation that was performed.</summary>
    public string? Type { get; set; }
}

/// <summary>
/// Applies a single operation to several instances.
/// <c>metadata.type = "BatchInstanceRequest"</c>.
/// </summary>
internal sealed class BatchInstanceRequest : NamingRequest
{
    /// <summary>The <c>type</c> value that registers every instance in the request.</summary>
    public const string RegisterType = "batchRegisterInstance";

    /// <summary>Gets or sets the operation to perform.</summary>
    public string? Type { get; set; }

    /// <summary>Gets or sets the instances the operation applies to.</summary>
    public List<Instance>? Instances { get; set; }
}

/// <summary>Confirms a batch instance operation.</summary>
internal sealed class BatchInstanceResponse : InstanceResponse
{
}

/// <summary>
/// Registers a persistent (non-ephemeral) instance.
/// <c>metadata.type = "PersistentInstanceRequest"</c>.
/// </summary>
/// <remarks>
/// The server routes this to a different handler than <see cref="InstanceRequest"/>, which is
/// what makes the instance durable; the payload shape is otherwise identical.
/// </remarks>
internal sealed class PersistentInstanceRequest : InstanceRequest
{
}

/// <summary>
/// Queries the instances of a service. <c>metadata.type = "ServiceQueryRequest"</c>.
/// </summary>
internal sealed class ServiceQueryRequest : NamingRequest
{
    /// <summary>Gets or sets the cluster filter.</summary>
    public string? Cluster { get; set; }

    /// <summary>Gets or sets a value indicating whether unhealthy instances are filtered out.</summary>
    public bool HealthyOnly { get; set; }

    /// <summary>Gets or sets the legacy UDP push port.</summary>
    /// <remarks>
    /// Retained for wire compatibility with Nacos 1.x style clients; gRPC clients send <c>0</c>.
    /// </remarks>
    public int UdpPort { get; set; }
}

/// <summary>
/// A service together with its instances and cache metadata.
/// </summary>
internal sealed class ServiceInfo
{
    /// <summary>Gets or sets the service name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the service group name.</summary>
    public string? GroupName { get; set; }

    /// <summary>Gets or sets the cluster list.</summary>
    public string? Clusters { get; set; }

    /// <summary>Gets or sets the client-side cache duration in milliseconds.</summary>
    public long CacheMillis { get; set; } = 1000;

    /// <summary>Gets or sets the instances of the service.</summary>
    public List<Instance>? Hosts { get; set; } = [];

    /// <summary>Gets or sets the server timestamp of the last refresh.</summary>
    public long LastRefTime { get; set; }

    /// <summary>
    /// Gets or sets the checksum the server reported for the list. Nacos 3.x leaves it empty in the
    /// pushes it sends, so it cannot be used to detect changes.
    /// </summary>
    public string? Checksum { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the list contains all instances rather than only
    /// the healthy ones.
    /// </summary>
    /// <remarks>
    /// The Java property is <c>isAllIps</c> over a field named <c>allIPs</c>, so the wire name is
    /// <c>allIPs</c> with a capitalised <c>IP</c>.
    /// </remarks>
    public bool AllIPs { get; set; }

    /// <summary>Gets or sets a value indicating whether the service reached its protection threshold.</summary>
    public bool ReachProtectionThreshold { get; set; }

    /// <summary>Gets or sets a value indicating whether the payload is valid.</summary>
    public bool Valid { get; set; } = true;
}

/// <summary>
/// Carries the instances of a queried service.
/// <c>metadata.type = "QueryServiceResponse"</c>.
/// </summary>
/// <remarks>
/// Note the naming asymmetry: the request is <c>ServiceQueryRequest</c> but the response is
/// <c>QueryServiceResponse</c>, not <c>ServiceQueryResponse</c>.
/// </remarks>
internal sealed class QueryServiceResponse : NacosResponse
{
    /// <summary>Gets or sets the service and its instances.</summary>
    public ServiceInfo? ServiceInfo { get; set; }
}

/// <summary>
/// Lists the service names of a namespace. <c>metadata.type = "ServiceListRequest"</c>.
/// </summary>
internal sealed class ServiceListRequest : NamingRequest
{
    /// <summary>Gets or sets the one-based page number.</summary>
    public int PageNo { get; set; } = 1;

    /// <summary>Gets or sets the page size.</summary>
    public int PageSize { get; set; } = 10;

    /// <summary>Gets or sets the selector expression.</summary>
    public string? Selector { get; set; }
}

/// <summary>
/// Returns a page of service names. <c>metadata.type = "ServiceListResponse"</c>.
/// </summary>
internal sealed class ServiceListResponse : NacosResponse
{
    /// <summary>Gets or sets the total number of services.</summary>
    public int Count { get; set; }

    /// <summary>Gets or sets the service names on this page.</summary>
    public List<string>? ServiceNames { get; set; }
}

/// <summary>
/// Subscribes to or unsubscribes from a service.
/// <c>metadata.type = "SubscribeServiceRequest"</c>.
/// </summary>
internal sealed class SubscribeServiceRequest : NamingRequest
{
    /// <summary>Gets or sets a value indicating whether to subscribe or unsubscribe.</summary>
    public bool Subscribe { get; set; }

    /// <summary>Gets or sets the comma-separated cluster filter.</summary>
    public string? Clusters { get; set; }
}

/// <summary>
/// Confirms a subscription and returns the current service state.
/// <c>metadata.type = "SubscribeServiceResponse"</c>.
/// </summary>
internal sealed class SubscribeServiceResponse : NacosResponse
{
    /// <summary>Gets or sets the service and its instances.</summary>
    public ServiceInfo? ServiceInfo { get; set; }
}

/// <summary>
/// Server push delivering an updated service state to a subscriber.
/// <c>metadata.type = "NotifySubscriberRequest"</c>.
/// </summary>
/// <remarks>
/// This extends the Java <c>ServerRequest</c> base rather than the naming request base, so it
/// declares its own namespace/service/group fields. The server leaves those unset: it builds the
/// push from a service info alone (<c>NotifySubscriberRequest.buildNotifySubscriberRequest</c>), so
/// the identity of the pushed service is only ever in <see cref="ServiceInfo"/>. It is pushed over
/// the notification stream and carries the full instance list, which lets the client skip a
/// follow-up query.
/// </remarks>
internal sealed class NotifySubscriberRequest : NacosRequest
{
    /// <summary>Gets or sets the namespace id.</summary>
    public string? Namespace { get; set; }

    /// <summary>Gets or sets the service name.</summary>
    public string? ServiceName { get; set; }

    /// <summary>Gets or sets the service group name.</summary>
    public string? GroupName { get; set; }

    /// <summary>Gets or sets the updated service state.</summary>
    public ServiceInfo? ServiceInfo { get; set; }
}

/// <summary>Acknowledges a subscriber notification. Carries no fields.</summary>
internal sealed class NotifySubscriberResponse : NacosResponse
{
}

/// <summary>
/// Registers or cancels a naming fuzzy watch.
/// <c>metadata.type = "NamingFuzzyWatchRequest"</c>.
/// </summary>
/// <remarks>
/// <para>
/// A naming fuzzy watch names a pattern over namespace, group and service name, and the server
/// keeps the matching set of service keys in sync with the client. Unlike a subscription it carries
/// no instance lists: it answers <em>which services exist</em>, and a consumer that needs the
/// instances subscribes to the service it learned about.
/// </para>
/// <para>
/// <see cref="ReceivedGroupKeys"/> is the set of service keys the client already holds, which is
/// what makes the answer a diff rather than the whole match. It is sent on every request, including
/// a cancellation, exactly as the official client does.
/// </para>
/// </remarks>
internal sealed class NamingFuzzyWatchRequest : NamingRequest
{
    /// <summary>
    /// Gets or sets the pattern, as <c>namespace&gt;&gt;groupPattern&gt;&gt;serviceNamePattern</c>.
    /// </summary>
    public string? GroupKeyPattern { get; set; }

    /// <summary>
    /// Gets or sets the service keys the client already holds, each
    /// <c>namespace@@group@@service</c>.
    /// </summary>
    public List<string>? ReceivedGroupKeys { get; set; }

    /// <summary>
    /// Gets or sets the watch type: <c>WATCH</c> to register, <c>CANCEL_WATCH</c> to remove.
    /// </summary>
    public string? WatchType { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the client is still in its initial synchronisation.
    /// </summary>
    /// <remarks>
    /// Nacos' Java bean getter is <c>isInitializing()</c>, which Jackson maps to
    /// <c>initializing</c>; the property name is what produces that wire name.
    /// </remarks>
    public bool Initializing { get; set; }
}

/// <summary>
/// Confirms a naming fuzzy watch registration or cancellation. Carries no fields.
/// <c>metadata.type = "NamingFuzzyWatchResponse"</c>.
/// </summary>
internal sealed class NamingFuzzyWatchResponse : NacosResponse
{
}

/// <summary>
/// One service in a naming fuzzy watch diff.
/// </summary>
internal sealed class NamingFuzzyWatchContext
{
    /// <summary>Gets or sets the service key, as <c>namespace@@group@@service</c>.</summary>
    public string? ServiceKey { get; set; }

    /// <summary>
    /// Gets or sets the change type: <c>ADD_SERVICE</c> or <c>DELETE_SERVICE</c>.
    /// </summary>
    public string? ChangedType { get; set; }
}

/// <summary>
/// Server push carrying the difference between the client's set and the server's match.
/// <c>metadata.type = "NamingFuzzyWatchSyncRequest"</c>.
/// </summary>
/// <remarks>
/// Arrives in batches during initialisation (<see cref="FuzzyWatchProtocol.InitNotify"/>), on a
/// later diff (<see cref="FuzzyWatchProtocol.DiffSyncNotify"/>), and once more with
/// <see cref="FuzzyWatchProtocol.FinishInitNotify"/> to end the initial phase. The last one has no
/// contexts. The server sends ten services per batch for naming, where configuration sends twenty.
/// </remarks>
internal sealed class NamingFuzzyWatchSyncRequest : NacosRequest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NamingFuzzyWatchSyncRequest"/> class.
    /// </summary>
    public NamingFuzzyWatchSyncRequest() => Module = NacosModule.Naming;

    /// <summary>Gets or sets the pattern this diff belongs to.</summary>
    public string? GroupKeyPattern { get; set; }

    /// <summary>Gets or sets the services that differ.</summary>
    public List<NamingFuzzyWatchContext>? Contexts { get; set; }

    /// <summary>Gets or sets the sync type.</summary>
    public string? SyncType { get; set; }

    /// <summary>Gets or sets the total number of batches in this sync.</summary>
    public int TotalBatch { get; set; }

    /// <summary>Gets or sets the index of this batch.</summary>
    public int CurrentBatch { get; set; }
}

/// <summary>
/// Server push announcing that one service in a fuzzy watched pattern changed.
/// <c>metadata.type = "NamingFuzzyWatchChangeNotifyRequest"</c>.
/// </summary>
/// <remarks>
/// <para>
/// The push carries only the service key and the change type. The server derives the type from
/// whether the service still exists: <c>ADD_SERVICE</c> when the first instance of a service
/// appears, <c>DELETE_SERVICE</c> when its last instance goes away, <c>INSTANCE_CHANGED</c> when an
/// instance is added to or removed from a service that already exists, and <c>HEART_BEAT</c> for a
/// heartbeat. It only pushes to a pattern when the matched membership actually changed, so an
/// <c>INSTANCE_CHANGED</c> is not sent for a service that stays matched.
/// </para>
/// <para>
/// The official Java client folds <c>ADD_SERVICE</c> and <c>INSTANCE_CHANGED</c> into its single
/// "add" notification. This client reports an <c>INSTANCE_CHANGED</c> for an already known service
/// as <see cref="Lolitacoding.Nacos.Naming.NacosServiceWatchKind.InstancesChanged"/> instead, so a
/// push the server did send is never silently discarded; the matched set is unchanged either way.
/// </para>
/// </remarks>
internal sealed class NamingFuzzyWatchChangeNotifyRequest : NacosRequest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NamingFuzzyWatchChangeNotifyRequest"/> class.
    /// </summary>
    public NamingFuzzyWatchChangeNotifyRequest() => Module = NacosModule.Naming;

    /// <summary>
    /// Gets or sets the sync type, which the server sets to
    /// <see cref="FuzzyWatchProtocol.ResourceChanged"/>.
    /// </summary>
    public string? SyncType { get; set; }

    /// <summary>Gets or sets the service key, as <c>namespace@@group@@service</c>.</summary>
    public string? ServiceKey { get; set; }

    /// <summary>
    /// Gets or sets the change type: <c>ADD_SERVICE</c>, <c>DELETE_SERVICE</c>,
    /// <c>INSTANCE_CHANGED</c> or <c>HEART_BEAT</c>.
    /// </summary>
    public string? ChangedType { get; set; }
}

/// <summary>
/// Acknowledges a naming fuzzy watch diff or change notification. Carries no fields.
/// <c>metadata.type = "NamingFuzzyWatchChangeNotifyResponse"</c>.
/// </summary>
/// <remarks>
/// The official client answers both push types with this one response, and the server only
/// inspects the result code. The API does declare a separate <c>NamingFuzzyWatchSyncResponse</c>,
/// but no client handler returns it, so this client does not carry one either.
/// </remarks>
internal sealed class NamingFuzzyWatchChangeNotifyResponse : NacosResponse
{
}
