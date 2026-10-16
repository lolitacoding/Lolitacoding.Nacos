using Lolitacoding.Nacos.Auth;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Locking;

/// <summary>
/// Talks to the Nacos distributed lock service over the lock connection.
/// </summary>
/// <remarks>
/// <para>
/// Every operation is a single request and response, except an acquisition that joins the wait
/// queue: that one loops, re-asking whenever the server says the lock is still held and pausing
/// between attempts for either the server's notification or a short poll window, whichever comes
/// first. The poll window exists because a notification can be lost with the connection it was sent
/// on, and the loop must notice rather than block forever.
/// </para>
/// <para>
/// The queue is keyed on the lock and the owner, which is also the key a notification carries. The
/// waiter is therefore registered before the first request is sent: the server can grant the lock
/// and push the notification within the same round trip, and a registration made afterwards would
/// miss it.
/// </para>
/// <para>
/// The client owns its module's connection rather than the factory owning it, and unlike
/// configuration and naming that connection is opened on first use. A lock is usually taken by a
/// small part of a process, so an application that never takes one should not have to carry a
/// connection and a heartbeat for the module it does not use.
/// </para>
/// </remarks>
internal sealed class NacosLockClient : INacosLockClient
{
    /// <summary>The operation discriminator the server expects for each operation.</summary>
    private const string AcquireOperation = "ACQUIRE";
    private const string ReleaseOperation = "RELEASE";
    private const string RenewOperation = "RENEW";
    private const string CancelWaitOperation = "CANCEL_WAIT";

    /// <summary>The notification type the server sends when it drops a waiter.</summary>
    private const string TimeoutNotification = "TIMEOUT";

    /// <summary>The server ability that announces the distributed lock service.</summary>
    private const string LockAbility = "lock";

    /// <summary>The server error code for "this server does not implement that".</summary>
    private const int NotImplementedErrorCode = 501;

    /// <summary>The lease a request asks for when the caller names none.</summary>
    private const long ServerDefaultLeaseMilliseconds = -1;

    /// <summary>How long the wait loop pauses before re-asking, in milliseconds.</summary>
    private const int PollMilliseconds = 2000;

    /// <summary>The largest extra pause added to each poll, in milliseconds.</summary>
    /// <remarks>
    /// Without it, every waiter for one lock would wake together and re-ask together, and the server
    /// would have to sort out the resulting stampede on every lease boundary.
    /// </remarks>
    private const int PollJitterMilliseconds = 200;

    private readonly NacosLazyModuleConnection _connection;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NacosLockClient> _logger;

    /// <summary>The owner this client uses when a caller names none.</summary>
    private readonly string _owner = Guid.NewGuid().ToString("N");

