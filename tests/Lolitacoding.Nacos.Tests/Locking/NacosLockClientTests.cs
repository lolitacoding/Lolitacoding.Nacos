using Lolitacoding.Nacos.Locking;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Generated;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Tests.Transport;

namespace Lolitacoding.Nacos.Tests.Locking;

/// <summary>
/// Verifies the lock client's wire contract and its wait behaviour against a scripted lock service.
/// </summary>
/// <remarks>
/// The contract worth pinning down is the one the server reads literally: the operation names, the
/// owner every request must carry, and the fact that an unsuccessful acquisition is a normal reply
/// rather than an error. The wait is the only part with real logic in it, so it is exercised both
/// when a notification arrives and when none does.
/// </remarks>
public sealed class NacosLockClientTests
{
    [Fact]
    public async Task AcquireAsync_OpensTheLockConnectionOnlyWhenFirstUsed()
    {
        // An application that never takes a lock should not carry a connection and a heartbeat for
        // the module it does not use.
        var server = new LockTestServer();

        await using NacosLockClient client = server.CreateClient();

        server.Factory.RequestedAddresses.ShouldBeEmpty();
        server.Stream.Written.ShouldBeEmpty();

        server.OnOperation = _ => LockTest.Success(1);

        await client.AcquireAsync(new NacosLockKey("order-42"), ct: TestContext.Current.CancellationToken);

        server.Factory.RequestedAddresses.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AcquireAsync_SendsTheAcquireOnAConnectionLabelledForTheLockModule()
    {
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        server.OnOperation = _ => LockTest.Success(1);

        await client.AcquireAsync(new NacosLockKey("order-42"), ct: TestContext.Current.CancellationToken);

        ConnectionSetupRequest setup =
            PayloadCodec.Decode<ConnectionSetupRequest>(server.Stream.Written[0])!;

        setup.Labels![ProtocolHeaders.Module].ShouldBe("lock");
    }

    [Fact]
    public async Task AcquireAsync_SendsTheOperationTheServerReadsLiterally()
    {
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["holder"] = "nightly-job",
        };

        server.OnOperation = _ => LockTest.Success(1);

        await client.AcquireAsync(
            new NacosLockKey("order-42", NacosLockTypes.Reentrant),
            new NacosLockAcquireOptions
            {
                Owner = "owner-1",
                LeaseDuration = TimeSpan.FromSeconds(45),
                Parameters = parameters,
            },
            TestContext.Current.CancellationToken);

        LockOperationRequest request = server.Operations[0];

        request.LockOperationEnum.ShouldBe("ACQUIRE");
        request.LockInstance!.Key.ShouldBe("order-42");
        request.LockInstance.LockType.ShouldBe(NacosLockTypes.Reentrant);
        request.LockInstance.Owner.ShouldBe("owner-1");

        // The lease is a duration the server measures from now, in milliseconds.
        request.LockInstance.ExpiredTime.ShouldBe(45000);

        // No wait was asked for, so the protocol's "do not queue" value is sent.
        request.LockInstance.WaitTime.ShouldBe(-1);
        request.LockInstance.WaiterRetry.ShouldBeFalse();
        request.LockInstance.Params!["holder"].ShouldBe("nightly-job");
    }

    [Fact]
    public async Task AcquireAsync_NamesItselfAsOwnerWhenTheCallerDoesNot()
    {
        // The server would otherwise default the owner to the connection, and every wait would then
        // address the connection rather than the caller that is still waiting.
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        server.OnOperation = _ => LockTest.Success(1);

        await client.AcquireAsync(new NacosLockKey("order-42"), ct: TestContext.Current.CancellationToken);

        server.Operations[0].LockInstance!.Owner.ShouldNotBeNullOrWhiteSpace();
        server.Operations[0].LockInstance!.ExpiredTime.ShouldBe(-1);
    }

