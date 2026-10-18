using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Ai;

/// <summary>
/// Follows RAD agents by keeping a watch open on the AI registry connection.
/// </summary>
/// <remarks>
/// <para>
/// A watch is the push half of discovery. The client registers what it wants to follow once, and the
/// server answers with a hint whenever the answer may have changed; the client then reads the state
/// again. That is a different contract from the registry's other subscriptions, which are served by
/// re-reading on an interval, and it is why the RAD watch is the only AI surface that needs a server
/// push handler.
/// </para>
/// <para>
/// The watch belongs to the connection, not to the process: Nacos drops it when the connection
/// closes. Re-installing it after a reconnect with the same client identifier is therefore part of
/// the contract rather than an optimisation, and it is what <see cref="RestoreAsync"/> does.
/// </para>
/// <para>
/// Several callers that follow the same agent share one watch. The key they are grouped by is
/// derived from the discovery they would each send, so two callers asking the same question get one
/// watch and two enumerations of its answers.
/// </para>
/// </remarks>
internal sealed class NacosAgentWatchManager : IAsyncDisposable
{
    /// <summary>The error code Nacos uses for a feature a server does not implement.</summary>
    private const int NotImplementedErrorCode = 501;

    /// <summary>The error code Nacos uses for a request that cannot be honoured.</summary>
    private const int ServerErrorCode = 500;

    /// <summary>The error code Nacos uses when a request is malformed.</summary>
    private const int InvalidParamErrorCode = 400;

    /// <summary>The error code Nacos uses when a request is malformed before it reaches the server.</summary>
    private const int ClientInvalidParamErrorCode = -400;

    /// <summary>The error code Nacos uses when the caller is not allowed to read what it asked for.</summary>
    private const int NoRightErrorCode = 403;

    /// <summary>The error code Nacos uses for a resource that is not there.</summary>
    private const int NotFoundErrorCode = 404;

    /// <summary>The error code Nacos uses when a resource is not there, under the v3 spelling.</summary>
    private const int ResourceNotFoundErrorCode = -404;

    /// <summary>
    /// The error code Nacos uses for a resource that is not there, under the v2 spelling.
    /// </summary>
    /// <remarks>
    /// The AI module reports the v2 detail code through gRPC — the gRPC error mapper answers with the
    /// business code rather than the status — while the projection behind a watch reports the status
    /// code. Both spellings mean the same thing to a caller, so both are accepted.
    /// </remarks>
    private const int ResourceNotFoundDetailErrorCode = 20004;

    /// <summary>The error code Nacos uses when more is asked of the server than it will take.</summary>
    private const int OverThresholdErrorCode = 503;

    /// <summary>The error code Nacos uses when the client asked for more than it is allowed.</summary>
    private const int ClientOverThresholdErrorCode = -503;

    /// <summary>The error code Nacos uses for an agent that was never published.</summary>
    /// <remarks>
    /// The agent-specific spelling is what the legacy A2A surface answers with; the RAD discovery path
    /// uses the shared resource-not-found instead. It is accepted so that an absent agent is one state
    /// to the watch however the server names it.
    /// </remarks>
    private const int AgentNotFoundErrorCode = 50100;

    /// <summary>The error code Nacos uses for an agent version that was never published.</summary>
    private const int AgentVersionNotFoundErrorCode = 50101;

    /// <summary>The error code Nacos uses while an agent is protected by the A2A migration.</summary>
    private const int MigrationInProgressErrorCode = 50105;

    /// <summary>The longest a failed read is left alone before it is tried again.</summary>
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(1);

    /// <summary>How long a healthy watch waits before it looks at its own state again.</summary>
    /// <remarks>
    /// The wait exists so a lost hint cannot park a watch forever; the server pushes are what
    /// normally end it, and the timeout is long enough not to be a poll.
    /// </remarks>
    private static readonly TimeSpan IdleWait = TimeSpan.FromMinutes(5);

    /// <summary>How long a removal is given before it is abandoned.</summary>
    private static readonly TimeSpan UnsubscribeTimeout = TimeSpan.FromSeconds(5);

    private readonly NacosAiClient _owner;
    private readonly NacosOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Random _random;

    private readonly object _gate = new();
    private readonly Dictionary<string, Intent> _intents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Intent> _byWatchKey = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosAgentWatchManager"/> class.
    /// </summary>
    /// <param name="owner">The AI client that owns the connection.</param>
    /// <param name="options">The client options.</param>
    /// <param name="timeProvider">The clock used for retry and timeout timing.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="random">The source of retry jitter, or <see langword="null"/> for a fresh one.</param>
    public NacosAgentWatchManager(
        NacosAiClient owner,
        NacosOptions options,
        TimeProvider timeProvider,
        ILogger logger,
        Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _owner = owner;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _random = random ?? Random.Shared;
    }

