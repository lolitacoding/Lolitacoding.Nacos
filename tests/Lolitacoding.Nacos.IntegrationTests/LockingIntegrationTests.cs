using Lolitacoding.Nacos.Client;
using Lolitacoding.Nacos.Locking;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.IntegrationTests;

/// <summary>
/// Verifies the distributed lock end to end against a real Nacos server.
/// </summary>
/// <remarks>
/// <para>
/// The lock service is a Raft-backed service on the server, and the parts worth checking against the
/// real thing cannot be scripted: that the server holds a lock against the owner the client named,
/// that it refuses a second owner while the lease lasts, that it hands the lock over to a queued
/// waiter when the holder releases it, and that a renewal pushes the expiry out rather than being
/// accepted and ignored.
/// </para>
/// <para>
/// The container is started per test, as in the rest of the suite, but from an image of its own: the
/// lock service these exercise is the 3.3 one. A 3.2 server's lock module only knows <c>ACQUIRE</c>
/// and <c>RELEASE</c> — no renewal, no wait queue, no reentrant count — and it registers
/// <c>NACOS_LOCK</c> as its only lock type, so the parts that need the newer contract cannot be
/// observed against it at all.
/// </para>
/// </remarks>
[Trait(NacosTraits.Category, NacosTraits.Integration)]
public sealed class LockingIntegrationTests
{
    /// <summary>
    /// A lease long enough that only an explicit release ends it.
    /// </summary>
    /// <remarks>
    /// Every test that uses it releases the lock itself, so a lease that outlives the test is what
    /// makes a failure to release show up as a failure to reacquire rather than as an unrelated
    /// timing coincidence.
    /// </remarks>
    private static readonly TimeSpan LongLease = TimeSpan.FromSeconds(30);

    /// <summary>The owner a test holds a lock as when it names one itself.</summary>
    private const string Owner = "integration-owner";

    [Fact]
    public async Task AcquireAndRelease_RoundTripsThroughARealServer()
    {
        await using NacosContainer container = await NacosContainer.StartAsync(NacosIntegrationImages.LockImage);
        await using NacosClientFactory holder = CreateClient(container);
        await using NacosClientFactory other = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;
        var key = new NacosLockKey(NacosHttp.UniqueId("lock"));
        var options = new NacosLockAcquireOptions { LeaseDuration = LongLease };

        NacosLockResult acquired = await holder.Lock.AcquireAsync(key, options, ct);

        acquired.Success.ShouldBeTrue();
        acquired.ReentrantCount.ShouldBe(1);
        acquired.ErrorMessage.ShouldBeNull();

        // The same lock, from a client that never held it, is refused rather than granted: the owner
        // the request carried is what the server holds the lock against.
        NacosLockResult refused = await other.Lock.AcquireAsync(key, options, ct);

        refused.Success.ShouldBeFalse();
        refused.Waiting.ShouldBeFalse();
        refused.ErrorMessage.ShouldNotBeNullOrWhiteSpace();

        NacosLockResult released = await holder.Lock.ReleaseAsync(key, ct: ct);

        released.Success.ShouldBeTrue();

        // Released means released: the second client takes the lock without waiting out the lease.
        NacosLockResult retaken = await other.Lock.AcquireAsync(key, options, ct);

        retaken.Success.ShouldBeTrue();
        await other.Lock.ReleaseAsync(key, ct: ct);
    }

