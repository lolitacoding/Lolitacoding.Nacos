using Lolitacoding.Nacos.Locking;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Tests.Transport;
using Microsoft.Extensions.Time.Testing;

namespace Lolitacoding.Nacos.Tests.Locking;

/// <summary>
/// Verifies the high-level lock: waiting, re-entrancy, renewals and release.
/// </summary>
/// <remarks>
/// The lock is a lease that the client keeps alive, so most of what is worth pinning down is what the
/// client does on its own — whether a nested acquisition asks the server again, how often a lease is
/// renewed, and what a holder is told when a renewal fails. A fake clock drives the renewals; the
/// wait itself is exercised against real short deadlines, as in the low-level tests.
/// </remarks>
public sealed class NacosLockTests
{
    private static readonly NacosLockKey OrderKey = new("order-42", NacosLockTypes.Reentrant);

    [Fact]
    public async Task AcquireAsync_AsksTheServerForTheLeaseTheLockWasGiven()
    {
        // The lock holds the lease for every acquisition made through it, so a caller that named a
        // lease once does not have to name it again on every acquisition.
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        server.OnOperation = _ => LockTest.Success(1);

        NacosLock @lock = client.GetLock(OrderKey, new NacosLockOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(45),
        });

        await using NacosLockLease lease = await @lock.AcquireAsync(TestContext.Current.CancellationToken);

        LockOperationRequest request = server.Operations[0];

        request.LockOperationEnum.ShouldBe("ACQUIRE");
        request.LockInstance!.Key.ShouldBe(OrderKey.Name);
        request.LockInstance.LockType.ShouldBe(OrderKey.LockType);
        request.LockInstance.ExpiredTime.ShouldBe(45000);

        // An acquisition that waits for as long as it takes queues for five minutes at a time,
        // which is what Nacos' own client asks for.
        request.LockInstance.WaitTime.ShouldBe(300000);
        request.LockInstance.WaiterRetry.ShouldBeFalse();

