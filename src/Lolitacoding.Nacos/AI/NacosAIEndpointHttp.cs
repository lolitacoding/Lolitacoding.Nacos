using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Messages;

namespace Lolitacoding.Nacos.AI;

/// <summary>
/// Which transport owns one endpoint publication.
/// </summary>
/// <remarks>
/// The owner is chosen once, when a publication is first registered, and kept with it: a
/// registration and the heartbeat or removal that follows have to go to the same place, because the
/// two transports keep two different records. Publishing the same endpoint over both would leave a
/// copy behind that nothing removes.
/// </remarks>
internal enum NacosAIPublicationTransport
{
    /// <summary>The AI registry connection owns the publication.</summary>
    Grpc,

    /// <summary>The shared HTTP publisher owns the publication.</summary>
    Http,
}

/// <summary>
/// Reads the answers the AI module gives to the stateful endpoint requests.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint requests are the only AI calls that are stateful on the server: a registration binds
/// the HTTP client to a namespace and an identity, a heartbeat renews everything that client owns,
/// and a removal disconnects the client once it owns nothing. The failures therefore carry business
/// codes the plain read paths never produce, and the publisher has to read them to decide whether a
/// failure is worth retrying.
/// </para>
/// <para>
/// The retry rules are the official client's: a server-side failure or an unreachable server is
/// worth retrying, while a capacity refusal, a migration refusal and an unimplemented contract are
/// facts that will not change by asking again.
/// </para>
/// </remarks>
internal static class NacosAIEndpointHttp
{
    /// <summary>The business code for an HTTP client the server no longer knows.</summary>
    public const int HttpClientNotFoundErrorCode = 50404;

    /// <summary>The business code for an agent whose A2A migration is still running.</summary>
    public const int AgentMigrationInProgressErrorCode = 50105;

    /// <summary>The business code for a publisher that reached its endpoint capacity.</summary>
    public const int EndpointPublicationOverLimitErrorCode = 50103;

    /// <summary>The code a server uses for a contract it does not implement.</summary>
    private const int NotImplementedErrorCode = 501;

    /// <summary>The code a client uses when it was already disposed.</summary>
    private const int ClientDisconnectErrorCode = -401;

    /// <summary>The code a client uses for a request it could not send.</summary>
    private const int ClientErrorCode = -500;

    /// <summary>
    /// Fails the operation when the response is not a success.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <param name="operation">The operation, used in the message of a failure.</param>
    /// <exception cref="NacosAuthException">The server refused the operation.</exception>
    /// <exception cref="NacosServerException">The status or body said the operation failed.</exception>
    /// <remarks>
    /// A stateful AI failure names a business code in the body, and three of those are more
    /// informative than the status: a lost HTTP client, a migration in progress and a capacity
    /// refusal all arrive as an ordinary 4xx, so the code is what the publisher reacts to.
    /// </remarks>
    public static void ThrowIfFailed(NacosAIHttpResponse response, string operation)
    {
        int status = (int)response.StatusCode;

        if (status is >= 200 and < 300)
        {
            return;
        }

        string message = response.ErrorMessage
            ?? string.Concat("The Nacos server answered the ", operation, " with HTTP ", status, ".");

        if (status == 403)
        {
            throw new NacosAuthException(status, message);
        }

        int errorCode = response.ErrorCode is int code
            && code is HttpClientNotFoundErrorCode
                or AgentMigrationInProgressErrorCode
                or EndpointPublicationOverLimitErrorCode
                    ? code
                    : status;

        throw new NacosServerException(errorCode, message);
    }

    /// <summary>
    /// Reads the liveness intervals from a registration or heartbeat answer.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <param name="operation">The operation, used in the message of a failure.</param>
    /// <returns>The intervals, or <see langword="null"/> when the server reported none.</returns>
    /// <exception cref="NacosAuthException">The server refused the operation.</exception>
    /// <exception cref="NacosServerException">The status or body said the operation failed.</exception>
    public static NacosClientLivenessInfo? ReadLiveness(
        NacosAIHttpResponse response,
        string operation)
    {
        ThrowIfFailed(response, operation);

        NacosClientLivenessResult? result = ReadResult(
            response,
            NacosJsonContext.Default.NacosClientLivenessResult,
            operation);

        if (result.Code != 0)
        {
            throw new NacosServerException(
                result.Code,
                result.Message
                    ?? string.Concat("The Nacos server refused the ", operation, "."));
        }

        return result.Data;
    }

    /// <summary>
    /// Reads an answer that carries nothing, failing when it reports an error.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <param name="operation">The operation, used in the message of a failure.</param>
    /// <exception cref="NacosAuthException">The server refused the operation.</exception>
    /// <exception cref="NacosServerException">The status or body said the operation failed.</exception>
    public static void ReadAccepted(NacosAIHttpResponse response, string operation)
    {
        ThrowIfFailed(response, operation);

        NacosApiResult? result = ReadResult(
            response,
            NacosJsonContext.Default.NacosApiResult,
            operation);

        if (result.Code is int code && code != 0)
        {
            throw new NacosServerException(
                code,
                result.Message ?? string.Concat("The Nacos server refused the ", operation, "."));
        }
    }

    /// <summary>
    /// Decides whether a failed publication is worth trying again.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <returns><see langword="true"/> when the failure may resolve by itself.</returns>
    /// <remarks>
    /// An unreachable server carries no code and is retried for the same reason a server error is:
    /// neither is an answer. A refusal the server chose — capacity, migration, an unimplemented
    /// contract — will be chosen again, so it is not.
    /// </remarks>
    public static bool IsRetryable(NacosException exception) =>
        exception.ErrorCode != AgentMigrationInProgressErrorCode
        && exception.ErrorCode != NotImplementedErrorCode
        && exception.ErrorCode != EndpointPublicationOverLimitErrorCode
        && exception.ErrorCode != ClientDisconnectErrorCode
        && (exception.ErrorCode >= 500
            || exception.ErrorCode == 0
            || exception.ErrorCode == ClientErrorCode);

    /// <summary>
    /// Decides whether a failure was the server refusing more endpoint publications.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <returns><see langword="true"/> when the publisher is over its capacity.</returns>
    public static bool IsCapacityRejected(NacosException exception) =>
        exception.ErrorCode == EndpointPublicationOverLimitErrorCode;

    /// <summary>
    /// Deserializes the result envelope, or reports a body that is not one.
    /// </summary>
    /// <typeparam name="T">The envelope type.</typeparam>
    /// <param name="response">The response.</param>
    /// <param name="typeInfo">The contract to deserialize the body with.</param>
    /// <param name="operation">The operation, used in the message of a failure.</param>
    /// <returns>The envelope.</returns>
    /// <exception cref="NacosServerException">The body was not a result envelope.</exception>
    private static T ReadResult<T>(
        NacosAIHttpResponse response,
        JsonTypeInfo<T> typeInfo,
        string operation)
        where T : class
    {
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
                    operation,
                    " that is not a result."),
                ex);
        }

        return result ?? throw new NacosServerException(
            string.Concat("The Nacos server returned no result for the ", operation, "."));
    }
}
