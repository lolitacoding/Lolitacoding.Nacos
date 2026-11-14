using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.AI;

/// <summary>
/// The RAD agent registry, served over the AI registry connection or the client OpenAPI.
/// </summary>
/// <remarks>
/// <para>
/// This is the agent contract Nacos 3.3 adds: an agent is a versioned resource with metadata, call
/// interfaces and runtime endpoints, published and searched through the registry's own model rather
/// than as an A2A document. It is a different surface from the card registry, not a newer version of
/// it — a card still advertises an agent to other agents, and this is how an agent is managed.
/// </para>
/// <para>
/// Which transport answers is <see cref="NacosAIOptions.TransportMode"/>, with
/// <see cref="NacosAIOptions.AgentTransportMode"/> overriding it for this registry. A publish may not
/// fall back to the other transport, because the attempt that failed may already have created the
/// version; a search and a discovery are reads and may. In <see cref="NacosAITransportMode.Auto"/> a
/// publish uses HTTP only while gRPC is known to be unreachable.
/// </para>
/// <para>
/// Endpoint registration stays on gRPC whatever the transport says. The HTTP endpoint contract gives
/// a publication a heartbeat and a TTL instead of the connection's lifetime, which is a separate
/// piece of work; the gRPC endpoints remain Naming instances owned by the connection.
/// </para>
/// </remarks>
internal sealed class NacosAgentRegistryClient : INacosAgentRegistry, IAsyncDisposable
{
    /// <summary>The error code Nacos uses for a feature a server does not implement.</summary>
    private const int NotImplementedErrorCode = 501;

    /// <summary>The error code Nacos uses for a request the client could not determine how to send.</summary>
    private const int ServerErrorCode = 500;

    /// <summary>The client OpenAPI path that publishes and discovers one agent.</summary>
    private const string AgentPath = "/v3/client/ai/agents";

    /// <summary>The client OpenAPI path that searches the agent catalog.</summary>
    private const string AgentSearchPath = "/v3/client/ai/agents/search";

    private readonly NacosAIClient _owner;
    private readonly NacosOptions _options;
    private readonly NacosAIHttpClient _http;
    private readonly ILogger<NacosAgentRegistryClient> _logger;
    private readonly NacosAgentWatchManager _watch;
    private readonly NacosAITransportFallback _fallback;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosAgentRegistryClient"/> class.
    /// </summary>
    /// <param name="owner">The AI client that owns the connection.</param>
    /// <param name="options">The client options.</param>
    /// <param name="timeProvider">The clock used for retry and timeout timing.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="http">The HTTP client the agent registry is published and read over.</param>
    /// <param name="random">The source of retry jitter, or <see langword="null"/> for a shared one.</param>
    /// <param name="httpWatch">
    /// The HTTP batch watch binding, or <see langword="null"/> when the AI client has no HTTP
    /// transport to offer.
    /// </param>
    public NacosAgentRegistryClient(
        NacosAIClient owner,
        NacosOptions options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        NacosAIHttpClient http,
        Random? random = null,
        INacosAgentHttpWatchBinding? httpWatch = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(http);

        _owner = owner;
        _options = options;
        _http = http;
        _logger = loggerFactory.CreateLogger<NacosAgentRegistryClient>();
        _fallback = new NacosAITransportFallback(timeProvider);
        _watch = new NacosAgentWatchManager(
            owner,
            options,
            timeProvider,
            loggerFactory.CreateLogger<NacosAgentWatchManager>(),
            random,
            httpWatch);
    }

    /// <summary>
    /// Gets the watch manager that the AI client routes pushed hints to.
    /// </summary>
    /// <remarks>
    /// The manager is exposed for the AI client rather than reached through this type, because the
    /// pushes arrive on the connection the AI client owns: the client needs to hand them to the
    /// manager without a caller being involved.
    /// </remarks>
    public NacosAgentWatchManager Watch => _watch;

    /// <inheritdoc />
    public async Task<NacosAgentVersionDetail> PublishAgentAsync(
        NacosAgentPublishRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentName, nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Version, nameof(request));

        NacosAITransportMode mode = EffectiveTransport;

        if (mode == NacosAITransportMode.Http
            || (mode == NacosAITransportMode.Auto && !_fallback.GrpcIsWorthTrying()))
        {
            return await PublishOverHttpAsync(request, ct).ConfigureAwait(false);
        }

