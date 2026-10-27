using Lolitacoding.Nacos.Protocol.Messages;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Ai;

/// <summary>
/// Keeps one HTTP batch long poll open for the complete watch set of a namespace.
/// </summary>
/// <remarks>
/// <para>
/// The server answers a long poll only when a watch's projection changed or the requested timeout
/// elapsed, so one request covers every watch the client follows. The transport owns only the wire
/// registration and the generation that makes an outdated answer harmless; deciding what a change
/// means is left to the watch manager, which reads the authoritative state again.
/// </para>
/// <para>
/// A watch set change increments the generation and cancels the in-flight request, because the
/// server would otherwise hold a request that no longer describes what the client follows. A watch
/// that was reported changed is left out of the next batch until the manager has read the new state
/// and handed back a fingerprint, which is what stops the server from reporting the same change
/// repeatedly.
/// </para>
/// </remarks>
internal sealed class NacosAgentHttpWatchTransport : IAsyncDisposable
{
    /// <summary>How long one long poll asks the server to wait.</summary>
    private static readonly TimeSpan LongPollTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long a failed long poll waits before it is tried again.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>How many consecutive failures a binding is given before it is abandoned.</summary>
    private const int FailuresBeforeFallback = 3;

    /// <summary>The longest timeout the server accepts for one long poll.</summary>
    private const int MaxTimeoutMillis = 60000;

    /// <summary>The error code Nacos uses for a request that cannot be honoured.</summary>
    private const int ServerErrorCode = 500;

    /// <summary>The error code Nacos uses for a feature a server does not implement.</summary>
    private const int NotImplementedErrorCode = 501;

    /// <summary>The error code Nacos uses when a resource is not there.</summary>
    private const int NotFoundErrorCode = 404;

    /// <summary>The error code Nacos uses when a resource is not there, under the v3 spelling.</summary>
    private const int ResourceNotFoundErrorCode = -404;

    /// <summary>The error code Nacos uses when a handler is not mapped.</summary>
    private const int NoHandlerErrorCode = 302;

    /// <summary>The error code Nacos uses when the caller is not allowed to read what it asked for.</summary>
    private const int NoRightErrorCode = 403;

    /// <summary>The error code Nacos uses when a request is malformed.</summary>
    private const int InvalidParamErrorCode = 400;

    /// <summary>The error code Nacos uses when a request is malformed before it reaches the server.</summary>
    private const int ClientInvalidParamErrorCode = -400;

    /// <summary>The error code Nacos uses while an agent is protected by the A2A migration.</summary>
    private const int MigrationInProgressErrorCode = 50105;

    private readonly INacosAgentHttpWatchBinding _client;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Action<string> _invalidated;
    private readonly Action<string, NacosException> _unavailable;
    private readonly Action<NacosException> _wireUnavailable;
    private readonly TimeSpan _longPollTimeout;
    private readonly TimeSpan _retryDelay;
    private readonly int _failuresBeforeFallback;
    private readonly object _gate = new();
    private readonly Dictionary<string, WireWatch> _watches = new(StringComparer.Ordinal);
    private readonly HashSet<string> _awaitingRefresh = new(StringComparer.Ordinal);
    private readonly List<string> _pendingAdditions = [];
    private readonly CancellationTokenSource _shutdown = new();