    [Fact]
    public async Task AcquireAsync_ReportsTheReentrantCount()
    {
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        server.OnOperation = _ => LockTest.Success(3);

        NacosLockResult result =
            await client.AcquireAsync(new NacosLockKey("order-42"), ct: TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.ReentrantCount.ShouldBe(3);
        result.Waiting.ShouldBeFalse();
    }

    [Fact]
    public async Task AcquireAsync_ReportsAHeldLockAsAnUnsuccessfulResult()
    {
        // Being refused is an ordinary answer to asking for a lock, not a failure of the call, so
        // it must not be an exception.
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        server.OnOperation = _ => new LockOperationResponse
        {
            Result = false,
            LockResult = new LockResult
            {
                Success = false,
                WaitPosition = -1,
                ErrorMessage = "Lock is held by another owner",
            },
        };

        NacosLockResult result =
            await client.AcquireAsync(new NacosLockKey("order-42"), ct: TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldBe("Lock is held by another owner");
    }

    [Fact]
    public async Task AcquireAsync_ThrowsWhenTheServerReportsATransportFailure()
    {
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        server.OnOperation = _ => new LockOperationResponse
        {
            ResultCode = NacosResponse.FailureCode,
            ErrorCode = 500,
            Message = "lock service exploded",
        };

        NacosServerException ex = await Should.ThrowAsync<NacosServerException>(
            () => client.AcquireAsync(
                new NacosLockKey("order-42"), ct: TestContext.Current.CancellationToken));

        ex.ErrorCode.ShouldBe(500);
        ex.Message.ShouldBe("lock service exploded");
    }

    [Fact]
    public async Task AcquireAsync_ThrowsWhenTheServerAdvertisesNoLockSupport()
    {
        var server = new LockTestServer(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["lock"] = false,
        });

        await using NacosLockClient client = server.CreateClient();

        server.OnOperation = _ => LockTest.Success(1);

        NacosServerException ex = await Should.ThrowAsync<NacosServerException>(
            () => client.AcquireAsync(
                new NacosLockKey("order-42"), ct: TestContext.Current.CancellationToken));

        ex.ErrorCode.ShouldBe(501);
        server.Operations.ShouldBeEmpty();
    }

    [Fact]
    public async Task AcquireAsync_ProceedsWhenTheServerIsSilentAboutLockSupport()
    {
        // An ability table that does not mention the lock means the server did not say, not that it
        // cannot serve it: the request is the only way to find out.
        var server = new LockTestServer(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["fuzzyWatch"] = false,
        });

        await using NacosLockClient client = server.CreateClient();

        server.OnOperation = _ => LockTest.Success(1);

        NacosLockResult result =
            await client.AcquireAsync(new NacosLockKey("order-42"), ct: TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task AcquireAsync_RejectsNegativeWaitAndNonPositiveLease()
    {
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        server.OnOperation = _ => LockTest.Success(1);
        var key = new NacosLockKey("order-42");

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => client.AcquireAsync(
                key,
                new NacosLockAcquireOptions { WaitTime = TimeSpan.FromMilliseconds(-1) },
                TestContext.Current.CancellationToken));

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => client.AcquireAsync(
                key,
                new NacosLockAcquireOptions { LeaseDuration = TimeSpan.Zero },
                TestContext.Current.CancellationToken));

        server.Operations.ShouldBeEmpty();
    }