    /// <summary>
    /// Follows one agent, yielding the current state and then every state the server reports.
    /// </summary>
    /// <param name="reference">The agent and the version or label to follow.</param>
    /// <param name="filter">What the caller can use, or <see langword="null"/> for everything.</param>
    /// <param name="ct">A token that ends the watch.</param>
    /// <returns>
    /// The current state as the first element — or <see langword="null"/> when the agent is not
    /// published — then the state again on every change, with <see langword="null"/> reporting that
    /// it stopped being served.
    /// </returns>
    public async IAsyncEnumerable<NacosAgentDiscoveryResult?> WatchAsync(
        NacosAgentReference reference,
        NacosAgentDiscoveryFilter? filter,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.AgentName, nameof(reference));

        string key = BuildKey(reference, filter);
        Subscriber subscriber = Add(key, reference, filter);

        try
        {
            await foreach (WatchSignal signal in subscriber.Channel.Reader
                .ReadAllAsync(ct)
                .ConfigureAwait(false))
            {
                if (signal.Error is not null)
                {
                    // A fatal signal ends the watch. It is raised on the reader's thread rather than
                    // logged so the caller sees why the watch it asked for is not there.
                    throw signal.Error;
                }

                yield return signal.Value;
            }
        }
        finally
        {
            await RemoveAsync(key, subscriber).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Answers a watch hint pushed by the server.
    /// </summary>
    /// <param name="request">The pushed message.</param>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>
    /// The acknowledgement, or <see langword="null"/> when the message is not a watch hint and another
    /// handler should be offered it.
    /// </returns>
    /// <remarks>
    /// A hint for a watch this client does not own is declined rather than accepted. Declining is how
    /// the server is told to stop sending a hint for a watch that was closed, which happens whenever
    /// a removal and a push cross on the wire.
    /// </remarks>
    public Task<NacosResponse?> OnServerRequestAsync(NacosRequest request, CancellationToken ct)
    {
        if (request is not AgentDiscoveryNotifyRequest notify || string.IsNullOrEmpty(notify.WatchKey))
        {
            return Task.FromResult<NacosResponse?>(null);
        }

        Intent? intent;

        lock (_gate)
        {
            _byWatchKey.TryGetValue(notify.WatchKey, out intent);
        }

        if (intent is null || !IsWellFormed(notify))
        {
            // A hint for a watch this client does not own — a removal and a push crossing on the wire —
            // is declined, which is how the server is told to stop sending it. So is a hint that
            // contradicts itself, because acting on it would mean guessing what the server meant.
            NacosAiLog.AgentWatchHintUnmatched(_logger, notify.WatchKey);

            return Task.FromResult<NacosResponse?>(new AgentDiscoveryNotifyResponse
            {
                WatchKey = notify.WatchKey,
                Accepted = false,
                ResultCode = NacosResponse.FailureCode,
            });
        }

        ApplyHint(intent, notify);

        if (intent.WatchKey is null)
        {
            // The server released the watch it announced the end of, so the key it pushed under no
            // longer names anything this client owns.
            lock (_gate)
            {
                if (_byWatchKey.TryGetValue(notify.WatchKey, out Intent? mapped)
                    && ReferenceEquals(mapped, intent))
                {
                    _byWatchKey.Remove(notify.WatchKey);
                }
            }
        }

        intent.Wake();

        return Task.FromResult<NacosResponse?>(new AgentDiscoveryNotifyResponse
        {
            WatchKey = notify.WatchKey,
            Accepted = true,
        });
    }

    /// <summary>
    /// Re-installs every watch after the connection was re-established.
    /// </summary>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>A task that completes once every watch has been told to re-install.</returns>
    /// <remarks>
    /// The server forgot every watch binding when the old connection closed, so the seen fingerprint
    /// is cleared along with the key: the state may have moved on during the outage, and the client
    /// has to read again rather than assume it is looking at the current answer.
    /// </remarks>
    public Task RestoreAsync(CancellationToken ct)
    {
        List<Intent> intents;

        lock (_gate)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            intents = [.. _intents.Values];
            _byWatchKey.Clear();
        }

        foreach (Intent intent in intents)
        {
            lock (intent.Gate)
            {
                intent.WatchKey = null;
                intent.Fingerprint = null;
                intent.PendingFingerprint = null;
                intent.Dirty = true;
            }

            intent.Wake();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        List<Intent> intents;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            intents = [.. _intents.Values];
            _intents.Clear();
            _byWatchKey.Clear();
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);

        foreach (Intent intent in intents)
        {
            await intent.Lifetime.CancelAsync().ConfigureAwait(false);
        }

        foreach (Intent intent in intents)
        {
            await intent.Loop.ConfigureAwait(false);
        }

        foreach (Intent intent in intents)
        {
            intent.Lifetime.Dispose();
        }

        _shutdown.Dispose();
    }