    private CancellationTokenSource? _requestCancellation;
    private CancellationTokenSource? _retryCancellation;
    private Task? _requestTask;
    private Task? _retryTask;
    private long _generation;
    private int _consecutiveFailures;
    private bool _available = true;
    private bool _closed;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosAgentHttpWatchTransport"/> class.
    /// </summary>
    /// <param name="client">The HTTP watch binding.</param>
    /// <param name="timeProvider">The clock used for the retry delay.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="invalidated">
    /// Called with a watch identifier whose projection changed.
    /// </param>
    /// <param name="unavailable">
    /// Called with a watch identifier the server will no longer serve, and the reason.
    /// </param>
    /// <param name="wireUnavailable">
    /// Called once when the whole binding is no longer usable.
    /// </param>
    public NacosAgentHttpWatchTransport(
        INacosAgentHttpWatchBinding client,
        TimeProvider timeProvider,
        ILogger logger,
        Action<string> invalidated,
        Action<string, NacosException> unavailable,
        Action<NacosException> wireUnavailable)
        : this(
            client,
            timeProvider,
            logger,
            invalidated,
            unavailable,
            wireUnavailable,
            LongPollTimeout,
            RetryDelay,
            FailuresBeforeFallback)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosAgentHttpWatchTransport"/> class with the
    /// timings a test needs.
    /// </summary>
    /// <param name="client">The HTTP watch binding.</param>
    /// <param name="timeProvider">The clock used for the retry delay.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="invalidated">Called with a watch identifier whose projection changed.</param>
    /// <param name="unavailable">Called with a watch identifier the server will no longer serve.</param>
    /// <param name="wireUnavailable">Called once when the whole binding is no longer usable.</param>
    /// <param name="longPollTimeout">How long one long poll asks the server to wait.</param>
    /// <param name="retryDelay">How long a failed long poll waits before it is tried again.</param>
    /// <param name="failuresBeforeFallback">
    /// How many consecutive failures a binding is given before it is abandoned.
    /// </param>
    internal NacosAgentHttpWatchTransport(
        INacosAgentHttpWatchBinding client,
        TimeProvider timeProvider,
        ILogger logger,
        Action<string> invalidated,
        Action<string, NacosException> unavailable,
        Action<NacosException> wireUnavailable,
        TimeSpan longPollTimeout,
        TimeSpan retryDelay,
        int failuresBeforeFallback)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(invalidated);
        ArgumentNullException.ThrowIfNull(unavailable);
        ArgumentNullException.ThrowIfNull(wireUnavailable);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(longPollTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retryDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(failuresBeforeFallback, 1);

        _client = client;
        _timeProvider = timeProvider;
        _logger = logger;
        _invalidated = invalidated;
        _unavailable = unavailable;
        _wireUnavailable = wireUnavailable;
        _longPollTimeout = longPollTimeout;
        _retryDelay = retryDelay;
        _failuresBeforeFallback = failuresBeforeFallback;
    }