        try
        {
            NacosGrpcConnection connection = await ConnectAsync(mode, ct).ConfigureAwait(false);

            var rpcRequest = new AgentPublishRpcRequest
            {
                NamespaceId = _options.Namespace,
                PublishRequest = request,
            };

            AgentPublishRpcResponse response =
                await connection.RequestAsync<AgentPublishRpcResponse>(rpcRequest, ct).ConfigureAwait(false);

            NacosAgentVersionDetail? detail = response.VersionDetail;

            if (detail is null)
            {
                // A refused publish is a failure result code, which the transport has already turned
                // into an exception; reaching here means the reply did not match the contract.
                NacosAILog.AgentPublishDetailMissing(_logger, request.AgentName!, request.Version);

                throw new NacosServerException(
                    string.Concat(
                        "The Nacos server published agent '",
                        request.AgentName,
                        "' but returned no version detail."));
            }

            NacosAILog.AgentPublished(_logger, request.AgentName!, detail.Version, detail.Status);

            return detail;
        }
        catch (NacosException ex) when (
            mode == NacosAITransportMode.Auto
            && ex is NacosConnectionException or NacosTimeoutException)
        {
            // Deliberately not retried over HTTP: the attempt may have reached the server and created
            // the version, and a second publish would then either conflict with it or publish twice.
            // The window is still recorded, so the next operation does not pay the same failed
            // connect before choosing HTTP.
            _fallback.RecordGrpcFailure();

            NacosAILog.AiReleaseTransportUnavailable(_logger, "RAD agent", ex.Message);

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<NacosAgentPage<NacosAgentSummary>> SearchAgentsAsync(
        NacosAgentSearchRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        NacosAITransportMode mode = EffectiveTransport;

        if (mode == NacosAITransportMode.Http
            || (mode == NacosAITransportMode.Auto && !_fallback.GrpcIsWorthTrying()))
        {
            return await SearchOverHttpAsync(request, ct).ConfigureAwait(false);
        }

        try
        {
            NacosGrpcConnection connection = await ConnectAsync(mode, ct).ConfigureAwait(false);

            var rpcRequest = new AgentSearchRpcRequest
            {
                NamespaceId = _options.Namespace,
                SearchRequest = request,
            };

            AgentSearchResponse response =
                await connection.RequestAsync<AgentSearchResponse>(rpcRequest, ct).ConfigureAwait(false);

            NacosAgentPage<NacosAgentSummary>? page = response.Page;

            if (page is null)
            {
                throw new NacosServerException(
                    "The Nacos server answered a search but returned no page.");
            }

            return page;
        }
        catch (NacosException ex) when (
            mode == NacosAITransportMode.Auto
            && ex is NacosConnectionException or NacosTimeoutException)
        {
            // A search changes nothing, so repeating it over HTTP cannot duplicate an effect.
            _fallback.RecordGrpcFailure();

            NacosAILog.AiTransportFallback(_logger, "RAD agent", ex.Message);

            return await SearchOverHttpAsync(request, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<NacosAgentDiscoveryResult> DiscoverAgentAsync(
        NacosAgentReference reference,
        NacosAgentDiscoveryFilter? filter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.AgentName, nameof(reference));

        NacosAITransportMode mode = EffectiveTransport;

        if (mode == NacosAITransportMode.Http
            || (mode == NacosAITransportMode.Auto && !_fallback.GrpcIsWorthTrying()))
        {
            return await DiscoverOverHttpAsync(reference, filter, ct).ConfigureAwait(false);
        }

        try
        {
            NacosGrpcConnection connection = await ConnectAsync(mode, ct).ConfigureAwait(false);

            var discovery = new NacosAgentDiscoveryRequest
            {
                NamespaceId = _options.Namespace,
                Reference = reference,
                Filter = filter,
            };

            var rpcRequest = new AgentDiscoveryRpcRequest { DiscoveryRequest = discovery };

            AgentDiscoveryResponse response =
                await connection.RequestAsync<AgentDiscoveryResponse>(rpcRequest, ct).ConfigureAwait(false);

            NacosAgentDiscoveryResult? result = response.DiscoveryResult;

            if (result is null)
            {
                NacosAILog.AgentDiscoveryDetailMissing(
                    _logger, reference.AgentName!, reference.Version, reference.Label);

                throw new NacosServerException(
                    string.Concat(
                        "The Nacos server returned no discovery result for agent '",
                        reference.AgentName,
                        "'."));
            }

            return result;
        }
        catch (NacosException ex) when (
            mode == NacosAITransportMode.Auto
            && ex is NacosConnectionException or NacosTimeoutException)
        {
            // A discovery is a read, so it is safe to repeat over HTTP.
            _fallback.RecordGrpcFailure();

            NacosAILog.AiTransportFallback(_logger, "RAD agent", ex.Message);

            return await DiscoverOverHttpAsync(reference, filter, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task RegisterEndpointsAsync(
        NacosAgentEndpointBatch batch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentException.ThrowIfNullOrWhiteSpace(batch.AgentName, nameof(batch));
        ArgumentException.ThrowIfNullOrWhiteSpace(batch.Protocol, nameof(batch));

        if (batch.Endpoints is not { Count: > 0 })
        {
            // A batch replaces what the same publisher registered before, so an empty one would
            // withdraw the endpoints instead of publishing any.
            throw new ArgumentException(
                "An agent endpoint batch must carry at least one endpoint.",
                nameof(batch));
        }

        // Endpoint publication stays on gRPC in every mode: its owner is the connection, not a
        // heartbeat, so it is not the same contract the HTTP endpoint path implements.
        NacosGrpcConnection connection =
            await ConnectAsync(NacosAITransportMode.Grpc, ct).ConfigureAwait(false);

        var rpcRequest = new AgentEndpointRegisterRpcRequest
        {
            NamespaceId = _options.Namespace,
            RegistrationBatch = batch,
        };

        await connection
            .RequestAsync<AgentEndpointOperationResponse>(rpcRequest, ct)
            .ConfigureAwait(false);

        NacosAILog.AgentEndpointsRegistered(_logger, batch.AgentName!, batch.Protocol!, batch.Endpoints.Count);
    }

    /// <inheritdoc />
    public async Task DeregisterEndpointsAsync(
        string agentName,
        string protocol,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);

        NacosGrpcConnection connection =
            await ConnectAsync(NacosAITransportMode.Grpc, ct).ConfigureAwait(false);

        var rpcRequest = new AgentEndpointDeregisterRpcRequest
        {
            NamespaceId = _options.Namespace,
            AgentName = agentName,
            Protocol = protocol,
        };

        await connection
            .RequestAsync<AgentEndpointOperationResponse>(rpcRequest, ct)
            .ConfigureAwait(false);

        NacosAILog.AgentEndpointsDeregistered(_logger, agentName, protocol);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<NacosAgentDiscoveryResult?> WatchAgentAsync(
        NacosAgentReference reference,
        NacosAgentDiscoveryFilter? filter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.AgentName, nameof(reference));

        return _watch.WatchAsync(reference, filter, ct);
    }

    /// <summary>
    /// Releases the watches this client installed.
    /// </summary>
    /// <returns>A task that completes once every watch was closed.</returns>
    /// <remarks>
    /// This is called by the AI client that owns the connection rather than by a caller: the watches
    /// are closed with the client they were opened through, and a caller ends an individual watch by
    /// leaving its enumeration.
    /// </remarks>
    public ValueTask DisposeAsync() => _watch.DisposeAsync();

    /// <summary>
    /// Gets the transport a RAD agent registry operation uses.
    /// </summary>
    private NacosAITransportMode EffectiveTransport =>
        _options.AI.ResolveTransport(_options.AI.AgentTransportMode);

    /// <summary>
    /// Gets the connection, checking the RAD contract only when gRPC was pinned.
    /// </summary>
    /// <param name="mode">The transport the operation is using.</param>
    /// <param name="ct">A token that cancels the connection attempt.</param>
    /// <returns>The connected connection.</returns>
    /// <remarks>
    /// <see cref="NacosAITransportMode.Http"/> never reaches this method. In
    /// <see cref="NacosAITransportMode.Auto"/> the check is skipped as well, because the transport
    /// that answers is the one that can say whether the server serves the registry: a server that
    /// cannot is reported by the request's own failure, with the same code the local check would
    /// have thrown.
    /// </remarks>
    private async Task<NacosGrpcConnection> ConnectAsync(
        NacosAITransportMode mode,
        CancellationToken ct)
    {
        NacosGrpcConnection connection = await _owner.GetConnectionAsync(ct).ConfigureAwait(false);

        if (mode == NacosAITransportMode.Grpc)
        {
            EnsureRadSupported(connection);
        }

        return connection;
    }

    /// <summary>
    /// Publishes one agent version over the client OpenAPI.
    /// </summary>
    /// <param name="request">The publish request.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The version detail the registry created.</returns>
    /// <exception cref="NacosServerException">The publish was refused or failed.</exception>
    /// <remarks>
    /// The endpoint is a form POST, and the complex members are JSON in fields of their own rather
    /// than one JSON body. The fields are the ones the server binds literally, so a member is
    /// serialized with the same contract the gRPC request uses and then sent as a value.
    /// </remarks>
    private async Task<NacosAgentVersionDetail> PublishOverHttpAsync(
        NacosAgentPublishRequest request,
        CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>(14)
        {
            new("namespaceId", _options.Namespace),
            new("agentName", request.AgentName!),
        };

        AddFormIfPresent(form, "displayName", request.DisplayName);
        AddFormIfPresent(form, "description", request.Description);
        AddFormIfPresent(form, "iconUrl", request.IconUrl);
        AddFormJson(form, "provider", request.Provider, NacosJsonContext.Default.NacosAgentRegistryProvider);
        AddFormJson(form, "tags", request.Tags, NacosJsonContext.Default.ListString);
        AddFormJson(
            form,
            "extensions",
            request.Extensions,
            NacosJsonContext.Default.DictionaryStringJsonElement);

        form.Add(new KeyValuePair<string, string>("version", request.Version!));

        AddFormJson(
            form,
            "callInterfaces",
            request.CallInterfaces,
            NacosJsonContext.Default.ListNacosAgentCallInterface);
        AddFormIfPresent(form, "author", request.Author);
        AddFormIfPresent(form, "changeDescription", request.ChangeDescription);
        AddFormIfPresent(form, "basedOnVersion", request.BasedOnVersion);
        form.Add(new KeyValuePair<string, string>("autoSubmit", request.AutoSubmit ? "true" : "false"));

        NacosAIHttpResponse response = await _http.PostFormAsync(
            AgentPath,
            form,
            _options.RequestTimeout,
            headers: null,
            ct).ConfigureAwait(false);

        NacosAgentPublishResult result = ReadResult(
            response,
            NacosJsonContext.Default.NacosAgentPublishResult,
            "publish of agent",
            request.AgentName!);

        if (result.Code != 0 || result.Data is null)
        {
            NacosAILog.AgentPublishDetailMissing(_logger, request.AgentName!, request.Version);

            throw new NacosServerException(
                result.Code,
                result.Message
                    ?? string.Concat(
                        "The Nacos server published agent '",
                        request.AgentName,
                        "' but returned no version detail."));
        }

        NacosAILog.AgentPublished(
            _logger, request.AgentName!, result.Data.Version, result.Data.Status);

        return result.Data;
    }

    /// <summary>
    /// Searches the agent catalog over the client OpenAPI.
    /// </summary>
    /// <param name="request">The search request.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The page of matching agents.</returns>
    /// <exception cref="NacosServerException">The search failed.</exception>
    private async Task<NacosAgentPage<NacosAgentSummary>> SearchOverHttpAsync(
        NacosAgentSearchRequest request,
        CancellationToken ct)
    {
        var query = new List<KeyValuePair<string, string?>>(7)
        {
            new("namespaceId", _options.Namespace),
        };

        AddIfPresent(query, "agentNameContains", request.AgentNameContains);
        AddEach(query, "tagsAll", request.TagsAll);
        AddEach(query, "protocolsAny", request.ProtocolsAny);
        AddNumber(query, "pageNo", request.PageNo);
        AddNumber(query, "pageSize", request.PageSize);

        NacosAIHttpResponse response =
            await _http.GetAsync(AgentSearchPath, query, ct).ConfigureAwait(false);

        NacosAgentSearchResult result = ReadResult(
            response,
            NacosJsonContext.Default.NacosAgentSearchResult,
            "search of the agent catalog",
            string.Empty);

        if (result.Code != 0 || result.Data is null)
        {
            throw new NacosServerException(
                result.Code,
                result.Message
                    ?? "The Nacos server answered a search but returned no page.");
        }

        return result.Data;
    }

    /// <summary>
    /// Discovers one agent version over the client OpenAPI.
    /// </summary>
    /// <param name="reference">Which agent version the discovery is about.</param>
    /// <param name="filter">What the caller can use, or <see langword="null"/> for no filter.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The discovery answer.</returns>
    /// <exception cref="NacosServerException">
    /// The agent does not exist (the not-found code), or the discovery failed.
    /// </exception>
    private async Task<NacosAgentDiscoveryResult> DiscoverOverHttpAsync(
        NacosAgentReference reference,
        NacosAgentDiscoveryFilter? filter,
        CancellationToken ct)
    {
        var query = new List<KeyValuePair<string, string?>>(10)
        {
            new("namespaceId", _options.Namespace),
            new("agentName", reference.AgentName),
        };

        AddIfPresent(query, "version", reference.Version);
        AddIfPresent(query, "label", reference.Label);

        if (filter is not null)
        {
            AddEach(query, "protocol", filter.Protocols);
            AddIfPresent(query, "protocolVersion", filter.ProtocolVersion);
            AddEach(query, "transport", filter.Transports);
            AddEach(query, "endpointSource", filter.EndpointSources);

            if (filter.MetadataSelector is not null)
            {
                query.Add(new KeyValuePair<string, string?>(
                    "metadataSelector",
                    JsonSerializer.Serialize(
                        filter.MetadataSelector,
                        NacosJsonContext.Default.DictionaryStringString)));
            }
        }

        NacosAIHttpResponse response = await _http.GetAsync(AgentPath, query, ct).ConfigureAwait(false);

        NacosAgentDiscoveryEnvelope result = ReadResult(
            response,
            NacosJsonContext.Default.NacosAgentDiscoveryEnvelope,
            "discovery of agent",
            reference.AgentName!);

        if (result.Code != 0 || result.Data is null)
        {
            NacosAILog.AgentDiscoveryDetailMissing(
                _logger, reference.AgentName!, reference.Version, reference.Label);

            throw new NacosServerException(
                result.Code,
                result.Message
                    ?? string.Concat(
                        "The Nacos server returned no discovery result for agent '",
                        reference.AgentName,
                        "'."));
        }

        return result.Data;
    }

    /// <summary>
    /// Turns an HTTP response into the result envelope, or the exception its status means.
    /// </summary>
    /// <typeparam name="T">The envelope type.</typeparam>
    /// <param name="response">The response.</param>
    /// <param name="typeInfo">The contract to deserialize the body with.</param>
    /// <param name="operation">The operation, used in the message of a failure.</param>
    /// <param name="agentName">The agent, or an empty string when the operation names none.</param>
    /// <returns>The envelope.</returns>
    /// <exception cref="NacosAuthException">The server refused the operation.</exception>
    /// <exception cref="NacosServerException">The status or body was not a success result.</exception>
    /// <remarks>
    /// A missing agent arrives as HTTP 404 on both transports, and the code is what a caller reacts
    /// to, so the status becomes the error code. A 200 carrying something that is not a result — an
    /// intermediary's error page, say — would otherwise surface as a JSON exception from inside the
    /// serializer, which says nothing about what the server did.
    /// </remarks>
    private static T ReadResult<T>(
        NacosAIHttpResponse response,
        JsonTypeInfo<T> typeInfo,
        string operation,
        string agentName)
        where T : class
    {
        switch ((int)response.StatusCode)
        {
            case 200:
                break;

            case 403:
                throw new NacosAuthException(
                    (int)response.StatusCode,
                    response.ErrorMessage
                        ?? string.Concat("The Nacos server refused the ", Describe(operation, agentName), "."));

            default:
                throw new NacosServerException(
                    (int)response.StatusCode,
                    response.ErrorMessage
                        ?? string.Concat(
                            "The Nacos server answered the ",
                            Describe(operation, agentName),
                            " with HTTP ",
                            ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            "."));
        }

        T? result;

        try
        {
            result = JsonSerializer.Deserialize(response.Body, typeInfo);
        }
        catch (JsonException ex)
        {
            throw new NacosServerException(
                string.Concat(
                    "The Nacos server returned a body for the ",
                    Describe(operation, agentName),
                    " that is not a result."),
                ex);
        }

        if (result is null)
        {
            throw new NacosServerException(
                string.Concat(
                    "The Nacos server returned no result for the ",
                    Describe(operation, agentName),
                    "."));
        }

        return result;
    }

    /// <summary>
    /// Names an operation and, when it has one, its agent, for a message.
    /// </summary>
    /// <param name="operation">The operation.</param>
    /// <param name="agentName">The agent, or an empty string when the operation names none.</param>
    /// <returns>The phrase to put in a sentence.</returns>
    private static string Describe(string operation, string agentName) =>
        string.IsNullOrEmpty(agentName)
            ? operation
            : string.Concat(operation, " '", agentName, "'");

    /// <summary>
    /// Adds a query parameter when it carries a value.
    /// </summary>
    /// <param name="query">The parameters collected so far.</param>
    /// <param name="name">The parameter name.</param>
    /// <param name="value">The value, or <see langword="null"/> to omit the parameter.</param>
    private static void AddIfPresent(
        List<KeyValuePair<string, string?>> query,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            query.Add(new KeyValuePair<string, string?>(name, value));
        }
    }

    /// <summary>
    /// Adds one parameter per value, which is how the server binds a list.
    /// </summary>
    /// <param name="query">The parameters collected so far.</param>
    /// <param name="name">The parameter name.</param>
    /// <param name="values">The values, or <see langword="null"/> to add nothing.</param>
    private static void AddEach(
        List<KeyValuePair<string, string?>> query,
        string name,
        List<string>? values)
    {
        if (values is null)
        {
            return;
        }

        foreach (string value in values)
        {
            query.Add(new KeyValuePair<string, string?>(name, value));
        }
    }

    /// <summary>
    /// Adds a numeric query parameter when it carries a value.
    /// </summary>
    /// <param name="query">The parameters collected so far.</param>
    /// <param name="name">The parameter name.</param>
    /// <param name="value">The value, or <see langword="null"/> to omit the parameter.</param>
    private static void AddNumber(
        List<KeyValuePair<string, string?>> query,
        string name,
        int? value)
    {
        if (value is int number)
        {
            query.Add(new KeyValuePair<string, string?>(
                name,
                number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>
    /// Adds a form field when it carries a value.
    /// </summary>
    /// <param name="form">The fields collected so far.</param>
    /// <param name="name">The field name.</param>
    /// <param name="value">The value, or <see langword="null"/> to omit the field.</param>
    private static void AddFormIfPresent(
        List<KeyValuePair<string, string>> form,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            form.Add(new KeyValuePair<string, string>(name, value));
        }
    }

    /// <summary>
    /// Adds a form field whose value is a JSON document.
    /// </summary>
    /// <typeparam name="T">The member type.</typeparam>
    /// <param name="form">The fields collected so far.</param>
    /// <param name="name">The field name.</param>
    /// <param name="value">The value, or <see langword="null"/> to omit the field.</param>
    /// <param name="typeInfo">The contract to serialize the value with.</param>
    private static void AddFormJson<T>(
        List<KeyValuePair<string, string>> form,
        string name,
        T? value,
        JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (value is not null)
        {
            form.Add(new KeyValuePair<string, string>(name, JsonSerializer.Serialize(value, typeInfo)));
        }
    }

    /// <summary>
    /// Fails a call when the server does not speak the RAD contract.
    /// </summary>
    /// <param name="connection">The AI registry connection.</param>
    /// <exception cref="NacosServerException">
    /// The server does not implement the RAD contract, or stated nothing that says which agent
    /// contract it speaks.
    /// </exception>
    /// <remarks>
    /// <para>
    /// A server that states <c>radV1</c> is the only one this surface can talk to. A server that
    /// states the older <c>agent</c> ability instead is a 3.1 or 3.2 one: it has the A2A card
    /// contract and nothing else, which is a refusal rather than an unknown.
    /// </para>
    /// <para>
    /// A server that states neither has told the client nothing, and the two possibilities need
    /// different payloads. Sending one and hoping is what this avoids.
    /// </para>
    /// </remarks>
    private static void EnsureRadSupported(NacosGrpcConnection connection)
    {
        if (connection.ServerAbilities.TryGetValue(NacosAIServerAbility.RadV1, out bool supported))
        {
            if (supported)
            {
                return;
            }

            throw new NacosServerException(
                NotImplementedErrorCode,
                "The connected Nacos server states that it does not implement the RAD agent registry.");
        }

        if (connection.ServerAbilities.TryGetValue(NacosAIServerAbility.Agent, out bool agent) && agent)
        {
            throw new NacosServerException(
                NotImplementedErrorCode,
                string.Concat(
                    "The connected Nacos server implements the A2A agent card registry but not the RAD ",
                    "agent registry, which needs Nacos 3.3 or newer."));
        }

        throw new NacosServerException(
            ServerErrorCode,
            "The connected Nacos server stated neither the RAD agent ability nor the A2A agent "
            + "ability, so the client cannot tell which agent contract it speaks.");
    }
}
