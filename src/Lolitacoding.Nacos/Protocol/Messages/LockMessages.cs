namespace Lolitacoding.Nacos.Protocol.Messages;

/// <summary>
/// Base type for requests addressed to the Nacos distributed lock module.
/// </summary>
/// <remarks>
/// The lock service is identified by the module discriminator rather than by the service name and
/// group the configuration and naming requests carry, so it needs a base type of its own. The value
/// matches <c>Constants.Lock.LOCK_MODULE</c> on the server.
/// </remarks>
internal abstract class LockRequest : NacosRequest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LockRequest"/> class.
    /// </summary>
    protected LockRequest() => Module = NacosModule.Lock;
}

/// <summary>
/// The lock state a lock request carries, mirroring the server's <c>LockInstance</c>.
/// </summary>
/// <remarks>
/// <para>
/// One type serves every operation: acquire, release, renew and cancel-wait differ only in which
/// fields the server reads and in the operation discriminator carried beside it.
/// </para>
/// <para>
/// <see cref="ExpiredTime"/> is a duration in milliseconds measured from the moment the server
/// handles the request, not an absolute timestamp. It is always sent because the server compares it
/// against zero without a null check, so an omitted value fails the request.
/// </para>
/// </remarks>
internal sealed class LockInstance
{
    /// <summary>Gets or sets the lock identifier.</summary>
    public string? Key { get; set; }

    /// <summary>Gets or sets the lease duration in milliseconds.</summary>
    /// <remarks>
    /// A negative value asks for the server's default lease, which is 30 seconds.
    /// </remarks>
    public long ExpiredTime { get; set; }

    /// <summary>Gets or sets opaque metadata the server stores with the lock.</summary>
    /// <remarks>
    /// The server records only the size of this map; nothing interprets the contents. It is
    /// carried so that a caller can attach its own context to a lock.
    /// </remarks>
    public Dictionary<string, string>? Params { get; set; }

    /// <summary>Gets or sets the lock type.</summary>
    public string? LockType { get; set; }

    /// <summary>Gets or sets the owner that holds the lock.</summary>
    /// <remarks>
    /// The server substitutes the connection identifier when this is absent, but an explicit owner
    /// is what makes reentrancy, release and the wait queue address the same holder, so this client
    /// always sends one.
    /// </remarks>
    public string? Owner { get; set; }

    /// <summary>Gets or sets how long the server may keep the request in its wait queue.</summary>
    /// <remarks>
    /// Milliseconds. <c>-1</c> is the protocol's "do not queue" value; any value greater than zero
    /// asks the server to queue the request when the lock is already held.
    /// </remarks>
    public long WaitTime { get; set; } = -1;

    /// <summary>
    /// Gets or sets a value indicating whether this attempt is a retry from the wait queue.
    /// </summary>
    /// <remarks>
    /// The server grants a retry only when the caller is at the head of the queue, which is what
    /// makes the queue first-in-first-out rather than a stampede.
    /// </remarks>
    public bool WaiterRetry { get; set; }
}

/// <summary>
/// A request that performs one operation on one lock.
/// </summary>
internal sealed class LockOperationRequest : LockRequest
{
    /// <summary>Gets or sets the lock the operation applies to.</summary>
    public LockInstance? LockInstance { get; set; }

    /// <summary>Gets or sets the operation discriminator.</summary>
    /// <remarks>
    /// One of the names of the server's <c>LockOperationEnum</c>: <c>ACQUIRE</c>, <c>RELEASE</c>,
    /// <c>RENEW</c> or <c>CANCEL_WAIT</c>. The enum's remaining member, <c>EXPIRE</c>, is produced
    /// by the server for its own bookkeeping and is never sent by a client.
    /// </remarks>
    public string? LockOperationEnum { get; set; }
}

/// <summary>
/// The outcome of a lock operation, mirroring the server's <c>LockResult</c>.
/// </summary>
/// <remarks>
/// The server also serialises a computed <c>waiting</c> flag. It is deliberately not modelled:
/// it is exactly <see cref="WaitPosition"/> being non-negative, and a read-only property would be
/// dropped on the way back in anyway.
/// </remarks>
internal sealed class LockResult
{
    /// <summary>Gets or sets a value indicating whether the operation succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets how many times the owner currently holds the lock.</summary>
    public int ReentrantCount { get; set; }

    /// <summary>Gets or sets the position in the wait queue, or <c>-1</c> when not queued.</summary>
    public int WaitPosition { get; set; } = -1;

    /// <summary>Gets or sets the reason the operation did not succeed.</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// The response to a lock operation.
/// </summary>
/// <remarks>
/// The server reports a bare boolean for operations that have no structured outcome — a lease
/// renewal returns nothing else — and a <see cref="LockResult"/> for the ones that do. Both must be
/// read, because the boolean is also the fallback a server uses when the structured form is absent.
/// </remarks>
internal sealed class LockOperationResponse : NacosResponse
{
    /// <summary>Gets or sets the bare boolean outcome.</summary>
    public bool? Result { get; set; }

    /// <summary>Gets or sets the structured outcome.</summary>
    public LockResult? LockResult { get; set; }
}

/// <summary>
/// A server push announcing that a lock a client was queued for became available, or that the
/// server gave up on it.
/// </summary>
/// <remarks>
/// This is the only lock message that travels server to client, and it is why the lock module needs
/// its own connection: the push is addressed to the connection that queued the waiter.
/// </remarks>
internal sealed class LockNotificationRequest : LockRequest
{
    /// <summary>Gets or sets the lock the notification is about.</summary>
    public string? LockKey { get; set; }

    /// <summary>Gets or sets the type of the lock.</summary>
    public string? LockType { get; set; }

    /// <summary>Gets or sets the owner that was waiting for the lock.</summary>
    public string? Owner { get; set; }

    /// <summary>Gets or sets the notification discriminator.</summary>
    /// <remarks>
    /// <c>AVAILABLE</c> when the lock may be attempted again, <c>TIMEOUT</c> when the waiter was
    /// dropped from the queue.
    /// </remarks>
    public string? NotificationType { get; set; }
}

/// <summary>
/// The acknowledgement a client sends for a lock notification.
/// </summary>
/// <remarks>
/// It carries nothing beyond the base response; the server only needs to know that the push was
/// received, which is how it stops retrying the notification.
/// </remarks>
internal sealed class LockNotificationResponse : NacosResponse
{
}
