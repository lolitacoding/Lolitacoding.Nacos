namespace Lolitacoding.Nacos.Locking;

/// <summary>
/// The lock types the built-in Nacos lock service accepts.
/// </summary>
/// <remarks>
/// <para>
/// The type is part of a lock's identity, so the same key can be held independently under two
/// different types. The values are the server's <c>LockConstants</c> strings.
/// </para>
/// <para>
/// Only <see cref="Mutex"/> is served by a Nacos 3.2 server: <see cref="Reentrant"/> and
/// <see cref="NonReentrant"/> are registered by the 3.3 lock service, and asking a 3.2 server for
/// one fails with <c>lockType: &lt;type&gt; is not exist.</c>
/// </para>
/// </remarks>
public static class NacosLockTypes
{
    /// <summary>
    /// The default type, which grants the lock to one owner at a time.
    /// </summary>
    public const string Mutex = "NACOS_LOCK";

    /// <summary>
    /// A type the same owner may acquire more than once, counted by the server.
    /// </summary>
    /// <remarks>
    /// This is what makes a lock reentrant: a second acquisition by the owner succeeds and
    /// increments a counter, and the lock is only released when the counter returns to zero.
    /// </remarks>
    public const string Reentrant = "REENTRANT";

    /// <summary>
    /// A type that refuses a second acquisition by the owner that already holds it.
    /// </summary>
    public const string NonReentrant = "NON_REENTRANT";
}

/// <summary>
/// Identifies a distributed lock.
/// </summary>
/// <remarks>
/// <para>
/// The name is a caller-chosen key rather than a namespaced resource: Nacos keeps locks per server
/// cluster and does not partition them by namespace, so keys from different applications share one
/// space and only a naming convention keeps them apart. A prefix that identifies the application
/// is therefore worth the effort.
/// </para>
/// <para>
/// Unlike the lock service's own <c>LockInstance</c>, this type carries no owner, lease or wait
/// time: those are properties of an acquisition rather than of the lock, and modelling them here
/// would invite a caller to believe that setting them once is enough.
/// </para>
/// </remarks>
public sealed record NacosLockKey
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NacosLockKey"/> record.
    /// </summary>
    /// <param name="name">The lock name.</param>
    /// <param name="lockType">
    /// The lock type, defaulting to <see cref="NacosLockTypes.Mutex"/>. One of
    /// <see cref="NacosLockTypes"/>, or a value a newer server understands.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> or <paramref name="lockType"/> is null, empty or whitespace.
    /// </exception>
    /// <remarks>
    /// The type is deliberately not restricted to the values this client knows. An unknown type is
    /// rejected by the server with a message that names the ones it accepts, which is a better
    /// failure than refusing to send a type a newer server might support.
    /// </remarks>
    public NacosLockKey(string name, string lockType = NacosLockTypes.Mutex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockType);

        Name = name;
        LockType = lockType;
    }

    /// <summary>Gets the lock name.</summary>
    public string Name { get; }

    /// <summary>Gets the lock type.</summary>
    public string LockType { get; init; }

    /// <inheritdoc />
    public override string ToString() => string.Concat(LockType, "/", Name);
}
