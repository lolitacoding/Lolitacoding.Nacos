using System.Globalization;
using System.Net;
using System.Text.Json;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Messages;

namespace Lolitacoding.Nacos.AI;

/// <summary>
/// The batch binding an HTTP agent watch is registered through.
/// </summary>
/// <remarks>
/// The transport owns the generation, the watch set and the fallback rules, and this is the one call
/// it makes on the wire. Keeping it an interface lets the transport be tested without an HTTP
/// server, which matters because its interesting behaviour is timing and failure handling rather than
/// serialization.
/// </remarks>
internal interface INacosAgentHttpWatchBinding
{
    /// <summary>
    /// Sends one complete watch set and waits for a change or the timeout.
    /// </summary>
    /// <param name="generation">The client generation the set belongs to.</param>
    /// <param name="timeoutMillis">How long the server may hold the request.</param>
    /// <param name="watches">The complete watch set.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The server's answer.</returns>
    /// <exception cref="NacosException">The server rejected the request.</exception>
    Task<AgentWatchBatchResponse> WatchAsync(
        long generation,
        int timeoutMillis,
        IReadOnlyList<AgentWatchBatchItem> watches,
        CancellationToken ct);
}

/// <summary>
/// Issues the AI module's HTTP batch long poll for agent watches.
/// </summary>
/// <remarks>
/// <para>
/// This is the HTTP half of the watch contract. The client sends its complete watch set together
/// with the fingerprint of the state it holds, the server holds the request until one of those
/// projections no longer matches or the timeout elapses, and the answer names the watches the client
/// has to read again. Nothing about the response is stateful: a lost long poll is followed by another
/// one carrying the same set.
/// </para>
/// <para>
/// The batch is a form rather than a JSON body because that is the shape the server's
/// <c>AgentWatchBatchForm</c> binds, and the two headers identify the caller: the client identifier is
/// the same one its endpoint publications use, and the module header is what routes the request to
/// the AI module.
/// </para>
/// </remarks>
internal sealed class NacosAgentHttpWatchClient : INacosAgentHttpWatchBinding
{
    /// <summary>The path of the batch watch endpoint.</summary>
    private const string WatchPath = "/v3/client/ai/agents/watch";

    /// <summary>The header that names the client.</summary>
    private const string ClientIdHeader = "X-Nacos-Client-Id";

    /// <summary>The header that routes the request to the AI module.</summary>
    private const string RequestModuleHeader = "Request-Module";

    /// <summary>The module value the AI endpoints expect.</summary>
    private const string AIModule = "ai";

    /// <summary>The business code Nacos uses for a successful result.</summary>
    private const int SuccessCode = 200;

    /// <summary>The business code Nacos uses when an HTTP publisher is not registered.</summary>
    private const int HttpClientNotFoundCode = 50404;

    /// <summary>The business code Nacos uses while an agent is protected by the A2A migration.</summary>
    private const int MigrationInProgressCode = 50105;

    /// <summary>The business code Nacos uses when the watch capacity is reached.</summary>
    private const int WatchCapacityCode = 50104;

    /// <summary>The status Nacos reports alongside a capacity rejection.</summary>
    private const int OverThresholdCode = 503;

    /// <summary>The margin added to the long-poll timeout for the response to arrive.</summary>
    private static readonly TimeSpan ReadTimeoutGrace = TimeSpan.FromSeconds(5);

