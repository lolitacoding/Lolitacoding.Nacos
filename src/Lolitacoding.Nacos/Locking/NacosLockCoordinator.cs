using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Locking;

/// <summary>
/// Turns the lock client's request-and-reply operations into locks that hold themselves.
/// </summary>
/// <remarks>
/// <para>
/// The low-level client asks the server for a lock once and reports what it was told. This is what a
/// caller normally uses instead: it waits for a lock rather than reporting that somebody else has
/// it, keeps the lease renewed while the lock is held, releases it when the last lease is released,
/// and tracks re-entrancy so a nested caller does not have to know the lock is already held.
/// </para>
/// <para>
/// The owner of an acquisition is minted per holding rather than reused across them. Re-using an
/// owner would make the server count a re-acquisition from the same owner as re-entrant — which is
/// correct while the old holding is still alive, but not after it was released, when the release may
/// still be in flight and would then decrement the acquisition that followed it. A fresh owner per
/// holding makes every acquisition unambiguous to the server, and the re-entrancy the caller sees is
/// decided here rather than by the server.
/// </para>
/// <para>
/// Waiters are the one thing the server still arbitrates: an acquisition that has to wait joins the
/// server's queue, because only the server knows whether the lock is free. It re-queues itself after
/// asking, which is why the wait belongs to this type and not to the caller.
/// </para>
/// </remarks>
internal sealed class NacosLockCoordinator : IDisposable
{
    /// <summary>
    /// How long one attempt at an open-ended wait asks the server to keep the caller queued.
    /// </summary>
    /// <remarks>
    /// Nacos' own client uses five minutes here. A long queue time is correct because the queue
    /// entry, not the client, is what the server offers the lock to; a shorter one only means the
    /// client re-queues more often. The wait still ends when the caller stops.
    /// </remarks>
    private static readonly TimeSpan ServerWaitTime = TimeSpan.FromMinutes(5);

    private readonly INacosLockClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly NacosLockWatchdog _watchdog;
    private readonly AsyncLocal<NacosLockFlow?> _held = new();
    private readonly object _heldGate = new();
    private readonly string _clientId;