    private readonly object _waitersGate = new();
    private readonly Dictionary<string, TaskCompletionSource<NacosLockNotificationType>> _waiters =
        new(StringComparer.Ordinal);

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosLockClient"/> class.
    /// </summary>
    /// <param name="options">The client options.</param>
    /// <param name="servers">The parsed server addresses.</param>
    /// <param name="channelFactory">The channel factory that opens the gRPC channels.</param>
    /// <param name="timeProvider">The clock used for the wait deadline.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="random">The random source used for reconnect jitter.</param>
    /// <param name="authProvider">Supplies the access token, or <see langword="null"/> for none.</param>
    public NacosLockClient(
        NacosOptions options,
        IReadOnlyList<NacosServerAddress> servers,
        INacosChannelFactory channelFactory,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        Random? random = null,
        INacosAuthProvider? authProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(channelFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        ILogger<NacosGrpcConnection> connectionLogger = loggerFactory.CreateLogger<NacosGrpcConnection>();

        _connection = new NacosLazyModuleConnection(async ct =>
        {
            var connection = new NacosGrpcConnection(
                options,
                NacosConnectionModule.Lock,
                servers,
                channelFactory,
                timeProvider,
                connectionLogger,
                random,
                authProvider);

            // The handler is attached before the handshake so a notification cannot arrive in the
            // window between the server accepting the connection and the client listening on it.
            connection.ServerRequestReceived += OnServerRequestAsync;

            await connection.ConnectAsync(ct).ConfigureAwait(false);
            return connection;
        });

        _timeProvider = timeProvider;
        _logger = loggerFactory.CreateLogger<NacosLockClient>();
    }

    /// <inheritdoc />
    public async Task<NacosLockResult> AcquireAsync(
        NacosLockKey key,
        NacosLockAcquireOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ObjectDisposedException.ThrowIf(_disposed, this);

        options ??= new NacosLockAcquireOptions();

        string owner = options.Owner ?? _owner;
        long wait = WaitMilliseconds(options.WaitTime);
        bool queue = wait > 0;

        NacosGrpcConnection connection = await _connection.GetAsync(ct).ConfigureAwait(false);
        EnsureSupported(connection, key);

        var instance = new LockInstance
        {
            Key = key.Name,
            LockType = key.LockType,
            Owner = owner,
            ExpiredTime = LeaseMilliseconds(options.LeaseDuration),
            WaitTime = queue ? wait : -1,
            Params = options.Parameters is null
                ? null
                : new Dictionary<string, string>(options.Parameters, StringComparer.Ordinal),
        };

        if (queue)
        {
            RegisterWaiter(key.Name, owner);
        }

        bool acquired = false;

        try
        {
            long deadline = queue
                ? _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() + wait
                : 0;

            bool firstAttempt = true;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // The server consults the queue rather than granting the lock outright once the
                // caller is a retry, so this is what turns the queue into a fair waiting line.
                instance.WaiterRetry = !firstAttempt;

                NacosLockResult result =
                    await OperateAsync(connection, AcquireOperation, instance, ct).ConfigureAwait(false);

                acquired = result.Success;

                if (acquired || !queue)
                {
                    return result;
                }

                firstAttempt = false;

                long remaining = deadline - _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                if (remaining <= 0)
                {
                    return NotAcquired(
                        key, "The lock did not become available within the requested wait time.");
                }

                long poll = Math.Min(
                    remaining, PollMilliseconds + Random.Shared.Next(PollJitterMilliseconds));

                NacosLockNotificationType? notification = await WaitForNotificationAsync(
                    key.Name, owner, TimeSpan.FromMilliseconds(poll), ct).ConfigureAwait(false);

                if (notification == NacosLockNotificationType.TimedOut)
                {
                    return NotAcquired(key, "The server gave up waiting for the lock.");
                }
            }
        }
        finally
        {
            if (queue)
            {
                // A granted acquisition has already left the server's queue, so only the local
                // waiter has to go; one that gave up is still queued and has to be withdrawn, or
                // the server would keep offering it a lock nobody is waiting for.
                DropWaiter(key.Name, owner);

                if (!acquired)
                {
                    await WithdrawWaiterAsync(connection, key, owner).ConfigureAwait(false);
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task<NacosLockResult> ReleaseAsync(
        NacosLockKey key,
        string? owner = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ObjectDisposedException.ThrowIf(_disposed, this);

        NacosGrpcConnection connection = await _connection.GetAsync(ct).ConfigureAwait(false);
        EnsureSupported(connection, key);

        var instance = new LockInstance
        {
            Key = key.Name,
            LockType = key.LockType,
            Owner = owner ?? _owner,
        };

        return await OperateAsync(connection, ReleaseOperation, instance, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<NacosLockResult> RenewAsync(
        NacosLockKey key,
        string? owner = null,
        TimeSpan? leaseDuration = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ObjectDisposedException.ThrowIf(_disposed, this);

        NacosGrpcConnection connection = await _connection.GetAsync(ct).ConfigureAwait(false);
        EnsureSupported(connection, key);

        var instance = new LockInstance
        {
            Key = key.Name,
            LockType = key.LockType,
            Owner = owner ?? _owner,
            ExpiredTime = LeaseMilliseconds(leaseDuration),
        };

        return await OperateAsync(connection, RenewOperation, instance, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<NacosLockResult> CancelWaitAsync(
        NacosLockKey key,
        string? owner = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string actualOwner = owner ?? _owner;

        NacosGrpcConnection connection = await _connection.GetAsync(ct).ConfigureAwait(false);
        EnsureSupported(connection, key);

        // The local wait ends either way, so it is dropped before the server is told. A server that
        // no longer knows the waiter then reports a failure that means exactly that, rather than the
        // caller mistaking a stale queue entry for a live one.
        DropWaiter(key.Name, actualOwner);

        var instance = new LockInstance
        {
            Key = key.Name,
            LockType = key.LockType,
            Owner = actualOwner,
        };

        return await OperateAsync(connection, CancelWaitOperation, instance, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        TaskCompletionSource<NacosLockNotificationType>[] waiters;

        lock (_waitersGate)
        {
            waiters = [.. _waiters.Values];
            _waiters.Clear();
        }

        foreach (TaskCompletionSource<NacosLockNotificationType> waiter in waiters)
        {
            // Nothing will complete a waiter once the client is gone, so they are failed rather than
            // left to run out their poll windows one after another.
            waiter.TrySetCanceled();
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Routes a push that arrived on the lock connection.
    /// </summary>
    /// <param name="request">The pushed request.</param>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>The acknowledgement the server expects, or <see langword="null"/> when none is due.</returns>
    /// <remarks>
    /// The server expects a reply for a notification, and replies to one itself, so the response is
    /// produced inline: there is no work to do that could hold the stream up.
    /// </remarks>
    private Task<NacosResponse?> OnServerRequestAsync(NacosRequest request, CancellationToken ct) =>
        request is LockNotificationRequest notification
            ? Task.FromResult<NacosResponse?>(HandleNotification(notification))
            : Task.FromResult<NacosResponse?>(null);

    /// <summary>
    /// Completes the waiter a lock notification is addressed to.
    /// </summary>
    /// <param name="notification">The notification the server pushed.</param>
    /// <returns>The acknowledgement the server expects.</returns>
    /// <remarks>
    /// A notification for a lock nobody is waiting for is normal rather than an error: it is what a
    /// server sends to a waiter that gave up in the interval before the entry was withdrawn, and to
    /// a client that has since reconnected on a new stream.
    /// </remarks>
    public NacosResponse HandleNotification(LockNotificationRequest notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (string.IsNullOrEmpty(notification.LockKey))
        {
            return new LockNotificationResponse();
        }

        TaskCompletionSource<NacosLockNotificationType>? waiter = null;

        lock (_waitersGate)
        {
            if (_waiters.TryGetValue(
                WaitKey(notification.LockKey, notification.Owner), out TaskCompletionSource<NacosLockNotificationType>? found))
            {
                waiter = found;
            }
        }

        waiter?.TrySetResult(NotificationType(notification));
        return new LockNotificationResponse();
    }

    /// <summary>
    /// Sends one lock operation and translates the reply.
    /// </summary>
    /// <param name="connection">The lock connection.</param>
    /// <param name="operation">The operation discriminator.</param>
    /// <param name="instance">The lock state to send.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="NacosServerException">The server answered with a failure result code.</exception>
    private static async Task<NacosLockResult> OperateAsync(
        NacosGrpcConnection connection,
        string operation,
        LockInstance instance,
        CancellationToken ct)
    {
        LockOperationResponse response = await connection.RequestAsync<LockOperationResponse>(
            new LockOperationRequest
            {
                LockInstance = instance,
                LockOperationEnum = operation,
            },
            ct).ConfigureAwait(false);

        return FromResponse(response);
    }

    /// <summary>
    /// Translates the server's reply into the public outcome.
    /// </summary>
    /// <param name="response">The reply.</param>
    /// <returns>The outcome.</returns>
    /// <remarks>
    /// The structured result is preferred because it carries the reentrant count and the queue
    /// position. A bare boolean is what a renewal returns, and what any operation returns from a
    /// server that predates the structured form; it says nothing about either, so the count is
    /// reported as zero rather than guessed at.
    /// </remarks>
    private static NacosLockResult FromResponse(LockOperationResponse response)
    {
        LockResult? structured = response.LockResult;

        if (structured is not null)
        {
            return new NacosLockResult(
                structured.Success,
                structured.ReentrantCount,
                structured.WaitPosition,
                structured.ErrorMessage);
        }

        bool success = response.Result == true;

        return new NacosLockResult(success, 0, -1, success ? null : response.Message);
    }

    /// <summary>
    /// Fails the acquisition when the server does not implement the lock service.
    /// </summary>
    /// <param name="connection">The lock connection.</param>
    /// <param name="key">The lock that was requested.</param>
    /// <exception cref="NacosServerException">The server advertises that it cannot serve locks.</exception>
    /// <remarks>
    /// Only an explicit rejection is treated as one. A server that says nothing about the ability
    /// may still serve the request, and letting the request fail on its own reports the truth more
    /// precisely than guessing from the absence of a field.
    /// </remarks>
    private static void EnsureSupported(NacosGrpcConnection connection, NacosLockKey key)
    {
        if (connection.ServerAbilities.TryGetValue(LockAbility, out bool supported) && !supported)
        {
            throw new NacosServerException(
                NotImplementedErrorCode,
                string.Concat(
                    "The connected Nacos server does not implement the distributed lock, so lock '",
                    key.Name,
                    "' cannot be requested."));
        }
    }

    /// <summary>
    /// Registers the waiter a notification for this lock and owner will complete.
    /// </summary>
    /// <param name="lockKey">The lock key.</param>
    /// <param name="owner">The owner.</param>
    private void RegisterWaiter(string lockKey, string owner)
    {
        string waitKey = WaitKey(lockKey, owner);
        var waiter = new TaskCompletionSource<NacosLockNotificationType>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource<NacosLockNotificationType>? replaced = null;

        lock (_waitersGate)
        {
            if (_waiters.TryGetValue(waitKey, out TaskCompletionSource<NacosLockNotificationType>? existing))
            {
                replaced = existing;
            }

            _waiters[waitKey] = waiter;
        }

        if (replaced is not null)
        {
            // Two acquisitions of one lock by one owner cannot both be the server's waiter, so the
            // earlier one is released as "available" to re-ask immediately instead of blocking until
            // its own poll window elapses.
            replaced.TrySetResult(NacosLockNotificationType.Available);
        }
    }

    /// <summary>
    /// Removes the waiter for a lock and owner without telling the server.
    /// </summary>
    /// <param name="lockKey">The lock key.</param>
    /// <param name="owner">The owner.</param>
    private void DropWaiter(string lockKey, string owner)
    {
        TaskCompletionSource<NacosLockNotificationType>? waiter = null;

        lock (_waitersGate)
        {
            if (_waiters.Remove(WaitKey(lockKey, owner), out TaskCompletionSource<NacosLockNotificationType>? found))
            {
                waiter = found;
            }
        }

        waiter?.TrySetCanceled();
    }

    /// <summary>
    /// Waits for a notification, or for the poll window to elapse.
    /// </summary>
    /// <param name="lockKey">The lock key.</param>
    /// <param name="owner">The owner.</param>
    /// <param name="timeout">How long to wait for a notification.</param>
    /// <param name="ct">A token that ends the wait.</param>
    /// <returns>The notification type, or <see langword="null"/> when none arrived in time.</returns>
    /// <remarks>
    /// <para>
    /// The waiter registered before the first attempt is reused rather than replaced, so a
    /// notification that arrives while the attempt is still in flight is neither lost nor mistaken
    /// for one belonging to a later round. If it already arrived, it is consumed here and its result
    /// returned, which is what lets the retry happen at once instead of at the end of the poll
    /// window.
    /// </para>
    /// <para>
    /// A waiter that was cancelled outright is replaced. That happens when the wait it belonged to
    /// was withdrawn by another caller, and reusing the cancelled waiter would end this wait too.
    /// </para>
    /// </remarks>
    private async Task<NacosLockNotificationType?> WaitForNotificationAsync(
        string lockKey,
        string owner,
        TimeSpan timeout,
        CancellationToken ct)
    {
        TaskCompletionSource<NacosLockNotificationType> waiter;
        string waitKey = WaitKey(lockKey, owner);

        lock (_waitersGate)
        {
            if (_waiters.TryGetValue(waitKey, out TaskCompletionSource<NacosLockNotificationType>? existing))
            {
                if (existing.Task.IsCompletedSuccessfully)
                {
                    _waiters.Remove(waitKey);
                    return existing.Task.Result;
                }

                if (existing.Task.IsCompleted)
                {
                    _waiters.Remove(waitKey);
                    existing = null;
                }
            }

            waiter = existing ?? new TaskCompletionSource<NacosLockNotificationType>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            _waiters[waitKey] = waiter;
        }

        try
        {
            return await waiter.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Tells the server to drop the queue entry for a waiter that gave up.
    /// </summary>
    /// <param name="connection">The lock connection.</param>
    /// <param name="key">The lock that was queued for.</param>
    /// <param name="owner">The owner that queued.</param>
    /// <returns>A task that completes once the attempt finished.</returns>
    /// <remarks>
    /// A failure here is not reported to the caller. The acquisition has already ended — usually
    /// because it was cancelled — so there is nothing left for the caller to do about it, and the
    /// server drops the entry when it expires.
    /// </remarks>
    private async Task WithdrawWaiterAsync(NacosGrpcConnection connection, NacosLockKey key, string owner)
    {
        try
        {
            var instance = new LockInstance
            {
                Key = key.Name,
                LockType = key.LockType,
                Owner = owner,
            };

            await OperateAsync(connection, CancelWaitOperation, instance, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            NacosLockLog.WithdrawWaiterFailed(_logger, key.Name, ex.Message);
        }
    }

    /// <summary>Builds the unsuccessful result reported when the wait ran out.</summary>
    /// <param name="key">The lock that was requested.</param>
    /// <param name="reason">Why the wait ended.</param>
    /// <returns>The outcome.</returns>
    private static NacosLockResult NotAcquired(NacosLockKey key, string reason) =>
        new(false, 0, -1, string.Concat(reason, " Lock: ", key.Name, "."));

    /// <summary>Builds the key a waiter and its notification are matched on.</summary>
    /// <param name="lockKey">The lock key.</param>
    /// <param name="owner">The owner.</param>
    /// <returns>The wait key.</returns>
    /// <remarks>
    /// The composition must match the server's, which uses the same lock and owner the notification
    /// carries. A separator is used because a lock name may itself contain either part.
    /// </remarks>
    private static string WaitKey(string lockKey, string? owner) =>
        string.Concat(lockKey, ":", owner);

    /// <summary>Reads the notification type the server sent.</summary>
    /// <param name="notification">The notification.</param>
    /// <returns>The type.</returns>
    private NacosLockNotificationType NotificationType(LockNotificationRequest notification)
    {
        if (string.Equals(notification.NotificationType, TimeoutNotification, StringComparison.Ordinal))
        {
            return NacosLockNotificationType.TimedOut;
        }

        // Anything else means the lock may be acquirable, which is the safe reading: the waiter
        // re-asks and the server answers with the truth.
        if (!string.Equals(notification.NotificationType, "AVAILABLE", StringComparison.Ordinal)
            && notification.NotificationType is not null)
        {
            NacosLockLog.UnknownNotificationType(
                _logger, notification.LockKey ?? "<unknown>", notification.NotificationType);
        }

        return NacosLockNotificationType.Available;
    }

    /// <summary>Converts a lease into the milliseconds the server expects.</summary>
    /// <param name="lease">The lease, or <see langword="null"/> for the server's default.</param>
    /// <returns>The duration in milliseconds, or <c>-1</c> for the server's default.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The lease is not a positive duration.</exception>
    private static long LeaseMilliseconds(TimeSpan? lease)
    {
        if (lease is null)
        {
            return ServerDefaultLeaseMilliseconds;
        }

        // Rounded up rather than truncated so a sub-millisecond lease cannot become the zero the
        // server rejects.
        long milliseconds = (long)Math.Ceiling(lease.Value.TotalMilliseconds);

        if (milliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lease), lease, "A lease must be a positive duration.");
        }

        return milliseconds;
    }

    /// <summary>Converts a wait time into the milliseconds the server expects.</summary>
    /// <param name="wait">The wait time, or <see langword="null"/> to not wait.</param>
    /// <returns>The duration in milliseconds, or zero to not wait.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The wait time is negative.</exception>
    private static long WaitMilliseconds(TimeSpan? wait)
    {
        if (wait is null)
        {
            return 0;
        }

        if (wait.Value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(wait), wait, "A wait time cannot be negative.");
        }

        return (long)Math.Ceiling(wait.Value.TotalMilliseconds);
    }

    /// <summary>How a lock notification resolved a waiter.</summary>
    private enum NacosLockNotificationType
    {
        /// <summary>The lock may be acquirable.</summary>
        Available,

        /// <summary>The server dropped the waiter from its queue.</summary>
        TimedOut,
    }
}
