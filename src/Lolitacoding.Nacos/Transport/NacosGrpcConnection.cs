using System.Diagnostics;
using Lolitacoding.Nacos.Auth;
using Lolitacoding.Nacos.Diagnostics;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Generated;
using Lolitacoding.Nacos.Protocol.Messages;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Transport;

/// <summary>
/// Manages the gRPC connection that carries the traffic of one Nacos module to one server at a
/// time, including the handshake, server-initiated pushes, health checking and reconnection.
/// </summary>
/// <remarks>
/// <para>
/// A connection serves exactly one <see cref="NacosConnectionModule"/>. The module is sent as the
/// <c>module</c> label of the connection setup request, and Nacos uses it to route the connection
/// to the manager that owns it — the naming, lock and AI servers all discard connections that did
/// not declare them. Features therefore get their own connection instead of sharing one.
/// </para>
/// <para>
/// The handshake order is fixed by the protocol and is not negotiable: a unary
/// <see cref="ServerCheckRequest"/>, then the bidirectional stream is opened, then
/// <see cref="ConnectionSetupRequest"/> is written as the first frame, and finally the server
/// acknowledges with <see cref="SetupAckRequest"/>. The acknowledgement is only sent when the
/// setup request carried a non-null ability table, so this client always advertises one.
/// </para>
/// <para>
/// All timing flows through <see cref="TimeProvider"/> and all randomness through an injected
/// <see cref="Random"/>, so reconnect and health check behaviour is fully drivable in tests.
/// </para>
/// </remarks>
internal sealed class NacosGrpcConnection : IAsyncDisposable
{
    private readonly NacosOptions _options;
    private readonly NacosConnectionModule _module;
    private readonly IReadOnlyDictionary<string, string>? _extraLabels;
    private readonly IReadOnlyList<NacosServerAddress> _servers;
    private readonly INacosChannelFactory _channelFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly NacosBackoffPolicy _backoff;
    private readonly Auth.INacosAuthProvider? _authProvider;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _reconnectGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    private INacosChannel? _channel;
    private INacosDuplexStream? _stream;
    private Task? _readLoop;
    private int _serverIndex;
    private NacosTransportState _state = NacosTransportState.Disconnected;
    private string? _connectionId;
    private bool _disposed;
    private IReadOnlyDictionary<string, bool>? _serverAbilities;