    private int _owners;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosLockCoordinator"/> class.
    /// </summary>
    /// <param name="client">The client the lock operations are sent through.</param>
    /// <param name="clientId">
    /// This client's own identifier, used as the readable part of the owner of each acquisition.
    /// </param>
    /// <param name="timeProvider">The clock used for wait deadlines and renewals.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public NacosLockCoordinator(
        INacosLockClient client,
        string clientId,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _client = client;
        _clientId = clientId;
        _timeProvider = timeProvider;
        _logger = loggerFactory.CreateLogger<NacosLockCoordinator>();
        _watchdog = new NacosLockWatchdog(
            RenewAsync, timeProvider, loggerFactory.CreateLogger<NacosLockWatchdog>());
    }

    /// <summary>
    /// Gets the current time as the server measures it: milliseconds since the Unix epoch.
    /// </summary>
    private long Now => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>
    /// Acquires a lock, waiting for as long as it takes.
    /// </summary>
    /// <param name="held">The locks the calling flow holds.</param>
    /// <param name="key">The lock to acquire.</param>
    /// <param name="options">The lease and the metadata of the acquisition.</param>
    /// <param name="ct">A token that cancels the wait.</param>
    /// <returns>The lease, which is never already held by another owner.</returns>
    /// <exception cref="LockRecursionException">
    /// The flow already holds the lock and its type does not allow the same holder to acquire it
    /// twice.
    /// </exception>
    /// <exception cref="NacosServerException">
    /// The connected server does not implement the lock service, or rejected the request.
    /// </exception>
    /// <exception cref="NacosConnectionException">The server could not be reached.</exception>
    public async Task<NacosLockLease> AcquireAsync(
        NacosLockFlow held,
        NacosLockKey key,
        NacosLockOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);

        NacosLockLease? reentered = Reenter(held, key);
        if (reentered is not null)
        {
            return reentered;
        }

        string owner = NextOwner();

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            NacosLockResult result = await _client
                .AcquireAsync(key, Request(owner, options, ServerWaitTime), ct)
                .ConfigureAwait(false);

            if (result.Success)
            {
                return Take(held, key, owner, options);
            }
        }
    }

    /// <summary>
    /// Acquires a lock, giving up after a while.
    /// </summary>
    /// <param name="held">The locks the calling flow holds.</param>
    /// <param name="key">The lock to acquire.</param>
    /// <param name="options">The lease and the metadata of the acquisition.</param>
    /// <param name="wait">
    /// How long to wait for a lock that is held. <see cref="TimeSpan.Zero"/> asks the server once and
    /// does not queue.
    /// </param>
    /// <param name="ct">A token that cancels the wait.</param>
    /// <returns>The lease, or <see langword="null"/> when the lock was not acquired in time.</returns>
    /// <exception cref="LockRecursionException">
    /// The flow already holds the lock and its type does not allow the same holder to acquire it
    /// twice.
    /// </exception>
    /// <exception cref="NacosServerException">
    /// The connected server does not implement the lock service, or rejected the request.
    /// </exception>
    /// <exception cref="NacosConnectionException">The server could not be reached.</exception>
    public async Task<NacosLockLease?> TryAcquireAsync(
        NacosLockFlow held,
        NacosLockKey key,
        NacosLockOptions options,
        TimeSpan wait,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);

        NacosLockLease? reentered = Reenter(held, key);
        if (reentered is not null)
        {
            return reentered;
        }

        string owner = NextOwner();

        if (wait <= TimeSpan.Zero)
        {
            // Asked once. The server is told not to queue, so its answer is final.
            NacosLockResult attempt = await _client
                .AcquireAsync(key, Request(owner, options, TimeSpan.Zero), ct)
                .ConfigureAwait(false);

            return attempt.Success ? Take(held, key, owner, options) : null;
        }

        long deadline = Now + (long)Math.Ceiling(wait.TotalMilliseconds);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            long remaining = deadline - Now;
            if (remaining <= 0)
            {
                return null;
            }

            // The server queues the caller for what is left of the wait, so an attempt that is
            // dropped early — the server gave the queue entry up — is re-queued for the remainder
            // rather than ending the wait.
            NacosLockResult result = await _client
                .AcquireAsync(key, Request(owner, options, TimeSpan.FromMilliseconds(remaining)), ct)
                .ConfigureAwait(false);

            if (result.Success)
            {
                return Take(held, key, owner, options);
            }

            if (Now >= deadline)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Gives up one lease of a holding, releasing the lock when it was the last one.
    /// </summary>
    /// <param name="holding">The holding one lease of which is being given up.</param>
    /// <returns>A task that completes once the release finished.</returns>
    /// <remarks>
    /// A release that the server refuses, or that cannot be sent, is logged rather than thrown: the
    /// caller is done with the lock either way, and the server frees it when the lease expires. The
    /// release is sent with no cancellation token because it must not be skipped when the work that
    /// held the lock was cancelled.
    /// </remarks>
    public async ValueTask ReleaseAsync(NacosLockHolding holding)
    {
        ArgumentNullException.ThrowIfNull(holding);

        if (!holding.Exit())
        {
            // A lease the flow still holds counts for the lock, so there is nothing to release.
            return;
        }

        holding.Flow?.Remove(holding);
        _watchdog.Unregister(holding);

        try
        {
            NacosLockResult result = await _client
                .ReleaseAsync(holding.Key, holding.Owner, CancellationToken.None)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                NacosLockLog.LeaseReleaseFailed(
                    _logger, holding.Key.Name, holding.Owner, result.ErrorMessage ?? "no reason reported");
            }
        }
        catch (Exception ex)
        {
            NacosLockLog.LeaseReleaseFailed(_logger, holding.Key.Name, holding.Owner, ex.Message);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Every lock this client holds is given up, and every renewal is stopped. The connection is
    /// closed by the client itself, which is what tells the server those locks are free.
    /// </remarks>
    public void Dispose() => _watchdog.Dispose();

    /// <summary>Gets the locks the calling flow holds, creating the flow's record if it has none.</summary>
    /// <returns>The flow's locks.</returns>
    /// <remarks>
    /// <para>
    /// This must be called from a method that runs synchronously in the caller's own frame — the
    /// public acquire methods — and never from inside an asynchronous method. A value written to an
    /// <see cref="AsyncLocal{T}"/> by an async method is part of that method's own execution context
    /// and is not visible to the caller that awaits it, so a record created here from an async frame
    /// would be replaced on the next call — and the lock a flow holds would stop being recognised as
    /// its own.
    /// </para>
    /// <para>
    /// The gate only covers the record's creation. Two flows racing here each end up with their own,
    /// because the value being written is the flow's own; the gate is what stops one flow from
    /// creating two records and losing a lock it already holds in the other.
    /// </para>
    /// </remarks>
    internal NacosLockFlow Held()
    {
        NacosLockFlow? held = _held.Value;

        if (held is not null)
        {
            return held;
        }

        lock (_heldGate)
        {
            return _held.Value ??= new NacosLockFlow();
        }
    }

    /// <summary>
    /// Re-enters a lock this flow already holds.
    /// </summary>
    /// <param name="held">The locks this flow holds.</param>
    /// <param name="key">The lock being acquired.</param>
    /// <returns>The lease, or <see langword="null"/> when the flow does not hold the lock.</returns>
    /// <exception cref="LockRecursionException">
    /// The flow holds the lock and its type does not allow the same holder to acquire it twice.
    /// </exception>
    private NacosLockLease? Reenter(NacosLockFlow held, NacosLockKey key)
    {
        NacosLockHolding? holding = held.Find(key);

        if (holding is null)
        {
            return null;
        }

        if (!holding.IsHeld)
        {
            // The record is of a lock this flow gave up, so it says nothing about the acquisition
            // that is being made now.
            held.Remove(holding);
            return null;
        }

        if (!string.Equals(key.LockType, NacosLockTypes.Reentrant, StringComparison.Ordinal))
        {
            throw new LockRecursionException(
                string.Concat(
                    "The flow already holds the lock '",
                    key.Name,
                    "' as type '",
                    key.LockType,
                    "', which does not allow one holder to acquire it twice."));
        }

        holding.Enter();
        return new NacosLockLease(this, holding);
    }

    /// <summary>
    /// Files an acquisition that the server granted.
    /// </summary>
    /// <param name="held">The locks this flow holds.</param>
    /// <param name="key">The lock that was acquired.</param>
    /// <param name="owner">The owner the server granted it to.</param>
    /// <param name="options">The options the acquisition used.</param>
    /// <returns>The lease.</returns>
    private NacosLockLease Take(NacosLockFlow held, NacosLockKey key, string owner, NacosLockOptions options)
    {
        var holding = new NacosLockHolding(key, owner, options.LeaseDuration);

        held.Add(holding);
        holding.Enter();
        _watchdog.Register(holding);

        return new NacosLockLease(this, holding);
    }

    /// <summary>Mints the owner of one acquisition.</summary>
    /// <returns>The owner.</returns>
    /// <remarks>
    /// The client's own identifier is followed by a serial number so that an operator reading a lock
    /// on the server can tell which client holds it and which of that client's acquisitions it is.
    /// </remarks>
    private string NextOwner() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{_clientId}:{Interlocked.Increment(ref _owners)}");

    /// <summary>Builds the acquisition the low-level client sends.</summary>
    /// <param name="owner">The owner of the acquisition.</param>
    /// <param name="options">The caller's options.</param>
    /// <param name="wait">How long the server should queue the caller.</param>
    /// <returns>The acquisition options.</returns>
    private static NacosLockAcquireOptions Request(string owner, NacosLockOptions options, TimeSpan wait) => new()
    {
        Owner = owner,
        LeaseDuration = options.LeaseDuration,
        Parameters = options.Parameters,
        WaitTime = wait,
    };

    /// <summary>Extends the lease of a holding.</summary>
    /// <param name="holding">The holding to renew.</param>
    /// <param name="ct">A token that cancels the renewal.</param>
    /// <returns>Whether the server accepted the renewal.</returns>
    private async Task<bool> RenewAsync(NacosLockHolding holding, CancellationToken ct)
    {
        NacosLockResult result = await _client
            .RenewAsync(holding.Key, holding.Owner, holding.LeaseDuration, ct)
            .ConfigureAwait(false);

        return result.Success;
    }
}