    [Fact]
    public async Task AcquireAsync_RetriesAsASecondAttemptAndSucceedsAfterANotification()
    {
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        int attempts = 0;

        server.OnOperation = request =>
        {
            request.LockOperationEnum.ShouldBe("ACQUIRE");
            return ++attempts == 1 ? LockTest.Waiting(0) : LockTest.Success(1);
        };

        var key = new NacosLockKey("order-42");

        Task<NacosLockResult> acquire = client.AcquireAsync(
            key,
            new NacosLockAcquireOptions { WaitTime = TimeSpan.FromSeconds(30) },
            TestContext.Current.CancellationToken);

        // The waiter is registered before the first attempt is sent, so once the attempt has been
        // recorded a notification pushed now is guaranteed to find it.
        await LockTest.WaitForAsync(() => server.Operations.Count >= 1, TestContext.Current.CancellationToken);

        string owner = server.Operations[0].LockInstance!.Owner!;

        server.Stream.Push(TestPayload.ForRequest(new LockNotificationRequest
        {
            LockKey = key.Name,
            LockType = key.LockType,
            Owner = owner,
            NotificationType = "AVAILABLE",
            RequestId = "notify-1",
        }));

        NacosLockResult result = await acquire.WaitAsync(
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();

        // The retry must announce itself, or the server would grant the lock to the caller ahead of
        // this one in its queue.
        server.Operations.Count.ShouldBe(2);
        server.Operations[1].LockInstance!.WaiterRetry.ShouldBeTrue();

        // The acknowledgement is what stops the server retrying the notification.
        await LockTest.WaitForAsync(
            () => server.Stream.Written.Any(
                payload => PayloadCodec.ReadTypeName(payload) == "LockNotificationResponse"),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AcquireAsync_WithdrawsTheQueueEntryWhenTheWaitRunsOut()
    {
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        server.OnOperation = request => request.LockOperationEnum switch
        {
            "ACQUIRE" => LockTest.Waiting(0),
            "CANCEL_WAIT" => LockTest.Success(0),
            _ => throw new InvalidOperationException(request.LockOperationEnum),
        };

        NacosLockResult result = await client.AcquireAsync(
            new NacosLockKey("order-42"),
            new NacosLockAcquireOptions { WaitTime = TimeSpan.FromMilliseconds(200) },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull().ShouldContain("wait time");

        // Leaving the entry behind would have the server offer the lock to a caller that gave up.
        server.Operations[^1].LockOperationEnum.ShouldBe("CANCEL_WAIT");
    }

    [Fact]
    public async Task AcquireAsync_StopsWhenTheServerDropsTheWaiter()
    {
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        server.OnOperation = request => request.LockOperationEnum switch
        {
            "ACQUIRE" => LockTest.Waiting(2),
            "CANCEL_WAIT" => LockTest.Success(0),
            _ => throw new InvalidOperationException(request.LockOperationEnum),
        };

        var key = new NacosLockKey("order-42");

        Task<NacosLockResult> acquire = client.AcquireAsync(
            key,
            new NacosLockAcquireOptions { WaitTime = TimeSpan.FromSeconds(30) },
            TestContext.Current.CancellationToken);

        await LockTest.WaitForAsync(() => server.Operations.Count >= 1, TestContext.Current.CancellationToken);

        server.Stream.Push(TestPayload.ForRequest(new LockNotificationRequest
        {
            LockKey = key.Name,
            Owner = server.Operations[0].LockInstance!.Owner,
            NotificationType = "TIMEOUT",
        }));

        NacosLockResult result = await acquire.WaitAsync(
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull().ShouldContain("gave up");

        // The local waiter is cleared as well, so a notification that never comes cannot be waited
        // on, and the server is told the entry is not wanted even though it has already dropped it.
        server.Operations[^1].LockOperationEnum.ShouldBe("CANCEL_WAIT");
    }

    [Fact]
    public async Task ReleaseAsync_SendsTheReleaseForTheNamedOwner()
    {
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        server.OnOperation = _ => LockTest.Success(0);

        NacosLockResult result = await client.ReleaseAsync(
            new NacosLockKey("order-42"),
            "owner-1",
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.ReentrantCount.ShouldBe(0);
        server.Operations[0].LockOperationEnum.ShouldBe("RELEASE");
        server.Operations[0].LockInstance!.Owner.ShouldBe("owner-1");
        server.Operations[0].LockInstance!.Key.ShouldBe("order-42");
    }

    [Fact]
    public async Task RenewAsync_SendsTheRenewWithTheNewLease()
    {
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        // A renewal answers with a bare boolean, which is why the client must read that form too.
        server.OnOperation = _ => new LockOperationResponse { Result = true };

        NacosLockResult result = await client.RenewAsync(
            new NacosLockKey("order-42"),
            "owner-1",
            TimeSpan.FromSeconds(20),
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        server.Operations[0].LockOperationEnum.ShouldBe("RENEW");
        server.Operations[0].LockInstance!.ExpiredTime.ShouldBe(20000);
    }

    [Fact]
    public async Task RenewAsync_FailsWhenTheServerSaysTheLeaseIsGone()
    {
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        server.OnOperation = _ => new LockOperationResponse { Result = false };

        NacosLockResult result = await client.RenewAsync(
            new NacosLockKey("order-42"), ct: TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
    }

    [Fact]
    public async Task CancelWaitAsync_SendsTheCancellationForTheNamedOwner()
    {
        var server = new LockTestServer();
        await using NacosLockClient client = server.CreateClient();

        server.OnOperation = _ => LockTest.Success(0);

        NacosLockResult result = await client.CancelWaitAsync(
            new NacosLockKey("order-42"),
            "owner-1",
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        server.Operations[0].LockOperationEnum.ShouldBe("CANCEL_WAIT");
        server.Operations[0].LockInstance!.Owner.ShouldBe("owner-1");
    }

    [Fact]
    public async Task Operations_ThrowAfterTheClientIsDisposed()
    {
        var server = new LockTestServer();
        NacosLockClient client = server.CreateClient();

        server.OnOperation = _ => LockTest.Success(1);

        await client.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(
            () => client.AcquireAsync(
                new NacosLockKey("order-42"), ct: TestContext.Current.CancellationToken));
    }

}
