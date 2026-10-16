namespace Lolitacoding.Nacos.Locking;

/// <summary>
/// The locks one asynchronous flow holds.
/// </summary>
/// <remarks>
/// <para>
/// Ownership of a distributed lock has to name a holder, and a holder has to be something a nested
/// caller can recognise as itself. Nacos' own client uses the calling thread for that, which does not
/// carry over to .NET: asynchronous work has no fixed thread, and two pieces of work on one thread
/// are not the same holder. The flow that flows through <see cref="System.Threading.AsyncLocal{T}"/>
/// is the equivalent: a nested call, and a task started from the flow holding the lock, run in the
/// same flow and therefore hold what it holds.
/// </para>
/// <para>
/// The consequence to keep in mind is that work which is *concurrent* with the holder but was started
/// from inside it — a task started while the lock is held — is part of the same flow and re-enters
/// rather than blocking. Work that must exclude the holder asks the low-level client for the lock
/// with an owner of its own, or starts on a flow that inherits nothing.
/// </para>
/// <para>
/// A flow is looked up by lock key, because one flow may hold several locks at once. Holders are kept
/// here rather than pooled per lock: an entry only exists while the flow holds the lock, so a
/// long-lived flow does not accumulate one entry per lock it ever took.
/// </para>
/// <para>
/// The record is created — and filed against the flow — by the public acquire methods, which run
/// synchronously in the caller's frame. That is not a detail: a value written to an
/// <see cref="System.Threading.AsyncLocal{T}"/> inside an <c>async</c> method belongs to that
/// method's own execution context and is lost to the caller that awaits it, so a record created
/// anywhere below the public API would be invisible to the next acquisition in the same flow.
/// </para>
/// </remarks>
internal sealed class NacosLockFlow
{
    private readonly Dictionary<NacosLockKey, NacosLockHolding> _locks = new();
    private readonly object _gate = new();

    /// <summary>Finds the holder of a lock, if this flow holds it.</summary>
    /// <param name="key">The lock to look up.</param>
    /// <returns>The holder, or <see langword="null"/> when the flow does not hold the lock.</returns>
    public NacosLockHolding? Find(NacosLockKey key)
    {
        lock (_gate)
        {
            return _locks.TryGetValue(key, out NacosLockHolding? holding) ? holding : null;
        }
    }

    /// <summary>Files a holder so nested calls in this flow find it.</summary>
    /// <param name="holding">The holder.</param>
    /// <remarks>
    /// A second holder for one key in one flow cannot be reached: the earlier holder would have made
    /// the acquisition re-entrant, so this never replaces a holder that is still held.
    /// </remarks>
    public void Add(NacosLockHolding holding)
    {
        lock (_gate)
        {
            _locks[holding.Key] = holding;
            holding.Flow = this;
        }
    }

    /// <summary>Unfiles a holder, because it no longer holds its lock.</summary>
    /// <param name="holding">The holder.</param>
    public void Remove(NacosLockHolding holding)
    {
        lock (_gate)
        {
            if (_locks.TryGetValue(holding.Key, out NacosLockHolding? found)
                && ReferenceEquals(found, holding))
            {
                _locks.Remove(holding.Key);
                holding.Flow = null;
            }
        }
    }
}