    /// <summary>
    /// Gets a value indicating whether the binding may still be used.
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                return _available && !_closed;
            }
        }
    }

    /// <summary>
    /// Adds or replaces one watch and starts a new long poll for the changed set.
    /// </summary>
    /// <param name="clientWatchId">The client's stable identifier for the watch.</param>
    /// <param name="request">The discovery the watch follows.</param>
    /// <param name="fingerprint">The fingerprint of the state the caller already holds.</param>
    /// <exception cref="NacosServerException">The binding is no longer available.</exception>
    public void Start(string clientWatchId, NacosAgentDiscoveryRequest request, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientWatchId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        lock (_gate)
        {
            if (_closed)
            {
                throw new NacosServerException(
                    ServerErrorCode,
                    "The agent HTTP watch transport has been shut down.");
            }

            if (!_available)
            {
                throw new NacosServerException(
                    NotImplementedErrorCode,
                    "The agent HTTP watch binding is unavailable.");
            }

            if (_watches.TryGetValue(clientWatchId, out WireWatch? existing))
            {
                existing.Request = request;
                existing.Fingerprint = fingerprint;
            }
            else
            {
                _watches[clientWatchId] = new WireWatch(clientWatchId, request, fingerprint);
                _pendingAdditions.Add(clientWatchId);
            }

            _awaitingRefresh.Remove(clientWatchId);
            AdvanceLocked();

            NacosAiLog.AgentWatchHttpRegistered(_logger, clientWatchId);
        }
    }

    /// <summary>
    /// Hands the fingerprint of a freshly read state back to the binding.
    /// </summary>
    /// <param name="clientWatchId">The watch identifier.</param>
    /// <param name="fingerprint">The fingerprint of the state just read.</param>
    public void Update(string clientWatchId, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientWatchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        lock (_gate)
        {
            if (_closed || !_available || !_watches.TryGetValue(clientWatchId, out WireWatch? watch))
            {
                return;
            }

            watch.Fingerprint = fingerprint;
            _awaitingRefresh.Remove(clientWatchId);
            AdvanceLocked();
        }
    }

    /// <summary>
    /// Removes one watch from the set.
    /// </summary>
    /// <param name="clientWatchId">The watch identifier.</param>
    public void Stop(string clientWatchId)
    {
        lock (_gate)
        {
            if (!_watches.Remove(clientWatchId))
            {
                return;
            }

            _awaitingRefresh.Remove(clientWatchId);
            _pendingAdditions.Remove(clientWatchId);
            AdvanceLocked();

            NacosAiLog.AgentWatchHttpStopped(_logger, clientWatchId);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task? request;
        Task? retry;

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _available = false;
            _watches.Clear();
            _awaitingRefresh.Clear();
            _pendingAdditions.Clear();
            _requestCancellation?.Cancel();
            _requestCancellation = null;
            _retryCancellation?.Cancel();
            _retryCancellation = null;
            request = _requestTask;
            retry = _retryTask;
            _requestTask = null;
            _retryTask = null;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);

        foreach (Task? task in new[] { request, retry })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The tasks are cancelled by the shutdown above.
            }
        }

        _shutdown.Dispose();
    }

    /// <summary>
    /// Increments the generation, cancels the in-flight work, and starts a new long poll.
    /// </summary>
    private void AdvanceLocked()
    {
        _generation++;
        _consecutiveFailures = 0;
        CancelRequestLocked();
        CancelRetryLocked();
        DispatchLocked(_generation);
    }

    /// <summary>
    /// Starts one long poll if the generation still describes the watch set.
    /// </summary>
    /// <param name="expectedGeneration">The generation the request is built for.</param>
    private void DispatchLocked(long expectedGeneration)
    {
        if (_closed || !_available || _generation != expectedGeneration || _requestTask is not null)
        {
            return;
        }

        var items = new List<AgentWatchBatchItem>(_watches.Count);

        foreach (WireWatch watch in _watches.Values)
        {
            if (_awaitingRefresh.Contains(watch.ClientWatchId))
            {
                continue;
            }

            items.Add(new AgentWatchBatchItem
            {
                ClientWatchId = watch.ClientWatchId,
                DiscoveryRequest = watch.Request,
                MaterializedFingerprint = watch.Fingerprint,
            });
        }

        if (items.Count == 0)
        {
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);

        _requestCancellation = cancellation;
        Task task = RunRequestAsync(expectedGeneration, items, cancellation);

        if (ReferenceEquals(_requestCancellation, cancellation))
        {
            _requestTask = task;
        }
    }

    /// <summary>
    /// Runs one long poll and applies its answer.
    /// </summary>
    /// <param name="generation">The generation the request was built for.</param>
    /// <param name="items">The watch set sent.</param>
    /// <param name="cancellation">The source that cancels this request.</param>
    /// <returns>A task that completes when the answer was applied.</returns>
    private async Task RunRequestAsync(
        long generation,
        IReadOnlyList<AgentWatchBatchItem> items,
        CancellationTokenSource cancellation)
    {
        try
        {
            AgentWatchBatchResponse response = await _client.WatchAsync(
                generation,
                (int)_longPollTimeout.TotalMilliseconds,
                items,
                cancellation.Token).ConfigureAwait(false);

            FinishRequest(cancellation);
            HandleResponse(generation, response);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            FinishRequest(cancellation);
        }
        catch (NacosException ex)
        {
            FinishRequest(cancellation);
            HandleFailure(generation, ex);
        }
        catch (Exception ex)
        {
            FinishRequest(cancellation);
            HandleFailure(
                generation,
                new NacosServerException(
                    string.Concat(
                        "The agent HTTP watch request failed: ",
                        ex.Message)));
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    /// <summary>
    /// Clears the in-flight request if it is still the one that finished.
    /// </summary>
    /// <param name="cancellation">The request's cancellation source.</param>
    private void FinishRequest(CancellationTokenSource cancellation)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_requestCancellation, cancellation))
            {
                _requestCancellation = null;
                _requestTask = null;
            }
        }
    }

    /// <summary>
    /// Applies a successful long-poll answer.
    /// </summary>
    /// <param name="requestGeneration">The generation the request was built for.</param>
    /// <param name="response">The answer.</param>
    private void HandleResponse(long requestGeneration, AgentWatchBatchResponse? response)
    {
        List<string> changed = [];

        lock (_gate)
        {
            if (!AcceptLocked(requestGeneration))
            {
                return;
            }

            if (response is null || response.Generation != requestGeneration)
            {
                // A mismatched generation means the answer belongs to a watch set the client no
                // longer has; acting on it would invalidate watches by identifiers that may have
                // been reused.
                ScheduleRetryLocked(requestGeneration, new NacosServerException(
                    ServerErrorCode,
                    "The Nacos server answered an agent watch with a mismatched generation."));

                return;
            }

            _consecutiveFailures = 0;
            _pendingAdditions.Clear();

            if (response.Changed && response.ChangedClientWatchIds is not null)
            {
                foreach (string clientWatchId in response.ChangedClientWatchIds)
                {
                    if (_watches.ContainsKey(clientWatchId) && _awaitingRefresh.Add(clientWatchId))
                    {
                        changed.Add(clientWatchId);
                    }
                }
            }

            DispatchLocked(requestGeneration);
        }

        if (response is not null && response.Changed)
        {
            NacosAiLog.AgentWatchHttpInvalidated(_logger, changed.Count);
        }

        foreach (string clientWatchId in changed)
        {
            _invalidated(clientWatchId);
        }
    }

    /// <summary>
    /// Applies a failed long poll.
    /// </summary>
    /// <param name="requestGeneration">The generation the request was built for.</param>
    /// <param name="exception">The failure.</param>
    private void HandleFailure(long requestGeneration, NacosException exception)
    {
        List<string> rejected = [];
        NacosException? wireFailure = null;

        lock (_gate)
        {
            if (!AcceptLocked(requestGeneration))
            {
                return;
            }

            if (exception.ErrorCode == MigrationInProgressErrorCode)
            {
                rejected.AddRange(_watches.Keys);
                _watches.Clear();
                _awaitingRefresh.Clear();
                _pendingAdditions.Clear();
                CancelRetryLocked();
            }
            else if (IsCapacity(exception) && _pendingAdditions.Count > 0)
            {
                string rejectedId = _pendingAdditions[^1];

                _pendingAdditions.RemoveAt(_pendingAdditions.Count - 1);
                _awaitingRefresh.Remove(rejectedId);
                _watches.Remove(rejectedId);
                rejected.Add(rejectedId);
                AdvanceLocked();
            }
            else if (RequiresFallback(exception)
                || ++_consecutiveFailures >= _failuresBeforeFallback)
            {
                _available = false;
                CancelRequestLocked();
                CancelRetryLocked();
                wireFailure = exception;
            }
            else
            {
                ScheduleRetryLocked(requestGeneration, exception);
            }

            if (wireFailure is null && rejected.Count == 0)
            {
                NacosAiLog.AgentWatchHttpFailed(
                    _logger,
                    exception.ErrorCode,
                    _consecutiveFailures,
                    false);
            }
        }

        if (wireFailure is not null)
        {
            NacosAiLog.AgentWatchHttpFailed(
                _logger,
                wireFailure.ErrorCode,
                _consecutiveFailures,
                true);

            _wireUnavailable(wireFailure);
            return;
        }

        foreach (string clientWatchId in rejected)
        {
            _unavailable(clientWatchId, exception);
        }
    }

    /// <summary>
    /// Determines whether an answer still describes the current watch set.
    /// </summary>
    /// <param name="requestGeneration">The generation the request was built for.</param>
    /// <returns><see langword="true"/> when the answer may be applied.</returns>
    private bool AcceptLocked(long requestGeneration) =>
        !_closed && _available && _generation == requestGeneration;

    /// <summary>
    /// Schedules another long poll after a transient failure.
    /// </summary>
    /// <param name="requestGeneration">The generation the failed request was built for.</param>
    /// <param name="exception">The failure.</param>
    private void ScheduleRetryLocked(long requestGeneration, NacosException exception)
    {
        if (_closed || !_available || _generation != requestGeneration || _retryTask is not null)
        {
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);

        _retryCancellation = cancellation;
        Task task = RunRetryAsync(requestGeneration, cancellation);

        if (ReferenceEquals(_retryCancellation, cancellation))
        {
            _retryTask = task;
        }

        NacosAiLog.AgentWatchHttpRetryScheduled(
            _logger,
            exception.ErrorCode,
            _consecutiveFailures);
    }

    /// <summary>
    /// Waits the retry delay and dispatches the same generation again.
    /// </summary>
    /// <param name="generation">The generation to retry.</param>
    /// <param name="cancellation">The source that cancels the wait.</param>
    /// <returns>A task that completes when the retry was dispatched or cancelled.</returns>
    private async Task RunRetryAsync(long generation, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(_retryDelay, _timeProvider, cancellation.Token).ConfigureAwait(false);

            lock (_gate)
            {
                if (!ReferenceEquals(_retryCancellation, cancellation))
                {
                    return;
                }

                _retryCancellation = null;
                _retryTask = null;
                DispatchLocked(generation);
            }
        }
        catch (OperationCanceledException)
        {
            // The watch set changed or the transport was shut down.
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_retryCancellation, cancellation))
                {
                    _retryCancellation = null;
                    _retryTask = null;
                }
            }

            cancellation.Dispose();
        }
    }

    /// <summary>
    /// Cancels the in-flight request.
    /// </summary>
    private void CancelRequestLocked()
    {
        _requestCancellation?.Cancel();
        _requestCancellation = null;
        _requestTask = null;
    }

    /// <summary>
    /// Cancels a scheduled retry.
    /// </summary>
    private void CancelRetryLocked()
    {
        _retryCancellation?.Cancel();
        _retryCancellation = null;
        _retryTask = null;
    }

    /// <summary>
    /// Determines whether a failure means the whole binding has to be abandoned.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <returns><see langword="true"/> when retrying the binding cannot help.</returns>
    private static bool RequiresFallback(NacosException exception) => exception.ErrorCode is
        NotFoundErrorCode or ResourceNotFoundErrorCode or NotImplementedErrorCode or NoHandlerErrorCode
        or NoRightErrorCode or InvalidParamErrorCode or ClientInvalidParamErrorCode
        || IsCapacity(exception);

    /// <summary>
    /// Determines whether a failure is the capacity rejection.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <returns><see langword="true"/> when the server rejected the set for capacity.</returns>
    private static bool IsCapacity(NacosException exception) =>
        exception is NacosAgentWatchCapacityException;

    /// <summary>
    /// One watch registered on the binding.
    /// </summary>
    private sealed class WireWatch
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="WireWatch"/> class.
        /// </summary>
        /// <param name="clientWatchId">The watch identifier.</param>
        /// <param name="request">The discovery the watch follows.</param>
        /// <param name="fingerprint">The fingerprint of the state the caller holds.</param>
        public WireWatch(
            string clientWatchId,
            NacosAgentDiscoveryRequest request,
            string fingerprint)
        {
            ClientWatchId = clientWatchId;
            Request = request;
            Fingerprint = fingerprint;
        }

        /// <summary>Gets the watch identifier.</summary>
        public string ClientWatchId { get; }

        /// <summary>Gets or sets the discovery the watch follows.</summary>
        public NacosAgentDiscoveryRequest Request { get; set; }

        /// <summary>Gets or sets the fingerprint of the state the caller holds.</summary>
        public string Fingerprint { get; set; }
    }
}