        request.LockInstance.Owner.ShouldBe(lease.Owner);
        lease.IsHeld.ShouldBeTrue();
        lease.ReentrancyCount.ShouldBe(1);
    }

    [Fact]
    public async Task AcquireAsync_ReentersInATaskStartedFromTheHoldingFlow()
    {
        // The other side of flow ownership, and the caveat that goes with it: work started from the
        // holding flow is part of that flow, so it re-enters rather than waiting. That is right for
        // a helper called from the critical section and wrong for work that has to exclude it, which
        // is why the independent form is a flow of its own.
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        server.OnOperation = _ => LockTest.Success(1);

        NacosLock @lock = client.GetLock(OrderKey);
        CancellationToken ct = TestContext.Current.CancellationToken;

        await using NacosLockLease outer = await @lock.AcquireAsync(ct);

        Task<NacosLockLease> nested = Task.Run(() => @lock.AcquireAsync(ct), ct);

        await using NacosLockLease inner = await nested.WaitAsync(TimeSpan.FromSeconds(10), ct);

        inner.ReentrancyCount.ShouldBe(2);
        inner.Owner.ShouldBe(outer.Owner);
        server.Operations.Count(request => request.LockOperationEnum == "ACQUIRE").ShouldBe(1);
    }

    [Fact]
    public async Task AcquireAsync_GivesEachFlowItsOwnOwner()
    {
        // Two concurrent acquisitions that inherit no holder are two holders: each mints an owner of
        // its own and the server arbitrates between them. That is the mutual exclusion a distributed
        // lock exists for; re-entering only applies to a flow that already holds the lock.
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        server.OnOperation = _ => LockTest.Success(1);

        NacosLock @lock = client.GetLock(OrderKey);
        CancellationToken ct = TestContext.Current.CancellationToken;

        Task<NacosLockLease>[] attempts =
        [
            Task.Run(() => @lock.AcquireAsync(ct), ct),
            Task.Run(() => @lock.AcquireAsync(ct), ct),
        ];

        NacosLockLease[] leases = await Task
            .WhenAll(attempts)
            .WaitAsync(TimeSpan.FromSeconds(10), ct);

        foreach (NacosLockLease lease in leases)
        {
            await lease.DisposeAsync();
        }

        server.Operations.Count(request => request.LockOperationEnum == "ACQUIRE").ShouldBe(2);
        server.Operations[0].LockInstance!.Owner.ShouldNotBe(server.Operations[1].LockInstance!.Owner);
    }

    [Fact]
    public async Task AcquireAsync_GivesAnotherFlowItsOwnOwnerWhenTheFlowIsSuppressed()
    {
        // Ownership follows the flow, so work that must not inherit a holder has to start without one.
        // Suppressing the flow is how a caller does that in process; the low-level client with an
        // explicit owner is the other way.
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        server.OnOperation = _ => LockTest.Success(1);

        NacosLock @lock = client.GetLock(OrderKey);
        CancellationToken ct = TestContext.Current.CancellationToken;

        await using NacosLockLease first = await @lock.AcquireAsync(ct);

        Task<NacosLockLease> other;

        using (ExecutionContext.SuppressFlow())
        {
            other = Task.Run(() => @lock.AcquireAsync(ct), ct);
        }

        await using NacosLockLease second = await other.WaitAsync(TimeSpan.FromSeconds(10), ct);

        second.Owner.ShouldNotBe(first.Owner);
        second.ReentrancyCount.ShouldBe(1);
        server.Operations.Count(request => request.LockOperationEnum == "ACQUIRE").ShouldBe(2);
    }

    [Fact]
    public async Task AcquireAsync_ReentersWithoutAskingTheServerAndWithoutReleasingEarly()
    {
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        server.OnOperation = _ => LockTest.Success(1);

        NacosLock @lock = client.GetLock(OrderKey);
        CancellationToken ct = TestContext.Current.CancellationToken;

        await using NacosLockLease outer = await @lock.AcquireAsync(ct);
        await using NacosLockLease inner = await @lock.AcquireAsync(ct);

        inner.ReentrancyCount.ShouldBe(2);

        // The lock is one acquisition as far as the server is concerned, so the inner release is
        // local and the lock stays held.
        await inner.DisposeAsync();

        inner.IsHeld.ShouldBeFalse();
        outer.IsHeld.ShouldBeTrue();
        server.Operations.Count(request => request.LockOperationEnum == "RELEASE").ShouldBe(0);

        await outer.DisposeAsync();

        outer.IsHeld.ShouldBeFalse();
        server.Operations.Count(request => request.LockOperationEnum == "RELEASE").ShouldBe(1);
        server.Operations[^1].LockInstance!.Owner.ShouldBe(outer.Owner);
    }

    [Fact]
    public async Task AcquireAsync_RefusesToReenterALockThatDoesNotAllowIt()
    {
        // The server would either refuse a second acquisition by the owner that already holds a
        // non-reentrant lock or queue it behind that owner. Refusing it here is both honest and
        // faster, and it names the lock type in the failure.
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        server.OnOperation = _ => LockTest.Success(1);

        NacosLock @lock = client.GetLock(new NacosLockKey("order-42", NacosLockTypes.NonReentrant));
        CancellationToken ct = TestContext.Current.CancellationToken;

        await using NacosLockLease lease = await @lock.AcquireAsync(ct);

        LockRecursionException ex = await Should.ThrowAsync<LockRecursionException>(
            () => @lock.AcquireAsync(ct));

        ex.Message.ShouldContain("order-42");
        ex.Message.ShouldContain(NacosLockTypes.NonReentrant);
        server.Operations.Count.ShouldBe(1);
    }

    [Fact]
    public async Task TryAcquireAsync_DoesNotQueueAndDoesNotWaitWhenNoWaitIsAskedFor()
    {
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        server.OnOperation = _ => LockTest.Waiting(0);

        NacosLockLease? lease = await client
            .GetLock(OrderKey)
            .TryAcquireAsync(TimeSpan.Zero, TestContext.Current.CancellationToken);

        lease.ShouldBeNull();

        // One request: no queue entry to withdraw, because none was made.
        server.Operations.Count.ShouldBe(1);
        server.Operations[0].LockInstance!.WaitTime.ShouldBe(-1);
    }

    [Fact]
    public async Task TryAcquireAsync_QueuesForTheWaitAndTakesTheLockWhenItIsOffered()
    {
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        int attempts = 0;

        server.OnOperation = request => request.LockOperationEnum switch
        {
            "ACQUIRE" => ++attempts == 1 ? LockTest.Waiting(0) : LockTest.Success(1),
            "CANCEL_WAIT" => LockTest.Success(0),
            _ => throw new InvalidOperationException(request.LockOperationEnum),
        };

        NacosLock @lock = client.GetLock(OrderKey);
        CancellationToken ct = TestContext.Current.CancellationToken;

        Task<NacosLockLease?> attempt = @lock.TryAcquireAsync(TimeSpan.FromSeconds(30), ct);

        // The waiter is registered before the first attempt is sent, so once the attempt has been
        // recorded a notification pushed now is guaranteed to find it.
        await LockTest.WaitForAsync(() => server.Operations.Count >= 1, ct);

        server.Stream.Push(TestPayload.ForRequest(new LockNotificationRequest
        {
            LockKey = OrderKey.Name,
            LockType = OrderKey.LockType,
            Owner = server.Operations[0].LockInstance!.Owner,
            NotificationType = "AVAILABLE",
        }));

        await using NacosLockLease? lease = await attempt.WaitAsync(TimeSpan.FromSeconds(10), ct);

        lease.ShouldNotBeNull();
        lease.IsHeld.ShouldBeTrue();
        lease.Owner.ShouldBe(server.Operations[0].LockInstance!.Owner);

        server.Operations.Count.ShouldBe(2);
        server.Operations[1].LockInstance!.WaiterRetry.ShouldBeTrue();
        server.Operations[1].LockInstance!.WaitTime.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task TryAcquireAsync_ReturnsNullAndWithdrawsTheWaiterWhenTheWaitRunsOut()
    {
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        server.OnOperation = request => request.LockOperationEnum switch
        {
            "ACQUIRE" => LockTest.Waiting(0),
            "CANCEL_WAIT" => LockTest.Success(0),
            _ => throw new InvalidOperationException(request.LockOperationEnum),
        };

        NacosLockLease? lease = await client
            .GetLock(OrderKey)
            .TryAcquireAsync(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);

        lease.ShouldBeNull();

        server.Operations[0].LockInstance!.WaitTime.ShouldBeGreaterThan(0);
        server.Operations[0].LockInstance!.WaitTime.ShouldBeLessThanOrEqualTo(200);

        // Leaving the entry behind would have the server offer the lock to a caller that gave up.
        server.Operations[^1].LockOperationEnum.ShouldBe("CANCEL_WAIT");
    }

    [Fact]
    public async Task TryAcquireAsync_RejectsANegativeWait()
    {
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        server.OnOperation = _ => LockTest.Success(1);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => client
                .GetLock(OrderKey)
                .TryAcquireAsync(TimeSpan.FromSeconds(-1), TestContext.Current.CancellationToken));

        server.Operations.ShouldBeEmpty();
    }

    [Fact]
    public async Task AcquireAsync_RequeuesWhenTheServerDropsTheWaiter()
    {
        // An acquisition that waits for as long as it takes has no deadline of its own, so a queue
        // entry the server gave up is re-queued rather than reported as a failure.
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        int attempts = 0;

        server.OnOperation = request => request.LockOperationEnum switch
        {
            "ACQUIRE" => ++attempts == 1 ? LockTest.Waiting(0) : LockTest.Success(1),
            "CANCEL_WAIT" => LockTest.Success(0),
            _ => throw new InvalidOperationException(request.LockOperationEnum),
        };

        CancellationToken ct = TestContext.Current.CancellationToken;
        Task<NacosLockLease> acquire = client.GetLock(OrderKey).AcquireAsync(ct);

        await LockTest.WaitForAsync(() => server.Operations.Count >= 1, ct);

        server.Stream.Push(TestPayload.ForRequest(new LockNotificationRequest
        {
            LockKey = OrderKey.Name,
            Owner = server.Operations[0].LockInstance!.Owner,
            NotificationType = "TIMEOUT",
        }));

        await using NacosLockLease lease = await acquire.WaitAsync(TimeSpan.FromSeconds(10), ct);

        lease.IsHeld.ShouldBeTrue();
        server.Operations[^1].LockOperationEnum.ShouldBe("ACQUIRE");

        // The re-queue is a fresh acquisition rather than a retry: the attempt that was dropped
        // withdrew its queue entry on the way out, so there is nothing left to retry and the server
        // is asked for the lock as a new caller would.
        server.Operations[^1].LockInstance!.WaiterRetry.ShouldBeFalse();
        server.Operations[^1].LockInstance!.WaitTime.ShouldBe(300000);
    }

    [Fact]
    public async Task DisposeAsync_ReleasesOnceHoweverOftenItIsCalled()
    {
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        server.OnOperation = _ => LockTest.Success(0);

        NacosLockLease lease = await client
            .GetLock(OrderKey)
            .AcquireAsync(TestContext.Current.CancellationToken);

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        server.Operations.Count(request => request.LockOperationEnum == "RELEASE").ShouldBe(1);
        lease.IsHeld.ShouldBeFalse();
        lease.Ended.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task DisposeAsync_ReportsNothingWhenTheServerRefusesTheRelease()
    {
        // The lock is a lease, so a release the server refuses — the lease already expired, say —
        // costs another holder at most the rest of the lease. Throwing that out of a dispose would
        // mask whatever ended the scope instead.
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        server.OnOperation = request => request.LockOperationEnum switch
        {
            "ACQUIRE" => LockTest.Success(1),
            "RELEASE" => new LockOperationResponse
            {
                Result = false,
                LockResult = new LockResult
                {
                    Success = false,
                    WaitPosition = -1,
                    ErrorMessage = "Lock does not exist or already expired",
                },
            },
            _ => throw new InvalidOperationException(request.LockOperationEnum),
        };

        NacosLockLease lease = await client
            .GetLock(OrderKey)
            .AcquireAsync(TestContext.Current.CancellationToken);

        await Should.NotThrowAsync(() => lease.DisposeAsync().AsTask());

        lease.IsHeld.ShouldBeFalse();
        lease.Ended.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task AcquireAsync_ThrowsWhenTheClientWasDisposed()
    {
        var server = new LockTestServer();
        NacosLockClient client = server.CreateClient();

        server.OnOperation = _ => LockTest.Success(1);

        NacosLock @lock = client.GetLock(OrderKey);

        await client.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(
            () => @lock.AcquireAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Lease_IsGivenUpWhenTheClientIsDisposed()
    {
        // The connection the lock was held over is gone, so the server counts the lock as free. The
        // holder is told rather than left believing it still holds something.
        var server = new LockTestServer();
        NacosLockClient client = server.CreateClient();

        server.OnOperation = _ => LockTest.Success(1);

        NacosLockLease lease = await client
            .GetLock(OrderKey)
            .AcquireAsync(TestContext.Current.CancellationToken);

        await client.DisposeAsync();

        lease.IsHeld.ShouldBeFalse();
        lease.ReentrancyCount.ShouldBe(0);
        lease.Ended.IsCompleted.ShouldBeTrue();

        // Giving the lease up afterwards is a no-op: there is nothing left to release.
        await lease.DisposeAsync();
        server.Operations.Count(request => request.LockOperationEnum == "RELEASE").ShouldBe(0);
    }

    [Fact]
    public async Task Lease_IsRenewedThricePerLeaseAtMost()
    {
        var timeProvider = new FakeTimeProvider();
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient(timeProvider);

        server.OnOperation = request => request.LockOperationEnum switch
        {
            "ACQUIRE" => LockTest.Success(1),
            "RENEW" => new LockOperationResponse { Result = true },
            _ => throw new InvalidOperationException(request.LockOperationEnum),
        };

        await using NacosLockLease lease = await client
            .GetLock(OrderKey, new NacosLockOptions { LeaseDuration = TimeSpan.FromSeconds(15) })
            .AcquireAsync(TestContext.Current.CancellationToken);

        CancellationToken ct = TestContext.Current.CancellationToken;

        // Fifteen seconds is renewed every five, so the lease survives a renewal that is late.
        await AdvanceUntilAsync(timeProvider, () => Renewals(server) >= 1, ct);

        LockOperationRequest first = server.Operations.Last(
            request => request.LockOperationEnum == "RENEW");

        first.LockInstance!.Owner.ShouldBe(lease.Owner);
        first.LockInstance.ExpiredTime.ShouldBe(15000);

        await AdvanceUntilAsync(timeProvider, () => Renewals(server) >= 2, ct);

        server.Operations.Count(request => request.LockOperationEnum == "RENEW").ShouldBe(2);
    }

    [Fact]
    public async Task Lease_IsGivenUpWhenTheRenewalIsRefused()
    {
        // A renewal the server refuses is indistinguishable from a holder that died, so the holder
        // is told the lock is no longer its own instead of being left with a lock the server has
        // already given away.
        var timeProvider = new FakeTimeProvider();
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient(timeProvider);

        server.OnOperation = request => request.LockOperationEnum switch
        {
            "ACQUIRE" => LockTest.Success(1),
            "RENEW" => new LockOperationResponse { Result = false },
            _ => throw new InvalidOperationException(request.LockOperationEnum),
        };

        NacosLockLease lease = await client
            .GetLock(OrderKey)
            .AcquireAsync(TestContext.Current.CancellationToken);

        CancellationToken ct = TestContext.Current.CancellationToken;

        await AdvanceUntilAsync(timeProvider, () => Renewals(server) >= 1, ct);
        await LockTest.WaitForAsync(() => lease.Ended.IsCompleted, ct);

        lease.IsHeld.ShouldBeFalse();
        lease.ReentrancyCount.ShouldBe(0);

        // The lock is given up once: there is nothing left to lose, so nothing is retried.
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(50, ct);
        Renewals(server).ShouldBe(1);
    }

    [Fact]
    public async Task Lease_StopsBeingRenewedWhenItIsReleased()
    {
        var timeProvider = new FakeTimeProvider();
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient(timeProvider);

        server.OnOperation = request => request.LockOperationEnum switch
        {
            "ACQUIRE" => LockTest.Success(1),
            "RELEASE" => LockTest.Success(0),
            "RENEW" => new LockOperationResponse { Result = true },
            _ => throw new InvalidOperationException(request.LockOperationEnum),
        };

        NacosLockLease lease = await client
            .GetLock(OrderKey, new NacosLockOptions { LeaseDuration = TimeSpan.FromSeconds(15) })
            .AcquireAsync(TestContext.Current.CancellationToken);

        await lease.DisposeAsync();

        // The renewal loop is stopped by the release, so advancing well past the lease changes
        // nothing: a renewal for a lock nobody holds would be a request the server has no owner for.
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Renewals(server).ShouldBe(0);
    }

    /// <summary>Counts the renewals the client sent.</summary>
    /// <param name="server">The scripted server.</param>
    /// <returns>The number of renewals.</returns>
    private static int Renewals(LockTestServer server) =>
        server.Operations.Count(request => request.LockOperationEnum == "RENEW");

    /// <summary>
    /// Moves a fake clock forward in small steps until a condition holds.
    /// </summary>
    /// <param name="timeProvider">The fake clock.</param>
    /// <param name="condition">The condition to wait for.</param>
    /// <param name="ct">A token that ends the wait.</param>
    /// <returns>A task that completes once the condition holds.</returns>
    /// <remarks>
    /// The steps are shorter than the interval being driven, so no renewal is skipped, and the loop
    /// gives the renewal loop time to register its next wait between steps — which is what an
    /// ordinary single jump past the interval cannot guarantee.
    /// </remarks>
    private static async Task AdvanceUntilAsync(
        FakeTimeProvider timeProvider,
        Func<bool> condition,
        CancellationToken ct)
    {
        for (int i = 0; i < 400; i++)
        {
            if (condition())
            {
                return;
            }

            timeProvider.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(5, ct);
        }

        throw new TimeoutException("The expected condition was not observed.");
    }
}
