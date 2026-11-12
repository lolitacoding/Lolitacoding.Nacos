using Lolitacoding.Nacos.Protocol.Messages;

namespace Lolitacoding.Nacos;

/// <summary>
/// Serves as the base class for every exception raised by the Nacos client.
/// </summary>
/// <remarks>
/// Deriving from <see cref="Exception"/> and sealing every concrete subtype keeps the
/// exception hierarchy closed, so callers can distinguish transport, authentication,
/// timeout and server-side failures without inspecting message text.
/// </remarks>
public class NacosException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NacosException"/> class.
    /// </summary>
    public NacosException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public NacosException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public NacosException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosException"/> class with the
    /// server-reported error code.
    /// </summary>
    /// <param name="errorCode">The numeric error code reported by the Nacos server.</param>
    /// <param name="message">The message that describes the error.</param>
    public NacosException(int errorCode, string message)
        : base(message) => ErrorCode = errorCode;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosException"/> class with the
    /// server-reported error code and an inner exception.
    /// </summary>
    /// <param name="errorCode">The numeric error code reported by the Nacos server.</param>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public NacosException(int errorCode, string message, Exception? innerException)
        : base(message, innerException) => ErrorCode = errorCode;

    /// <summary>
    /// Gets the numeric error code reported by the Nacos server, or <c>0</c> when the
    /// failure did not originate from a server response.
    /// </summary>
    public int ErrorCode { get; }
}

/// <summary>
/// Thrown when the Nacos server rejects the supplied credentials or the access token is invalid.
/// </summary>
public sealed class NacosAuthException : NacosException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NacosAuthException"/> class.
    /// </summary>
    public NacosAuthException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosAuthException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public NacosAuthException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosAuthException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public NacosAuthException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosAuthException"/> class with the
    /// server-reported error code.
    /// </summary>
    /// <param name="errorCode">The numeric error code reported by the Nacos server.</param>
    /// <param name="message">The message that describes the error.</param>
    public NacosAuthException(int errorCode, string message)
        : base(errorCode, message)
    {
    }
}

/// <summary>
/// Thrown when a Nacos request does not complete within the configured timeout.
/// </summary>
public sealed class NacosTimeoutException : NacosException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NacosTimeoutException"/> class.
    /// </summary>
    public NacosTimeoutException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosTimeoutException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public NacosTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosTimeoutException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public NacosTimeoutException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when the Nacos server returns an error response for an otherwise well-formed request.
/// </summary>
public sealed class NacosServerException : NacosException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NacosServerException"/> class.
    /// </summary>
    public NacosServerException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosServerException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public NacosServerException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosServerException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public NacosServerException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosServerException"/> class with the
    /// server-reported error code.
    /// </summary>
    /// <param name="errorCode">The numeric error code reported by the Nacos server.</param>
    /// <param name="message">The message that describes the error.</param>
    public NacosServerException(int errorCode, string message)
        : base(errorCode, message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosServerException"/> class with the
    /// response that reported the failure.
    /// </summary>
    /// <param name="errorCode">The numeric error code reported by the Nacos server.</param>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="response">The decoded response the server sent.</param>
    internal NacosServerException(int errorCode, string message, NacosResponse response)
        : base(errorCode, message) => Response = response;

    /// <summary>
    /// Gets the decoded response that reported the failure, when the server sent one.
    /// </summary>
    /// <remarks>
    /// Most failures carry only a code and a message, but some are an answer the caller can act on:
    /// a "not modified" configuration reply, for instance, carries the hash, content type and
    /// modification time of the version the client already holds. Discarding the response would
    /// leave the caller to guess at metadata the server just sent.
    /// </remarks>
    internal NacosResponse? Response { get; }
}

/// <summary>
/// Thrown when the client cannot reach any of the configured Nacos servers.
/// </summary>
public sealed class NacosConnectionException : NacosException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NacosConnectionException"/> class.
    /// </summary>
    public NacosConnectionException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosConnectionException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public NacosConnectionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosConnectionException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public NacosConnectionException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when the client options are missing or contradictory.
/// </summary>
/// <remarks>
/// This is deliberately distinct from the transport failures: it signals a mistake in the
/// application's own configuration, and no amount of retrying or reconnecting will resolve it.
/// Callers that catch this during startup should fail fast and report the offending setting
/// rather than entering a degraded mode.
/// </remarks>
public sealed class NacosConfigurationException : NacosException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NacosConfigurationException"/> class.
    /// </summary>
    public NacosConfigurationException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosConfigurationException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public NacosConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosConfigurationException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public NacosConfigurationException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