    [Fact]
    public async Task ReentrantLock_CountsEveryAcquisitionOfOneOwner()
    {
        await using NacosContainer container = await NacosContainer.StartAsync(NacosIntegrationImages.LockImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;
        var key = new NacosLockKey(NacosHttp.UniqueId("lock-reentrant"), NacosLockTypes.Reentrant);
        var options = new NacosLockAcquireOptions { Owner = Owner, LeaseDuration = LongLease };

        NacosLockResult acquired = await client.Lock.AcquireAsync(key, options, ct);

        acquired.Success.ShouldBeTrue();
        acquired.ReentrantCount.ShouldBe(1);

        // Asking again as the same owner is a re-entry rather than a refusal, which is the server
        // side of the count the high-level lock keeps locally.
        NacosLockResult reentered = await client.Lock.AcquireAsync(key, options, ct);

        reentered.Success.ShouldBeTrue();
        reentered.ReentrantCount.ShouldBe(2);

        // One release of two leaves the lock held, so a different owner is still refused.
        (await client.Lock.ReleaseAsync(key, Owner, ct)).Success.ShouldBeTrue();

        NacosLockResult refused = await client.Lock.AcquireAsync(
            key, new NacosLockAcquireOptions { LeaseDuration = LongLease }, ct);

        refused.Success.ShouldBeFalse();

        // The second release empties the count and frees the lock.
        (await client.Lock.ReleaseAsync(key, Owner, ct)).Success.ShouldBeTrue();

        NacosLockResult taken = await client.Lock.AcquireAsync(
            key, new NacosLockAcquireOptions { LeaseDuration = LongLease }, ct);

        taken.Success.ShouldBeTrue();
        await client.Lock.ReleaseAsync(key, ct: ct);
    }

    [Fact]
    public async Task WaitQueue_HandsTheLockToTheWaiterWhenTheHolderReleases()
    {
        await using NacosContainer container = await NacosContainer.StartAsync(NacosIntegrationImages.LockImage);
        await using NacosClientFactory holder = CreateClient(container);
        await using NacosClientFactory waiter = CreateClient(container);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        var key = new NacosLockKey(NacosHttp.UniqueId("lock-wait"));

        NacosLockResult acquired = await holder.Lock.AcquireAsync(
            key, new NacosLockAcquireOptions { LeaseDuration = LongLease }, ct);

        acquired.Success.ShouldBeTrue();

        // The waiter joins the server's queue and is answered when the lock is handed over, so the
        // release has to happen while it waits. That hand-over is the part a scripted server cannot
        // reproduce: it is a notification pushed by the server, not a reply to a request.
        Task<NacosLockResult> queued = waiter.Lock.AcquireAsync(
            key,
            new NacosLockAcquireOptions
            {
                LeaseDuration = LongLease,
                WaitTime = TimeSpan.FromSeconds(30),
            },
            ct);

        // Time for the waiter to reach the server, so that what is observed is the hand-over rather
        // than an acquisition of a lock that happened to be free.
        await Task.Delay(TimeSpan.FromSeconds(2), ct);

        NacosLockResult released = await holder.Lock.ReleaseAsync(key, ct: ct);

        released.Success.ShouldBeTrue();

        NacosLockResult handed = await queued;

        handed.Success.ShouldBeTrue();
        handed.ReentrantCount.ShouldBe(1);

        await waiter.Lock.ReleaseAsync(key, ct: ct);
    }

    [Fact]
    public async Task HighLevelLock_RenewsItsLeaseAndReleasesOnDispose()
    {
        await using NacosContainer container = await NacosContainer.StartAsync(NacosIntegrationImages.LockImage);
        await using NacosClientFactory holder = CreateClient(container);
        await using NacosClientFactory other = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;
        var key = new NacosLockKey(NacosHttp.UniqueId("lock-lease"));
        var options = new NacosLockAcquireOptions { LeaseDuration = LongLease };

        // A six second lease renewed every two: a holder that is not renewed loses the lock after
        // six seconds, which is well inside the hold below.
        NacosLock @lock = holder.Lock.GetLock(
            key, new NacosLockOptions { LeaseDuration = TimeSpan.FromSeconds(6) });

        await using (NacosLockLease lease = await @lock.AcquireAsync(ct))
        {
            lease.IsHeld.ShouldBeTrue();

            await Task.Delay(TimeSpan.FromSeconds(15), ct);

            // No renewal may have failed, and the server must still refuse the other holder: the
            // second assertion is what proves the renewals reached it rather than only being sent.
            lease.IsHeld.ShouldBeTrue();
            (await other.Lock.AcquireAsync(key, options, ct)).Success.ShouldBeFalse();
        }

        // Disposing the last lease released the lock, so it is free at once rather than in a lease.
        NacosLockResult retaken = await other.Lock.AcquireAsync(key, options, ct);

        retaken.Success.ShouldBeTrue();
        await other.Lock.ReleaseAsync(key, ct: ct);
    }

    private static NacosClientFactory CreateClient(NacosContainer container) =>
        NacosClientFactory.Create(
            new NacosOptions
            {
                ServerAddresses = [container.HttpAddress],
                ConnectionTimeout = TimeSpan.FromSeconds(15),
                Reconnect = new NacosReconnectOptions
                {
                    InitialDelay = TimeSpan.FromMilliseconds(500),
                    MaxDelay = TimeSpan.FromSeconds(5),
                    JitterFactor = 0.1,
                },
            },
            httpClient: null,
            channelFactory: null,
            timeProvider: TimeProvider.System,
            loggerFactory: NullLoggerFactory.Instance);
}