    /// <summary>
    /// Adds a reader to the watch for one discovery, creating if this is the first.
    /// </summary>
    /// <param name="key">The grouping key of the watch.</param>
    /// <param name="reference">The agent to follow.</param>
    /// <param name="filter">What the caller can use.</param>
    /// <returns>The reader.</returns>
    private Subscriber Add(string key, NacosAgentReference reference, NacosAgentDiscoveryFilter? filter)
    {
        Intent intent;
        Subscriber subscriber;
        bool start;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_intents.TryGetValue(key, out Intent? existing))
            {
                intent = existing;
                start = false;
            }
            else
            {
                intent = new Intent(key, reference, filter);
                _intents[key] = intent;
                start = true;
            }

            subscriber = new Subscriber();

            lock (intent.Gate)
            {
                intent.Subscribers.Add(subscriber);

                if (intent.Seeded)
                {
                    // A reader that joins an established watch is given what it already knows, so it
                    // does not wait for the next change to see where things stand.
                    subscriber.Channel.Writer.TryWrite(new WatchSignal(intent.Current, null));
                }
            }
        }

        if (start)
        {
            // Started outside the lock, because the loop's own work takes the same locks.
            intent.Loop = RunAsync(intent);
        }

        return subscriber;
    }

    /// <summary>
    /// Removes a reader, and the watch itself when it was the last one.
    /// </summary>
    /// <param name="key">The grouping key of the watch.</param>
    /// <param name="subscriber">The reader to remove.</param>
    /// <returns>A task that completes once the watch was closed.</returns>
    private async Task RemoveAsync(string key, Subscriber subscriber)
    {
        Intent? intent = null;
        string? watchKey = null;

        lock (_gate)
        {
            if (_intents.TryGetValue(key, out Intent? found))
            {
                bool removed;

                lock (found.Gate)
                {
                    removed = found.Subscribers.Remove(subscriber);

                    if (removed && found.Subscribers.Count == 0)
                    {
                        watchKey = found.WatchKey;
                    }
                }

                if (removed && found.Subscribers.Count == 0)
                {
                    _intents.Remove(key);
                    intent = found;
                }
            }
        }

        if (intent is null)
        {
            return;
        }

        await intent.Lifetime.CancelAsync().ConfigureAwait(false);

        if (watchKey is not null)
        {
            await UnsubscribeAsync(intent, watchKey).ConfigureAwait(false);
        }

        intent.Lifetime.Dispose();
    }

    /// <summary>
    /// Runs the state machine of one watch until it is closed.
    /// </summary>
    /// <param name="intent">The watch.</param>
    /// <returns>A task that completes when the watch ends.</returns>
    private async Task RunAsync(Intent intent)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            intent.Lifetime.Token, _shutdown.Token);

        CancellationToken ct = linked.Token;
        int failures = 0;

        try
        {
            while (!ct.IsCancellationRequested && !intent.Terminated)
            {
                if (intent.Unavailable)
                {
                    // The server reported the watch gone. The readers are told the agent is not
                    // served before the watch is installed again, because the answer they are holding
                    // is no longer one the server stands behind.
                    ReportUnavailable(intent);
                }

                if (intent.WatchKey is null)
                {
                    try
                    {
                        await SubscribeAsync(intent, ct).ConfigureAwait(false);
                        failures = 0;
                    }
                    catch (Exception ex) when (IsNotFound(ex))
                    {
                        // An agent that was never published is a state rather than a failure, and the
                        // watch is offered again until the agent appears.
                        ReportUnavailable(intent);
                        failures++;
                        await WaitAsync(intent, RetryDelay(failures), ct).ConfigureAwait(false);
                        continue;
                    }
                    catch (Exception ex) when (IsTerminal(ex))
                    {
                        Fail(intent, ex);
                        return;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        NacosAiLog.AgentWatchRestoreFailed(_logger, AgentName(intent), ex.Message);
                        failures++;
                        await WaitAsync(intent, RetryDelay(failures), ct).ConfigureAwait(false);
                        continue;
                    }
                }

                if (intent.Terminated)
                {
                    break;
                }

                if (intent.Dirty || !intent.Seeded)
                {
                    try
                    {
                        await RefreshAsync(intent, ct).ConfigureAwait(false);
                        failures = 0;
                    }
                    catch (Exception ex) when (IsTerminal(ex))
                    {
                        Fail(intent, ex);
                        return;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        NacosAiLog.AgentWatchRefreshFailed(_logger, AgentName(intent), ex.Message);
                        failures++;
                        await WaitAsync(intent, RetryDelay(failures), ct).ConfigureAwait(false);
                        continue;
                    }
                }

                if (intent.Terminated)
                {
                    break;
                }

                await WaitAsync(intent, IdleWait, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The watch was closed or the client was disposed; the readers are completed below.
        }
        finally
        {
            Complete(intent);
        }
    }

    /// <summary>
    /// Installs the watch on the connection.
    /// </summary>
    /// <param name="intent">The watch.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server admitted the watch.</returns>
    private async Task SubscribeAsync(Intent intent, CancellationToken ct)
    {
        NacosGrpcConnection connection = await _owner.GetConnectionAsync(ct).ConfigureAwait(false);
        EnsureWatchSupported(connection);

        string? fingerprint;

        lock (intent.Gate)
        {
            fingerprint = intent.Fingerprint;
        }

        var request = new AgentSubscribeRpcRequest
        {
            ClientWatchId = intent.ClientWatchId,
            DiscoveryRequest = BuildDiscovery(intent),
            MaterializedFingerprint = fingerprint,
        };

        AgentSubscribeRpcResponse response =
            await connection.RequestAsync<AgentSubscribeRpcResponse>(request, ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(response.WatchKey))
        {
            throw new NacosServerException(
                ServerErrorCode,
                string.Concat(
                    "The Nacos server admitted the watch of agent '",
                    AgentName(intent),
                    "' but returned no watch key."));
        }

        if (!response.RefreshRequired && response.ObservedFingerprint is null)
        {
            // A server that says no read is needed and does not say what state it thinks the client
            // holds has contradicted itself, and the client would have nothing to compare against.
            throw new NacosServerException(
                ServerErrorCode,
                string.Concat(
                    "The Nacos server admitted the watch of agent '",
                    AgentName(intent),
                    "' without asking for a refresh and without reporting a fingerprint."));
        }

        lock (_gate)
        {
            _byWatchKey[response.WatchKey] = intent;
        }

        lock (intent.Gate)
        {
            intent.WatchKey = response.WatchKey;

            if (response.ObservedFingerprint is not null)
            {
                intent.Fingerprint = response.ObservedFingerprint;
            }

            if (response.RefreshRequired || response.ObservedFingerprint is null)
            {
                intent.Dirty = true;
            }
        }

        NacosAiLog.AgentWatchStarted(_logger, AgentName(intent), intent.Reference.Version, intent.Reference.Label);
    }

    /// <summary>
    /// Reads the state the watch follows and hands it to the readers when it moved.
    /// </summary>
    /// <param name="intent">The watch.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the state was read.</returns>
    /// <remarks>
    /// The state is handed over only when it differs from the one the readers already hold. A hint
    /// says that the answer may have changed rather than that it did — a revalidation carries no state
    /// at all — so a read that finds the same answer has nothing to report, and reporting it would
    /// tell a caller that acted on the last element that something happened.
    /// </remarks>
    private async Task RefreshAsync(Intent intent, CancellationToken ct)
    {
        NacosGrpcConnection connection = await _owner.GetConnectionAsync(ct).ConfigureAwait(false);

        var request = new AgentDiscoveryRpcRequest { DiscoveryRequest = BuildDiscovery(intent) };

        NacosAgentDiscoveryResult? result;

        try
        {
            AgentDiscoveryResponse response =
                await connection.RequestAsync<AgentDiscoveryResponse>(request, ct).ConfigureAwait(false);

            result = response.DiscoveryResult;
        }
        catch (NacosServerException ex) when (IsNotFound(ex))
        {
            // An agent that is not published is a state the watch reports rather than a failure of
            // the read: the server pushes a hint when it appears.
            result = null;
        }

        byte[]? digest = Digest(result);
        bool changed;

        lock (intent.Gate)
        {
            changed = !intent.Seeded || !DigestEquals(intent.Digest, digest);

            intent.Dirty = false;
            intent.Seeded = true;
            intent.Current = result;
            intent.Digest = digest;
            intent.UnavailableNotified = false;

            // The fingerprint only advances once the state behind it was actually read, so a read
            // that fails leaves the two sides able to notice the change on the next hint.
            if (intent.PendingFingerprint is not null)
            {
                intent.Fingerprint = intent.PendingFingerprint;
                intent.PendingFingerprint = null;
            }
        }

        if (changed)
        {
            Broadcast(intent, result);
        }
    }

    /// <summary>
    /// Tells the readers that the agent is not served.
    /// </summary>
    /// <param name="intent">The watch.</param>
    /// <remarks>
    /// The readers of a watch are told once per absence, and only when the absence is news: an agent
    /// that is gone stays gone until it is published again, and a watch that repeated the absence on
    /// every attempt would bury the change it is waiting for. A watch whose readers already hold an
    /// absence — after a reconnect over an agent that was not there either — has nothing to report.
    /// </remarks>
    private static void ReportUnavailable(Intent intent)
    {
        bool notify;

        lock (intent.Gate)
        {
            notify = !intent.UnavailableNotified && (!intent.Seeded || intent.Current is not null);

            intent.UnavailableNotified = true;
            intent.Unavailable = false;
            intent.Seeded = true;
            intent.Current = null;
            intent.Digest = null;
        }

        if (notify)
        {
            Broadcast(intent, null);
        }
    }

    /// <summary>
    /// Reduces a state to what makes two of them the same answer.
    /// </summary>
    /// <param name="result">The state, or <see langword="null"/> when the agent is not served.</param>
    /// <returns>The state's digest, or <see langword="null"/> for an agent that is not served.</returns>
    /// <remarks>
    /// This is a local device, not the fingerprint the protocol speaks in: it is never sent anywhere.
    /// The server's fingerprint is what decides whether a hint is worth acting on; this only decides
    /// whether the read it asked for found anything a caller should see.
    /// </remarks>
    private static byte[]? Digest(NacosAgentDiscoveryResult? result) =>
        result is null
            ? null
            : JsonSerializer.SerializeToUtf8Bytes(result, NacosJsonContext.Default.NacosAgentDiscoveryResult);

    /// <summary>
    /// Compares two state digests.
    /// </summary>
    /// <param name="left">The digest the readers hold.</param>
    /// <param name="right">The digest of the state just read.</param>
    /// <returns><see langword="true"/> when both describe the same answer.</returns>
    private static bool DigestEquals(byte[]? left, byte[]? right) =>
        left is null
            ? right is null
            : right is not null && left.AsSpan().SequenceEqual(right);

    /// <summary>
    /// Asks the server to forget one watch.
    /// </summary>
    /// <param name="intent">The watch being closed.</param>
    /// <param name="watchKey">The key the server issued.</param>
    /// <returns>A task that completes once the removal was sent or abandoned.</returns>
    /// <remarks>
    /// The removal is best effort. A watch dies with the connection it was installed on regardless,
    /// so a removal that cannot be sent is not something the caller can act on, and the reader has
    /// already stopped reading by the time it is attempted.
    /// </remarks>
    private async Task UnsubscribeAsync(Intent intent, string watchKey)
    {
        using var timeout = new CancellationTokenSource(UnsubscribeTimeout, _timeProvider);

        try
        {
            NacosGrpcConnection connection =
                await _owner.GetConnectionAsync(timeout.Token).ConfigureAwait(false);

            await connection.RequestAsync<AgentUnsubscribeRpcResponse>(
                new AgentUnsubscribeRpcRequest { WatchKey = watchKey },
                timeout.Token).ConfigureAwait(false);

            NacosAiLog.AgentWatchStopped(_logger, AgentName(intent));
        }
        catch (Exception ex)
        {
            NacosAiLog.AgentWatchUnsubscribeFailed(_logger, AgentName(intent), ex.Message);
        }
    }

    /// <summary>
    /// Determines whether a hint says enough to be acted on.
    /// </summary>
    /// <param name="notify">The hint.</param>
    /// <returns><see langword="true"/> when the hint is one this client version understands.</returns>
    /// <remarks>
    /// A hint with no event type, a termination with no reason, and an event type this client version
    /// does not define are all declined rather than guessed at. Declining is what keeps a hint the
    /// client cannot honour from being answered as though it had been.
    /// </remarks>
    private static bool IsWellFormed(AgentDiscoveryNotifyRequest notify) => notify.EventType switch
    {
        null or "" => false,
        NacosAiWatchEvent.Invalidate => true,
        NacosAiWatchEvent.Revalidate => true,
        NacosAiWatchEvent.Terminated => notify.ErrorCode is not null,
        _ => false,
    };

    /// <summary>
    /// Applies one pushed hint to a watch.
    /// </summary>
    /// <param name="intent">The watch the hint is for.</param>
    /// <param name="notify">The hint.</param>
    private static void ApplyHint(Intent intent, AgentDiscoveryNotifyRequest notify)
    {
        if (string.Equals(notify.EventType, NacosAiWatchEvent.Terminated, StringComparison.Ordinal))
        {
            if (IsNotFoundCode(notify.ErrorCode))
            {
                lock (intent.Gate)
                {
                    // The agent is gone, which is a state rather than the end of the interest in it:
                    // the watch is offered again, and the readers are told in the meantime that the
                    // agent is not served. The key is dropped because the server released it, and the
                    // fingerprint with it because the state the server will report next may be one it
                    // reported before.
                    intent.WatchKey = null;
                    intent.Fingerprint = null;
                    intent.PendingFingerprint = null;
                    intent.Dirty = false;
                    intent.UnavailableNotified = false;
                    intent.Unavailable = true;
                }

                return;
            }

            Fail(
                intent,
                new NacosServerException(
                    notify.ErrorCode!.Value,
                    "The Nacos server ended the RAD agent watch."));

            return;
        }

        if (string.Equals(notify.EventType, NacosAiWatchEvent.Revalidate, StringComparison.Ordinal))
        {
            lock (intent.Gate)
            {
                // A revalidation is the server asking to be read again, so it is acted on whatever the
                // fingerprint of the last read was; the server says it cannot tell what the answer is.
                intent.PendingFingerprint = null;
                intent.Dirty = true;
            }

            return;
        }

        lock (intent.Gate)
        {
            if (notify.ObservedFingerprint is not null
                && notify.ObservedFingerprint == (intent.PendingFingerprint ?? intent.Fingerprint))
            {
                // A hint that carries the state the client holds — or one it has already been told
                // about and not yet read — says nothing new, so no read is scheduled for it.
                return;
            }

            intent.PendingFingerprint = notify.ObservedFingerprint;
            intent.Dirty = true;
        }
    }

    /// <summary>
    /// Determines whether an error code reports an agent that is not there.
    /// </summary>
    /// <param name="errorCode">The error code, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the agent is not published.</returns>
    private static bool IsNotFoundCode(int? errorCode) => errorCode is
        NotFoundErrorCode or ResourceNotFoundErrorCode or ResourceNotFoundDetailErrorCode
        or AgentNotFoundErrorCode or AgentVersionNotFoundErrorCode;

    /// <summary>
    /// Ends a watch with an error the caller has to see.
    /// </summary>
    /// <param name="intent">The watch.</param>
    /// <param name="error">The error to raise.</param>
    private static void Fail(Intent intent, Exception error)
    {
        lock (intent.Gate)
        {
            intent.Terminated = true;
        }

        foreach (Subscriber subscriber in Snapshot(intent))
        {
            subscriber.Channel.Writer.TryWrite(new WatchSignal(null, error));
        }
    }

    /// <summary>
    /// Hands one state to every reader of a watch.
    /// </summary>
    /// <param name="intent">The watch.</param>
    /// <param name="value">The state.</param>
    private static void Broadcast(Intent intent, NacosAgentDiscoveryResult? value)
    {
        foreach (Subscriber subscriber in Snapshot(intent))
        {
            subscriber.Channel.Writer.TryWrite(new WatchSignal(value, null));
        }
    }

    /// <summary>
    /// Ends every reader of a watch.
    /// </summary>
    /// <param name="intent">The watch.</param>
    private static void Complete(Intent intent)
    {
        foreach (Subscriber subscriber in Snapshot(intent))
        {
            subscriber.Channel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Takes a snapshot of the readers of a watch.
    /// </summary>
    /// <param name="intent">The watch.</param>
    /// <returns>The readers, at the moment of the call.</returns>
    private static Subscriber[] Snapshot(Intent intent)
    {
        lock (intent.Gate)
        {
            return [.. intent.Subscribers];
        }
    }

    /// <summary>
    /// Waits until a hint arrives, the delay elapses, or the watch ends.
    /// </summary>
    /// <param name="intent">The watch.</param>
    /// <param name="delay">How long to wait for a hint.</param>
    /// <param name="ct">A token that ends the watch.</param>
    /// <returns><see langword="true"/> when a hint broke the wait.</returns>
    private async Task<bool> WaitAsync(Intent intent, TimeSpan delay, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // The timer is the clock the tests control, and it is what ends a wait that no hint breaks.
        using ITimer timer = _timeProvider.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(),
            timeout,
            delay,
            Timeout.InfiniteTimeSpan);

        try
        {
            await intent.Wakes.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            return false;
        }
    }

    /// <summary>
    /// Computes the delay before the next attempt.
    /// </summary>
    /// <param name="failures">How many attempts failed in a row.</param>
    /// <returns>The delay to wait.</returns>
    /// <remarks>
    /// The delay doubles from the cache interval up to a minute, then is spread across
    /// four-fifths to six-fifths of that value so a server that comes back is not met by every
    /// client at once. The shape is the official client's bounded jittered backoff.
    /// </remarks>
    private TimeSpan RetryDelay(int failures)
    {
        long minimum = (long)_options.Ai.CacheRefreshInterval.TotalMilliseconds;
        long maximum = (long)MaximumRetryDelay.TotalMilliseconds;

        if (minimum < 1)
        {
            minimum = 1;
        }

        if (maximum < minimum)
        {
            maximum = minimum;
        }

        int exponent = Math.Min(Math.Max(0, failures - 1), 20);
        long exponential = minimum > (maximum >> exponent)
            ? maximum
            : Math.Min(maximum, minimum << exponent);
        long lower = Math.Max(1L, exponential * 4L / 5L);
        long upper = Math.Max(lower, Math.Min(maximum, exponential * 6L / 5L));
        long chosen = lower == upper ? lower : lower + (long)(_random.NextDouble() * (upper - lower + 1));

        return TimeSpan.FromMilliseconds(chosen);
    }

    /// <summary>
    /// Builds the discovery a watch and its reads are written in terms of.
    /// </summary>
    /// <param name="intent">The watch.</param>
    /// <returns>The discovery request.</returns>
    private NacosAgentDiscoveryRequest BuildDiscovery(Intent intent) => new()
    {
        NamespaceId = _options.Namespace,
        Reference = intent.Reference,
        Filter = intent.Filter,
    };

    /// <summary>
    /// Fails a watch when the server does not take the hint push.
    /// </summary>
    /// <param name="connection">The AI registry connection.</param>
    /// <remarks>
    /// A server that states <c>radWatchV1</c> is the only one a watch can be installed on. A server
    /// that says nothing about it is refused as well rather than watched without hints: a watch whose
    /// hints are never delivered would silently stop reporting changes. Both refusals carry the
    /// not-implemented code, because the ability a connection was handed cannot change while it is
    /// open — retrying would only ask the same server the same unanswerable question.
    /// </remarks>
    private static void EnsureWatchSupported(NacosGrpcConnection connection)
    {
        if (connection.ServerAbilities.TryGetValue(NacosAiServerAbility.RadWatchV1, out bool supported))
        {
            if (supported)
            {
                return;
            }

            throw new NacosServerException(
                NotImplementedErrorCode,
                "The connected Nacos server states that it does not implement the RAD watch hint push.");
        }

        throw new NacosServerException(
            NotImplementedErrorCode,
            "The connected Nacos server did not state whether it implements the RAD watch hint push, "
            + "so a watch could not be told apart from one whose hints are silently dropped.");
    }

    /// <summary>
    /// Determines whether an error says the agent the watch follows is not there.
    /// </summary>
    /// <param name="exception">The error.</param>
    /// <returns><see langword="true"/> when the agent is not published.</returns>
    /// <remarks>
    /// Several spellings mean the same thing, because the paths that report it differ: the gRPC error
    /// mapper answers with the v2 business code, the projection behind a watch reports the status
    /// code, and the legacy A2A surface has codes of its own. An agent that is not there is a state a
    /// watch reports rather than a failure of the watch, because the server pushes a hint when it
    /// appears.
    /// </remarks>
    private static bool IsNotFound(Exception exception) =>
        exception is NacosServerException server && IsNotFoundCode(server.ErrorCode);

    /// <summary>
    /// Determines whether an error ends a watch.
    /// </summary>
    /// <param name="exception">The error.</param>
    /// <returns><see langword="true"/> when asking again cannot help.</returns>
    /// <remarks>
    /// A server that is behind, that refuses the credentials, that will not take the load, or that
    /// answered outside the contract will not change its mind because the client asked again; a
    /// connection that dropped will. Only the first kind is terminal.
    /// </remarks>
    private static bool IsTerminal(Exception exception) =>
        exception is NacosAuthException
        || (exception is NacosServerException server && server.ErrorCode is
            NotImplementedErrorCode or ServerErrorCode
            or InvalidParamErrorCode or ClientInvalidParamErrorCode
            or NoRightErrorCode
            or OverThresholdErrorCode or ClientOverThresholdErrorCode
            or MigrationInProgressErrorCode);

    /// <summary>
    /// Reads the agent name a watch follows.
    /// </summary>
    /// <param name="intent">The watch.</param>
    /// <returns>The agent name.</returns>
    private static string AgentName(Intent intent) => intent.Reference.AgentName ?? string.Empty;

    /// <summary>
    /// Derives the key that groups callers following the same discovery.
    /// </summary>
    /// <param name="reference">The agent and version or label.</param>
    /// <param name="filter">What the caller can use.</param>
    /// <returns>The grouping key.</returns>
    /// <remarks>
    /// The key is a local grouping device, not the server's canonical request form: it only has to
    /// be stable and complete enough that two callers asking the same question agree. Set-valued
    /// filters are sorted and deduplicated so the order they were written in does not split a watch.
    /// </remarks>
    private string BuildKey(NacosAgentReference reference, NacosAgentDiscoveryFilter? filter)
    {
        var builder = new StringBuilder();

        builder.Append(_options.Namespace ?? string.Empty).Append('\u001f');
        builder.Append(reference.AgentName).Append('\u001f');
        builder.Append(reference.Version ?? string.Empty).Append('\u001f');
        builder.Append(reference.Label ?? string.Empty).Append('\u001f');

        if (filter is null)
        {
            return builder.ToString();
        }

        AppendSet(builder, filter.Protocols);
        builder.Append('\u001f');
        builder.Append(filter.ProtocolVersion ?? string.Empty).Append('\u001f');
        AppendSet(builder, filter.Transports);
        builder.Append('\u001f');
        AppendSet(builder, filter.EndpointSources);
        builder.Append('\u001f');

        if (filter.MetadataSelector is not null)
        {
            foreach (string name in filter.MetadataSelector.Keys.Order(StringComparer.Ordinal))
            {
                builder.Append(name).Append('=').Append(filter.MetadataSelector[name]).Append('\u001f');
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Appends a set-valued filter member in canonical order.
    /// </summary>
    /// <param name="builder">The key being built.</param>
    /// <param name="values">The values, or <see langword="null"/>.</param>
    private static void AppendSet(StringBuilder builder, List<string>? values)
    {
        if (values is null)
        {
            return;
        }

        foreach (string value in values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            builder.Append(value).Append(',');
        }
    }

    /// <summary>
    /// One watch, shared by the callers following the same discovery.
    /// </summary>
    private sealed class Intent
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Intent"/> class.
        /// </summary>
        /// <param name="key">The grouping key.</param>
        /// <param name="reference">The agent to follow.</param>
        /// <param name="filter">What the caller can use.</param>
        public Intent(string key, NacosAgentReference reference, NacosAgentDiscoveryFilter? filter)
        {
            Key = key;
            Reference = reference;
            Filter = filter;

            // The identifier is minted once and reused across reconnects, which is what makes a
            // re-installation re-bind the same watch on the server rather than add a second one. It
            // matches the pattern the server validates: hex, and well inside the 128 character cap.
            ClientWatchId = Guid.NewGuid().ToString("N");
        }

        /// <summary>Gets the grouping key.</summary>
        public string Key { get; }

        /// <summary>Gets the agent and version or label.</summary>
        public NacosAgentReference Reference { get; }

        /// <summary>Gets what the caller can use.</summary>
        public NacosAgentDiscoveryFilter? Filter { get; }

        /// <summary>Gets the stable client identifier of the watch.</summary>
        public string ClientWatchId { get; }

        /// <summary>Gets the source that ends the watch.</summary>
        public CancellationTokenSource Lifetime { get; } = new();

        /// <summary>Gets the channel a hint is announced on.</summary>
        public Channel<byte> Wakes { get; } = Channel.CreateBounded<byte>(
            new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropWrite,
            });

        /// <summary>Gets the lock that guards the members below.</summary>
        public object Gate { get; } = new();

        /// <summary>Gets the readers following this watch.</summary>
        public List<Subscriber> Subscribers { get; } = [];

        /// <summary>Gets or sets the key the server issued, once the watch is installed.</summary>
        public string? WatchKey { get; set; }

        /// <summary>Gets or sets the fingerprint of the state last read.</summary>
        public string? Fingerprint { get; set; }

        /// <summary>Gets or sets the fingerprint a hint announced but the read has not caught up with.</summary>
        public string? PendingFingerprint { get; set; }

        /// <summary>Gets or sets the state last read.</summary>
        public NacosAgentDiscoveryResult? Current { get; set; }

        /// <summary>Gets or sets the digest of the state last read.</summary>
        public byte[]? Digest { get; set; }

        /// <summary>Gets or sets a value indicating whether the state was ever read.</summary>
        public bool Seeded { get; set; }

        /// <summary>Gets or sets a value indicating whether the state has to be read again.</summary>
        public bool Dirty { get; set; }

        /// <summary>Gets or sets a value indicating whether the agent has to be reported as not served.</summary>
        public bool Unavailable { get; set; }

        /// <summary>Gets or sets a value indicating whether the absence was already reported.</summary>
        public bool UnavailableNotified { get; set; }

        /// <summary>Gets or sets a value indicating whether the server ended the watch.</summary>
        public bool Terminated { get; set; }

        /// <summary>Gets or sets the state machine of the watch.</summary>
        public Task Loop { get; set; } = Task.CompletedTask;

        /// <summary>Ends the wait the state machine is in.</summary>
        public void Wake() => Wakes.Writer.TryWrite(0);
    }

    /// <summary>
    /// One caller following a watch.
    /// </summary>
    private sealed class Subscriber
    {
        /// <summary>Gets the channel the caller reads its states from.</summary>
        public Channel<WatchSignal> Channel { get; } = System.Threading.Channels.Channel
            .CreateUnbounded<WatchSignal>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
    }

    /// <summary>
    /// One message to a caller: either a state or the error that ended the watch.
    /// </summary>
    /// <param name="Value">The state, or <see langword="null"/> when the agent is not served.</param>
    /// <param name="Error">The error that ends the watch, or <see langword="null"/>.</param>
    private readonly record struct WatchSignal(NacosAgentDiscoveryResult? Value, Exception? Error);
}
