namespace Lolitacoding.Nacos.Locking;

/// <summary>
/// The outcome of a lock operation.
/// </summary>
/// <remarks>
/// <para>
/// A failed operation is reported as a result rather than as an exception, because "the lock is
/// held by somebody else" and "the waiter gave up" are ordinary outcomes of asking for a lock, not
/// failures of the call. An exception is reserved for the cases where the request itself could not
/// be carried out: an unreachable server, a rejected token, or a server that reports the operation
/// as invalid.
/// </para>
/// <para>
/// This is the same triple the server reports — success, reentrant count and queue position — kept
/// as one immutable value so a caller cannot observe a half-updated outcome.
/// </para>
/// </remarks>
public sealed record NacosLockResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NacosLockResult"/> record.
    /// </summary>
    /// <param name="success">Whether the operation succeeded.</param>
    /// <param name="reentrantCount">How many times the owner holds the lock.</param>
    /// <param name="waitPosition">The queue position, or <c>-1</c> when not queued.</param>
    /// <param name="errorMessage">Why the operation did not succeed.</param>
    internal NacosLockResult(bool success, int reentrantCount, int waitPosition, string? errorMessage)
    {
        Success = success;
        ReentrantCount = reentrantCount;
        WaitPosition = waitPosition;
        ErrorMessage = errorMessage;
    }

    /// <summary>Gets a value indicating whether the operation succeeded.</summary>
    public bool Success { get; }

    /// <summary>
    /// Gets how many times the owner holds the lock after the operation.
    /// </summary>
    /// <remarks>
    /// Zero on a release that dropped the lock, and the new depth on a successful reentrant
    /// acquire. An acquire that reports a count greater than one means the owner already held the
    /// lock and the server counted the attempt rather than granting it afresh, so the lock must be
    /// released as many times as it was acquired. A renewal reports zero because the server answers
    /// it with a bare outcome that carries no count.
    /// </remarks>
    public int ReentrantCount { get; }

    /// <summary>Gets the position in the server's wait queue, or <c>-1</c> when not queued.</summary>
    public int WaitPosition { get; }

    /// <summary>Gets the reason the operation did not succeed.</summary>
    /// <remarks>
    /// The server produces this for both a lock held by another owner and a waiter the server
    /// dropped, so it is the message rather than the shape that distinguishes them.
    /// </remarks>
    public string? ErrorMessage { get; }

    /// <summary>
    /// Gets a value indicating whether the operation left the caller in the wait queue.
    /// </summary>
    public bool Waiting => WaitPosition >= 0;

    /// <inheritdoc />
    public override string ToString()
    {
        if (Success)
        {
            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Locked (count {ReentrantCount})");
        }

        return Waiting
            ? string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Waiting at position {WaitPosition}")
            : string.Concat("Not locked: ", ErrorMessage ?? "no reason reported");
    }
}