    /// <summary>
    /// Identifies the current connection attempt.
    /// </summary>
    /// <remarks>
    /// A dropped stream reports its own failure from a background read loop, which can outlive
    /// the connection it belonged to. Comparing this value lets such a report be ignored when it
    /// concerns a connection that has already been replaced, instead of tearing down the fresh
    /// connection that replaced it.
    /// </remarks>
    private int _connectionEpoch;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosGrpcConnection"/> class.
    /// </summary>
    /// <param name="options">The client options.</param>
    /// <param name="module">The module this connection serves.</param>
    /// <param name="servers">The parsed server list to fail over across.</param>
    /// <param name="channelFactory">Creates channels; substituted in tests.</param>
    /// <param name="timeProvider">The clock and timer source.</param>
    /// <param name="logger">The logger for connection diagnostics.</param>
    /// <param name="random">The random source used for reconnect jitter.</param>
    /// <param name="authProvider">
    /// Supplies the access token presented on outgoing requests. Defaults to anonymous access.
    /// </param>
    /// <param name="extraLabels">
    /// Additional connection labels, or <see langword="null"/> for none. They are merged over the
    /// standard labels, so a module can add its own without this transport knowing about it.
    /// </param>
    public NacosGrpcConnection(
        NacosOptions options,
        NacosConnectionModule module,
        IReadOnlyList<NacosServerAddress> servers,
        INacosChannelFactory channelFactory,
        TimeProvider timeProvider,
        ILogger logger,
        Random? random = null,
        Auth.INacosAuthProvider? authProvider = null,
        IReadOnlyDictionary<string, string>? extraLabels = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(channelFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        if (servers.Count == 0)
        {
            throw new ArgumentException(
                "At least one Nacos server address is required.", nameof(servers));
        }

        _options = options;
        _module = module;
        _extraLabels = extraLabels;
        _servers = servers;
        _channelFactory = channelFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _backoff = new NacosBackoffPolicy(options.Reconnect, timeProvider, random ?? new Random());
        _authProvider = authProvider;
        ClientVersion = BuildClientVersion();
    }

    /// <summary>Raised whenever the transport state changes.</summary>
    public event EventHandler<NacosTransportStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Raised after a connection has been re-established following a drop, so subscribers can
    /// restore the server-side state they had registered.
    /// </summary>
    /// <remarks>
    /// Nacos discards connection-scoped state — configuration listeners, service subscriptions
    /// and ephemeral instance registrations — when a connection goes away, so anything that
    /// registered such state must replay it here or it will silently stop receiving pushes.
    /// </remarks>
    public event Func<NacosTransportState, CancellationToken, Task>? Reconnected;

    /// <summary>Gets the state the transport is currently in.</summary>
    public NacosTransportState State => _state;

    /// <summary>Gets the identifier the server assigned this connection, when connected.</summary>
    public string? ConnectionId => _connectionId;

    /// <summary>Gets the address of the server currently in use, when connected.</summary>
    public NacosServerAddress? CurrentServer { get; private set; }

    /// <summary>Gets the client version string reported during the handshake.</summary>
    public string ClientVersion { get; }

    /// <summary>Gets the UTC timestamp of the last successfully answered request, in ticks.</summary>
    internal long LastActivityTicks { get; private set; }

    /// <summary>
    /// Gets the abilities the server advertised during the handshake.
    /// </summary>
    /// <remarks>
    /// Empty when the server did not acknowledge the handshake, which happens on servers older
    /// than ability negotiation. Callers must therefore treat an absent key as "unknown" rather
    /// than "unsupported".
    /// </remarks>
    public IReadOnlyDictionary<string, bool> ServerAbilities =>
        _serverAbilities ?? (IReadOnlyDictionary<string, bool>)new Dictionary<string, bool>(0);

    /// <summary>
    /// Establishes the connection and completes the handshake.
    /// </summary>
    /// <param name="ct">A token that cancels the attempt.</param>
    /// <returns>A task that completes once the connection is usable.</returns>
    /// <exception cref="NacosConnectionException">
    /// No configured server could be reached.
    /// </exception>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            await EstablishAsync(isReconnect: false, ct).ConfigureAwait(false);
        }
        catch (NacosConnectionException) when (_options.Optional && !ct.IsCancellationRequested)
        {
            // The caller opted into tolerating an unavailable server at startup. Recovery is
            // started in the background so the client becomes usable once the server returns,
            // rather than remaining permanently disconnected.
            _ = Task.Run(() => HandleConnectionLossAsync(_shutdown.Token), CancellationToken.None);
        }
    }

    /// <summary>
    /// Sends a request and returns the decoded response.
    /// </summary>
    /// <typeparam name="TResponse">The expected response type.</typeparam>
    /// <param name="request">The request to send.</param>
    /// <param name="ct">A token that cancels the call.</param>
    /// <returns>The decoded response.</returns>
    /// <exception cref="NacosServerException">The server answered with a failure result code.</exception>
    /// <exception cref="NacosConnectionException">The connection was lost while sending.</exception>
    public async Task<TResponse> RequestAsync<TResponse>(NacosRequest request, CancellationToken ct = default)
        where TResponse : NacosResponse
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        request.RequestId ??= Guid.NewGuid().ToString("N");

        try
        {
            return await SendAsync<TResponse>(request, ct).ConfigureAwait(false);
        }
        catch (NacosServerException ex) when (IsTokenRejection(ex.ErrorCode) && _authProvider is not null)
        {
            // The server rejected the token. That has two causes worth distinguishing: it simply
            // expired, or it was signed by a different key after a cluster node restarted. Both
            // are fixed by obtaining a fresh token, so one retry covers them — and only one, so
            // a genuinely unauthorized identity fails fast instead of looping.
            NacosAuthLog.TokenRejected(_logger, CurrentServer?.Original ?? "unknown", ex.Message);
            _authProvider.Invalidate();

            return await SendAsync<TResponse>(request, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Determines whether an error code means the access token was not accepted.
    /// </summary>
    /// <param name="errorCode">The code the server reported.</param>
    /// <returns><see langword="true"/> when the token should be refreshed and the request retried.</returns>
    /// <remarks>
    /// Two codes must be recognized because the gRPC and HTTP layers of Nacos report the same
    /// condition differently: the gRPC path propagates <c>NacosException.NO_RIGHT</c> (403), while
    /// the OpenAPI envelope carries <c>ErrorCode.ACCESS_DENIED</c> (10001). A missing token and an
    /// expired token produce the same codes, so both are treated as "obtain a fresh token".
    /// </remarks>
    private static bool IsTokenRejection(int errorCode) => errorCode is UnauthorizedErrorCode or AccessDeniedErrorCode;

    /// <summary>
    /// The gRPC error code Nacos returns when the caller is not permitted to perform an operation.
    /// </summary>
    private const int UnauthorizedErrorCode = 403;

    /// <summary>
    /// The OpenAPI error code Nacos returns when access is denied.
    /// </summary>
    private const int AccessDeniedErrorCode = 10001;

    /// <summary>
    /// Sends one attempt of a request, attaching the current access token.
    /// </summary>
    /// <typeparam name="TResponse">The expected response type.</typeparam>
    /// <param name="request">The request to send.</param>
    /// <param name="ct">A token that cancels the call.</param>
    /// <returns>The decoded response.</returns>
    private async Task<TResponse> SendAsync<TResponse>(NacosRequest request, CancellationToken ct)
        where TResponse : NacosResponse
    {
        if (_authProvider is not null)
        {
            string? token = await _authProvider.GetAccessTokenAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(token))
            {
                // Nacos reads the token from a header named literally "accessToken", not from an
                // Authorization bearer header, on both the gRPC and the HTTP paths.
                request.SetHeader(ProtocolHeaders.AccessToken, token);
            }
            else
            {
                // A previous attempt may have attached a token that has since been invalidated, so
                // the header is cleared rather than left to be re-sent.
                request.RemoveHeader(ProtocolHeaders.AccessToken);
            }
        }

        INacosChannel channel = _channel
            ?? throw new NacosConnectionException("The Nacos connection has not been established yet.");

        string requestType = PayloadTypeRegistry.GetTypeName(request);
        long startTimestamp = _timeProvider.GetTimestamp();

        using Activity? activity = NacosDiagnostics.ActivitySource.StartActivity(
            NacosDiagnostics.RequestActivityName, ActivityKind.Client);
        activity?.SetTag(NacosDiagnostics.RequestTypeTag, requestType);

        try
        {
            activity?.SetTag(NacosDiagnostics.ServerAddressTag, CurrentServer?.Original);

            Payload payload = PayloadCodec.Encode(request, _options.LocalClientIp);

            Payload responsePayload;
            try
            {
                responsePayload = await channel.UnaryAsync(payload, ct).ConfigureAwait(false);
            }
            catch (NacosConnectionException)
            {
                // A dropped connection must not leave the caller without recovery: hand off to the
                // reconnect path, then let the caller observe the failure and retry.
                await HandleConnectionLossAsync(_shutdown.Token).ConfigureAwait(false);
                throw;
            }

            // Mark the connection as recently used so the health checker does not probe a
            // connection that is demonstrably serving traffic.
            Touch();

            TResponse? response = PayloadCodec.Decode<TResponse>(responsePayload);

            if (response is null)
            {
                // The server reports transport-level failures as ErrorResponse regardless of the
                // request type, so surface that as a server exception rather than a decode failure.
                ErrorResponse? error = PayloadCodec.Decode<ErrorResponse>(responsePayload);
                if (error is not null && !error.Success)
                {
                    throw new NacosServerException(
                        error.ErrorCode,
                        error.Message ?? "The Nacos server returned an unspecified error.");
                }

                throw new NacosServerException(
                    string.Concat(
                        "Expected a '", typeof(TResponse).Name, "' response but the server sent '",
                        PayloadCodec.ReadTypeName(responsePayload), "'."));
            }

            response.RequestId ??= request.RequestId;

            if (!response.Success)
            {
                throw new NacosServerException(
                    response.ErrorCode,
                    response.Message ?? "The Nacos server reported a failure without a message.");
            }

            RecordRequest(requestType, NacosDiagnostics.OutcomeSuccess, startTimestamp);
            activity?.SetStatus(ActivityStatusCode.Ok);

            return response;
        }
        catch (Exception ex)
        {
            RecordRequest(requestType, NacosDiagnostics.OutcomeError, startTimestamp);

            if (ex is not OperationCanceledException)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            }

            throw;
        }
    }

    /// <summary>
    /// Records the request count and duration of one completed send attempt.
    /// </summary>
    /// <param name="requestType">The wire type name of the request.</param>
    /// <param name="outcome">Whether the attempt succeeded or failed.</param>
    /// <param name="startTimestamp">The timestamp the attempt started at.</param>
    /// <remarks>
    /// Both instruments share the request type tag so a consumer can correlate a spike in failures
    /// with a change in latency. The tags are built into a <see cref="TagList"/> to avoid a heap
    /// allocation on every request.
    /// </remarks>
    private void RecordRequest(string requestType, string outcome, long startTimestamp)
    {
        NacosMetrics.Requests.Add(
            1,
            new TagList
            {
                { NacosDiagnostics.RequestTypeTag, requestType },
                { NacosDiagnostics.OutcomeTag, outcome },
            });

        NacosMetrics.RequestDuration.Record(
            _timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds,
            new TagList
            {
                { NacosDiagnostics.RequestTypeTag, requestType },
            });
    }

    /// <summary>
    /// Records one server-initiated push.
    /// </summary>
    /// <param name="typeName">The wire type name of the pushed request.</param>
    private static void RecordPush(string typeName) =>
        NacosMetrics.Pushes.Add(
            1,
            new TagList
            {
                { NacosDiagnostics.PushTypeTag, typeName },
            });

    /// <summary>
    /// Raised when the server pushes a request this connection does not handle itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nacos multiplexes every feature over one bi-directional stream, so a push that arrives here
    /// belongs to whichever domain client registered for it — a configuration change, a service
    /// change, and so on. Returning a response lets the client answer the server; returning
    /// <see langword="null"/> means no response is due.
    /// </para>
    /// <para>
    /// Handlers run on the read loop, so they must return promptly and must not throw. Doing the
    /// real work inline would stall every other message on the stream behind them.
    /// </para>
    /// </remarks>
    internal event Func<NacosRequest, CancellationToken, Task<NacosResponse?>>? ServerRequestReceived;

    /// <summary>
    /// Sends a response the client owes the server for a server-initiated push.
    /// </summary>
    /// <param name="response">The response to send.</param>
    /// <param name="ct">A token that cancels the write.</param>
    /// <returns>A task that completes once the frame is written.</returns>
    internal async Task SendResponseAsync(NacosResponse response, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        INacosDuplexStream? stream = _stream;
        if (stream is null)
        {
            return;
        }

        try
        {
            await stream.WriteAsync(PayloadCodec.Encode(response), ct).ConfigureAwait(false);
        }
        catch (NacosConnectionException)
        {
            // The push reply is best-effort; the reconnect path re-establishes the connection.
        }
    }

    /// <summary>
    /// Runs one health check round, reconnecting when the current connection has gone stale.
    /// </summary>
    /// <param name="ct">A token that cancels the check.</param>
    /// <returns>A task that completes once the check or any resulting reconnect finishes.</returns>
    internal async Task HealthCheckAsync(CancellationToken ct = default)
    {
        if (_disposed || _state != NacosTransportState.Connected)
        {
            return;
        }

        TimeSpan idle = _timeProvider.GetUtcNow() - new DateTimeOffset(LastActivityTicks, TimeSpan.Zero);
        if (idle < _options.HealthCheckInterval)
        {
            return;
        }

        bool healthy = false;
        try
        {
            await RequestAsync<HealthCheckResponse>(new HealthCheckRequest(), ct).ConfigureAwait(false);
            healthy = true;
        }
        catch (NacosException)
        {
            // Any failure means the connection cannot be trusted; fall through and reconnect.
        }

        if (healthy)
        {
            Touch();
            return;
        }

        SetState(NacosTransportState.Unhealthy);
        await HandleConnectionLossAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SetState(NacosTransportState.Shutdown);

        await _shutdown.CancelAsync().ConfigureAwait(false);
        TearDown();

        _shutdown.Dispose();
        _connectGate.Dispose();
        _reconnectGate.Dispose();
    }

    /// <summary>
    /// Establishes a connection, recording a span for the attempt.
    /// </summary>
    /// <param name="isReconnect">Whether this attempt follows a lost connection.</param>
    /// <param name="ct">A token that cancels the attempt.</param>
    /// <returns>A task that completes once connected.</returns>
    /// <remarks>
    /// The span covers failover across every configured server, so its duration is the time the
    /// caller actually waited for a usable connection rather than the time spent on one server.
    /// </remarks>
    private async Task EstablishAsync(bool isReconnect, CancellationToken ct)
    {
        using Activity? activity = NacosDiagnostics.ActivitySource.StartActivity(
            NacosDiagnostics.ConnectActivityName, ActivityKind.Client);
        activity?.SetTag(NacosDiagnostics.ReconnectTag, isReconnect);

        try
        {
            await EstablishCoreAsync(isReconnect, ct).ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            }

            throw;
        }
    }

    /// <summary>
    /// Establishes a connection, trying each configured server in turn.
    /// </summary>
    /// <param name="isReconnect">Whether this attempt follows a lost connection.</param>
    /// <param name="ct">A token that cancels the attempt.</param>
    /// <returns>A task that completes once connected.</returns>
    private async Task EstablishCoreAsync(bool isReconnect, CancellationToken ct)
    {
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state == NacosTransportState.Connected)
            {
                return;
            }

            SetState(NacosTransportState.Connecting);

            var failures = new List<Exception>();

            for (int attempt = 0; attempt < _servers.Count; attempt++)
            {
                int index = (_serverIndex + attempt) % _servers.Count;
                NacosServerAddress address = _servers[index];

                try
                {
                    await ConnectToAsync(address, ct).ConfigureAwait(false);

                    _serverIndex = index;
                    SetState(NacosTransportState.Connected, address.Original);

                    if (isReconnect)
                    {
                        await RaiseReconnectedAsync(ct).ConfigureAwait(false);
                    }

                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    SetState(NacosTransportState.Disconnected);
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                    LogConnectFailed(address.Original, ex.Message);
                }
            }

            SetState(NacosTransportState.Disconnected);

            throw new NacosConnectionException(
                string.Concat(
                    "None of the ", _servers.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    " configured Nacos server(s) could be reached."),
                failures.Count > 0 ? new AggregateException(failures) : null);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>
    /// Performs the handshake against one specific server.
    /// </summary>
    /// <param name="address">The server to connect to.</param>
    /// <param name="ct">A token that cancels the attempt.</param>
    /// <returns>A task that completes once the handshake succeeded.</returns>
    private async Task ConnectToAsync(NacosServerAddress address, CancellationToken ct)
    {
        INacosChannel channel = _channelFactory.Create(address);
        bool published = false;

        try
        {
            // Step 1: unary liveness probe, which also returns the connection identifier.
            Payload checkResponse = await channel
                .UnaryAsync(PayloadCodec.Encode(new ServerCheckRequest(), _options.LocalClientIp), ct)
                .ConfigureAwait(false);

            ServerCheckResponse? check = PayloadCodec.Decode<ServerCheckResponse>(checkResponse);
            if (check is null || !check.Success)
            {
                throw new NacosConnectionException(
                    string.Concat(
                        "The server at ", address.Original,
                        " did not answer the handshake probe correctly."));
            }

            // Step 2: open the notification stream.
            INacosDuplexStream stream = channel.OpenStream(_shutdown.Token);

            // Step 3: register the connection as the first frame on that stream. The ability
            // table must be non-null, otherwise the server sends no acknowledgement and the
            // client cannot tell when the connection became usable.
            var setup = new ConnectionSetupRequest
            {
                ClientVersion = ClientVersion,
                Tenant = _options.Namespace,
                Labels = BuildLabels(),
                AbilityTable = BuildAbilityTable(),
            };

            await stream.WriteAsync(PayloadCodec.Encode(setup, _options.LocalClientIp), ct)
                .ConfigureAwait(false);

            _channel = channel;
            _stream = stream;
            _connectionId = check.ConnectionId;
            CurrentServer = address;
            published = true;
            Touch();

            int epoch = ++_connectionEpoch;

            // Step 4: pump the stream. The read loop resolves the handshake when the server
            // acknowledges, and reports the failure if the stream dies.
            var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _readLoop = Task.Run(
                () => ReadLoopAsync(stream, handshake, epoch, _shutdown.Token),
                CancellationToken.None);

            if (check.SupportAbilityNegotiation)
            {
                // The acknowledgement is only produced when the client advertised abilities, so
                // waiting for it is safe exactly when the server supports negotiation.
                using var timeout = new CancellationTokenSource(_options.ConnectionTimeout, _timeProvider);
                await handshake.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }

            Touch();
        }
        catch
        {
            if (published)
            {
                TearDown();
            }
            else
            {
                channel.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Reads pushed frames until the stream ends, answering the ones that expect a reply.
    /// </summary>
    /// <param name="stream">The stream to read.</param>
    /// <param name="handshake">Resolved when the server acknowledges the setup.</param>
    /// <param name="epoch">Identifies the connection this loop belongs to.</param>
    /// <param name="ct">A token that ends the loop.</param>
    /// <returns>A task that completes when the stream ends.</returns>
    private async Task ReadLoopAsync(
        INacosDuplexStream stream,
        TaskCompletionSource handshake,
        int epoch,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Payload? payload = await stream.ReadAsync(ct).ConfigureAwait(false);

                if (payload is null)
                {
                    break;
                }

                Touch();

                object? message = PayloadCodec.DecodeAny(payload);

                switch (message)
                {
                    case SetupAckRequest ack:
                        _serverAbilities = ack.AbilityTable;
                        handshake.TrySetResult();
                        break;

                    case ClientDetectionRequest detection:
                        // Liveness probe: answer promptly or the server drops the connection.
                        await SendResponseAsync(
                            new ClientDetectionResponse { RequestId = detection.RequestId }, ct)
                            .ConfigureAwait(false);
                        break;

                    case ConnectResetRequest reset:
                        await HandleConnectResetAsync(reset, ct).ConfigureAwait(false);
                        break;

                    case PushAckRequest:
                        // The server acknowledged a client push; nothing further is required.
                        break;

                    case null:
                        LogUnknownPush(PayloadCodec.ReadTypeName(payload));
                        break;

                    default:
                        // Anything else is a domain push. It is dispatched to the registered
                        // handlers rather than handled here, because the transport has no
                        // knowledge of configuration or naming semantics.
                        await DispatchServerRequestAsync((NacosRequest)message, ct).ConfigureAwait(false);
                        break;
                }
            }

            handshake.TrySetException(
                new NacosConnectionException(
                    "The Nacos notification stream ended before the handshake completed."));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            handshake.TrySetCanceled(ct);
        }
        catch (Exception ex)
        {
            handshake.TrySetException(ex);
        }
        finally
        {
            // Only the read loop of the connection that is still current may report a loss.
            // A loop belonging to a connection that has already been replaced must stay silent,
            // otherwise it would tear down the healthy connection that succeeded it.
            if (!ct.IsCancellationRequested && !_disposed && Volatile.Read(ref _connectionEpoch) == epoch)
            {
                SetState(NacosTransportState.Disconnected);
                await HandleConnectionLossAsync(_shutdown.Token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Routes a server-initiated request to the registered handlers.
    /// </summary>
    /// <param name="request">The request that arrived.</param>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>A task that completes once every handler ran and the acknowledgement was sent.</returns>
    /// <remarks>
    /// <para>
    /// A handler that throws is isolated so that one broken feature cannot stop the read loop and
    /// take every other feature on the connection down with it.
    /// </para>
    /// <para>
    /// The first response any handler produces is written back, and the remaining handlers still
    /// run. That is what the official client does: it asks every registered handler and answers
    /// with the first non-null reply, so a push that several features observe is acknowledged
    /// exactly once, by the feature that recognised it.
    /// </para>
    /// </remarks>
    private async Task DispatchServerRequestAsync(NacosRequest request, CancellationToken ct)
    {
        Func<NacosRequest, CancellationToken, Task<NacosResponse?>>? handlers = ServerRequestReceived;

        if (handlers is null)
        {
            string droppedTypeName = PayloadTypeRegistry.GetTypeName(request);
            RecordPush(droppedTypeName);
            LogUnhandledPush(droppedTypeName);
            return;
        }

        string typeName = PayloadTypeRegistry.GetTypeName(request);
        RecordPush(typeName);

        NacosResponse? response = null;

        foreach (Func<NacosRequest, CancellationToken, Task<NacosResponse?>> handler in
            handlers.GetInvocationList().Cast<Func<NacosRequest, CancellationToken, Task<NacosResponse?>>>())
        {
            try
            {
                // Handing the work off rather than awaiting it inline keeps a slow feature from
                // blocking the stream; ordering within the feature is preserved by its own queue.
                NacosResponse? handlerResponse = await handler(request, ct).ConfigureAwait(false);
                response ??= handlerResponse;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogPushHandlerFailed(typeName, ex.Message);
            }
        }

        if (response is not null)
        {
            // The server correlates the acknowledgement by request id, and the transport owns that
            // correlation: a handler only says what kind of reply is due, never which push it is for.
            response.RequestId = request.RequestId;
            await SendResponseAsync(response, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Answers a server redirect and moves to the indicated server or the next one in the list.
    /// </summary>
    /// <param name="reset">The redirect request.</param>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>A task that completes once the redirect was answered.</returns>
    private async Task HandleConnectResetAsync(ConnectResetRequest reset, CancellationToken ct)
    {
        await SendResponseAsync(new ConnectResetResponse { RequestId = reset.RequestId }, ct)
            .ConfigureAwait(false);

        // Move to the server the server recommended, or simply advance to the next one.
        if (!string.IsNullOrEmpty(reset.ServerIp))
        {
            for (int i = 0; i < _servers.Count; i++)
            {
                if (string.Equals(_servers[i].Host, reset.ServerIp, StringComparison.OrdinalIgnoreCase))
                {
                    _serverIndex = i;
                    return;
                }
            }
        }

        _serverIndex = (_serverIndex + 1) % _servers.Count;
    }

    /// <summary>
    /// Tears the current connection down and re-establishes it with exponential backoff.
    /// </summary>
    /// <param name="ct">A token that cancels the recovery.</param>
    /// <returns>A task that completes once reconnected, or immediately when shutting down.</returns>
    private async Task HandleConnectionLossAsync(CancellationToken ct)
    {
        if (_disposed || _state == NacosTransportState.Shutdown)
        {
            return;
        }

        // Only one recovery may run at a time. A caller that loses the race returns immediately:
        // the winner is already restoring the connection, and a second concurrent reconnect
        // would tear down the connection the winner just established.
        if (!await _reconnectGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (_disposed || _state == NacosTransportState.Shutdown || _shutdown.IsCancellationRequested)
            {
                return;
            }

            if (_state == NacosTransportState.Connecting)
            {
                // A reconnect is already under way; let it finish.
                return;
            }

            if (_state == NacosTransportState.Connected)
            {
                // A live connection is still in place. This happens when a stale read loop
                // reports a failure after the connection it belonged to was already replaced, so
                // the report must not tear down the healthy connection that succeeded it.
                return;
            }

            TearDown();

            int attempt = 0;
            while (!_disposed && _state != NacosTransportState.Shutdown)
            {
                attempt++;

                if (!_backoff.ShouldRetry(attempt - 1))
                {
                    LogReconnectGaveUp(attempt - 1);
                    SetState(NacosTransportState.Disconnected);
                    return;
                }

                TimeSpan delay = await _backoff.DelayAsync(attempt, ct).ConfigureAwait(false);
                LogReconnecting(attempt, delay.TotalMilliseconds);

                try
                {
                    await EstablishAsync(isReconnect: true, ct).ConfigureAwait(false);
                    LogReconnected(attempt);
                    NacosMetrics.Reconnects.Add(1);
                    return;
                }
                catch (NacosConnectionException)
                {
                    // Keep backing off; the loop decides when the attempt budget is spent.
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
            }
        }
        finally
        {
            _reconnectGate.Release();
        }
    }

    /// <summary>
    /// Releases the current stream and channel without touching the server list.
    /// </summary>
    private void TearDown()
    {
        INacosDuplexStream? stream = Interlocked.Exchange(ref _stream, null);
        INacosChannel? channel = Interlocked.Exchange(ref _channel, null);

        _connectionId = null;
        _serverAbilities = null;
        CurrentServer = null;

        stream?.Dispose();
        channel?.Dispose();
    }

    /// <summary>
    /// Raises <see cref="Reconnected"/> and waits for every subscriber to restore its state.
    /// </summary>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>A task that completes once all subscribers finished.</returns>
    private async Task RaiseReconnectedAsync(CancellationToken ct)
    {
        Delegate? handler = Reconnected;
        if (handler is null)
        {
            return;
        }

        foreach (Delegate subscriber in handler.GetInvocationList())
        {
            try
            {
                var restore = (Func<NacosTransportState, CancellationToken, Task>)subscriber;
                await restore(NacosTransportState.Connected, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One failing subscriber must not prevent the others from restoring their state.
                LogRestoreFailed(ex.Message);
            }
        }
    }

    /// <summary>Records the current instant as the last activity time.</summary>
    private void Touch() => LastActivityTicks = _timeProvider.GetUtcNow().UtcTicks;

    /// <summary>
    /// Publishes a transport state transition.
    /// </summary>
    /// <param name="state">The state being entered.</param>
    /// <param name="serverAddress">The server the change concerns, when known.</param>
    private void SetState(NacosTransportState state, string? serverAddress = null)
    {
        NacosTransportState previous = _state;
        if (previous == state)
        {
            return;
        }

        _state = state;

        string previousName = previous.ToString();
        string currentName = state.ToString();

        LogStateChanged(previousName, currentName);

        NacosMetrics.StateChanges.Add(
            1,
            new TagList
            {
                { NacosDiagnostics.ConnectionStateTag, currentName },
            });

        try
        {
            StateChanged?.Invoke(this, new NacosTransportStateChangedEventArgs(previous, state, serverAddress));
        }
        catch (Exception ex)
        {
            // A misbehaving observer must not break the transport.
            LogStateObserverFailed(ex.Message);
        }
    }

    /// <summary>
    /// Builds the labels sent with the connection setup.
    /// </summary>
    /// <returns>The labels.</returns>
    /// <remarks>
    /// The <c>module</c> label is what routes this connection to the right server-side manager, and
    /// the <c>source</c> label marks it as an SDK connection rather than a cluster one. Omitting
    /// either makes the server treat the connection as one it does not own, so a feature that
    /// registered state on it silently stops receiving pushes.
    /// </remarks>
    private Dictionary<string, string> BuildLabels()
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ProtocolHeaders.Source] = ProtocolHeaders.SourceSdk,
            [ProtocolHeaders.Module] = _module.ToLabel(),
            [ProtocolHeaders.AppName] = _options.AppName ?? "unknown",
            [ProtocolHeaders.ClientVersion] = ClientVersion,
            [ProtocolHeaders.NamespaceId] = _options.Namespace,
        };

        if (_extraLabels is not null)
        {
            foreach (KeyValuePair<string, string> label in _extraLabels)
            {
                labels[label.Key] = label.Value;
            }
        }

        return labels;
    }

    /// <summary>
    /// Builds the ability table advertised to the server.
    /// </summary>
    /// <returns>
    /// The abilities this client supports. The table is never empty and never null, because a
    /// null table suppresses the server's handshake acknowledgement.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Only abilities this client actually implements may be advertised. The table is a property of
    /// the client rather than of one module — the official client advertises the same SDK abilities
    /// on every connection — so each module's connection reports the same values. Keys are Nacos
    /// ability names, not Java enum member names.
    /// </para>
    /// <para>
    /// This transport does not implement fuzzy watch, so it is advertised as unsupported rather than
    /// claiming a capability it cannot honour. The distributed lock is implemented, so it is
    /// advertised: the server reads the flag to decide whether a lock notification may be pushed to
    /// this connection.
    /// </para>
    /// </remarks>
    private static Dictionary<string, bool> BuildAbilityTable() => new(StringComparer.Ordinal)
    {
        ["fuzzyWatch"] = false,
        ["lock"] = true,
    };

    /// <summary>
    /// Builds the client version string reported to the server.
    /// </summary>
    /// <returns>A version string in the form Nacos accepts.</returns>
    private static string BuildClientVersion()
    {
        Version? version = typeof(NacosGrpcConnection).Assembly.GetName().Version;
        return version is null ? "Nacos-DotNet" : string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Nacos-DotNet/{version.ToString(3)}");
    }

    private void LogConnectFailed(string address, string reason) =>
        NacosTransportLog.ConnectFailed(_logger, address, reason);

    private void LogUnknownPush(string typeName) =>
        NacosTransportLog.UnknownPush(_logger, typeName);

    private void LogUnhandledPush(string typeName) =>
        NacosTransportLog.UnhandledPush(_logger, typeName);

    private void LogPushHandlerFailed(string typeName, string reason) =>
        NacosTransportLog.PushHandlerFailed(_logger, typeName, reason);

    private void LogReconnecting(int attempt, double delayMs) =>
        NacosTransportLog.Reconnecting(_logger, attempt, delayMs);

    private void LogReconnected(int attempt) =>
        NacosTransportLog.Reconnected(_logger, attempt);

    private void LogReconnectGaveUp(int attempts) =>
        NacosTransportLog.ReconnectGaveUp(_logger, attempts);

    private void LogStateChanged(string previous, string current) =>
        NacosTransportLog.StateChanged(_logger, previous, current);

    private void LogRestoreFailed(string reason) =>
        NacosTransportLog.RestoreFailed(_logger, reason);

    private void LogStateObserverFailed(string reason) =>
        NacosTransportLog.StateObserverFailed(_logger, reason);
}