    private readonly NacosAIHttpClient _http;
    private readonly string _clientId;
    private readonly string _requestModule;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosAgentHttpWatchClient"/> class.
    /// </summary>
    /// <param name="http">The AI HTTP transport.</param>
    /// <param name="clientId">The identifier this client publishes endpoints under.</param>
    public NacosAgentHttpWatchClient(NacosAIHttpClient http, string clientId)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        _http = http;
        _clientId = clientId;
        _requestModule = AIModule;
    }

    /// <summary>
    /// Sends one complete watch set and waits for a change or the timeout.
    /// </summary>
    /// <param name="generation">The client generation the set belongs to.</param>
    /// <param name="timeoutMillis">How long the server may hold the request.</param>
    /// <param name="watches">The complete watch set.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The server's answer.</returns>
    /// <exception cref="NacosException">The server rejected the request.</exception>
    /// <exception cref="NacosAgentWatchCapacityException">
    /// The server reached its per-client watch capacity.
    /// </exception>
    public async Task<AgentWatchBatchResponse> WatchAsync(
        long generation,
        int timeoutMillis,
        IReadOnlyList<AgentWatchBatchItem> watches,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(watches);

        string payload = JsonSerializer.Serialize(
            watches as List<AgentWatchBatchItem> ?? [.. watches],
            NacosJsonContext.Default.ListAgentWatchBatchItem);

        var form = new List<KeyValuePair<string, string>>(3)
        {
            new("generation", generation.ToString(CultureInfo.InvariantCulture)),
            new("timeoutMillis", timeoutMillis.ToString(CultureInfo.InvariantCulture)),
            new("watches", payload),
        };

        var headers = new List<KeyValuePair<string, string>>(2)
        {
            new(ClientIdHeader, _clientId),
            new(RequestModuleHeader, _requestModule),
        };

        NacosAIHttpResponse response = await _http.PostFormAsync(
            WatchPath,
            form,
            TimeSpan.FromMilliseconds(timeoutMillis) + ReadTimeoutGrace,
            headers,
            ct).ConfigureAwait(false);

        NacosApiResult? result = TryReadResult(response.Body);
        bool succeeded = response.StatusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices;

        if (result?.Code is not null && result.Code != SuccessCode)
        {
            throw MapFailure(result, response);
        }

        if (!succeeded)
        {
            throw new NacosServerException(
                (int)response.StatusCode,
                response.ErrorMessage
                ?? string.Concat(
                    "The Nacos server answered the agent watch with HTTP ",
                    ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
                    "."));
        }

        if (result?.Code is null)
        {
            throw new NacosServerException(
                500,
                "The Nacos server answered the agent watch without a result envelope.");
        }

        if (result.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            throw new NacosServerException(
                500,
                "The Nacos server answered the agent watch without a batch.");
        }

        AgentWatchBatchResponse? batch;

        try
        {
            batch = data.Deserialize(NacosJsonContext.Default.AgentWatchBatchResponse);
        }
        catch (JsonException ex)
        {
            throw new NacosServerException(
                500,
                string.Concat(
                    "The Nacos server answered the agent watch with an unreadable batch: ",
                    ex.Message));
        }

        return batch ?? throw new NacosServerException(
            500,
            "The Nacos server answered the agent watch without a batch.");
    }

    /// <summary>
    /// Maps a failed v3 result onto the exception the transport reacts to.
    /// </summary>
    /// <param name="result">The result envelope.</param>
    /// <param name="response">The HTTP response that carried it.</param>
    /// <returns>The exception to throw.</returns>
    private static NacosException MapFailure(NacosApiResult result, NacosAIHttpResponse response)
    {
        string message = ReadDetail(result) ?? result.Message ?? response.ErrorMessage ?? "Agent watch failed.";

        if (result.Code == WatchCapacityCode)
        {
            return new NacosAgentWatchCapacityException(OverThresholdCode, message);
        }

        int errorCode = result.Code is HttpClientNotFoundCode or MigrationInProgressCode
            ? result.Code.Value
            : (int)response.StatusCode;

        return new NacosServerException(errorCode, message);
    }

    /// <summary>
    /// Reads the detail a failed result carries.
    /// </summary>
    /// <param name="result">The result envelope.</param>
    /// <returns>The detail, or <see langword="null"/> when the result carries none.</returns>
    private static string? ReadDetail(NacosApiResult result)
    {
        if (result.Data is not { } data || data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return data.ValueKind == JsonValueKind.String
            ? data.GetString()
            : data.GetRawText();
    }

    /// <summary>
    /// Reads a v3 result envelope, tolerating a body that is not one.
    /// </summary>
    /// <param name="body">The response body.</param>
    /// <returns>The envelope, or <see langword="null"/> when the body is not one.</returns>
    private static NacosApiResult? TryReadResult(byte[] body)
    {
        if (body.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(body, NacosJsonContext.Default.NacosApiResult);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Thrown when the server refuses a watch because the client's watch capacity is exhausted.
/// </summary>
/// <remarks>
/// The refusal is reported as an ordinary over-threshold status with the capacity detail in the
/// result body, so the detail is kept separately rather than folded into the status. The transport
/// needs it to tell a capacity rejection — which only rejects the newest watch — apart from a
/// transport that has to be abandoned.
/// </remarks>
internal sealed class NacosAgentWatchCapacityException : NacosException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NacosAgentWatchCapacityException"/> class.
    /// </summary>
    /// <param name="errorCode">The status the server reported.</param>
    /// <param name="message">The message that describes the error.</param>
    public NacosAgentWatchCapacityException(int errorCode, string message)
        : base(errorCode, message)
    {
    }

    /// <summary>Gets the business detail code that identifies the capacity rejection.</summary>
    public int DetailErrorCode { get; } = 50104;
}
