namespace Lolitacoding.Nacos.Protocol.Messages;

/// <summary>
/// Base type for every response the client receives from a Nacos server.
/// </summary>
/// <remarks>
/// Nacos 2.x and 3.x define exactly two result codes: <c>200</c> (success) and <c>500</c>
/// (failure). Most failures surface as an <see cref="ErrorResponse"/> carrying an
/// <see cref="ErrorCode"/> from <c>com.alibaba.nacos.api.exception.NacosException</c>, while
/// some endpoints report an operation-specific code through <see cref="ErrorCode"/> and
/// <see cref="NacosException.NacosException(int, string)"/>.
/// </remarks>
internal abstract class NacosResponse
{
    /// <summary>The result code Nacos uses for a successful response.</summary>
    public const int SuccessCode = 200;

    /// <summary>The result code Nacos uses for a failed response.</summary>
    public const int FailureCode = 500;

    /// <summary>Gets or sets the transport-level result code.</summary>
    public int ResultCode { get; set; } = SuccessCode;

    /// <summary>Gets or sets the server-reported error code.</summary>
    public int ErrorCode { get; set; }

    /// <summary>Gets or sets the human-readable failure message.</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets the correlation identifier echoed from the request.</summary>
    public string? RequestId { get; set; }

    /// <summary>
    /// Gets a value indicating whether the server reported success.
    /// </summary>
    /// <remarks>
    /// Nacos serialises this computed property into every response body, so it is part of the
    /// wire contract rather than a C# convenience.
    /// </remarks>
    public bool Success => ResultCode == SuccessCode;
}

/// <summary>
/// The transport-level failure payload Nacos sends with <c>metadata.type = "ErrorResponse"</c>.
/// </summary>
/// <remarks>
/// The server produces this for conditions that never reach a business handler, such as an
/// unknown request type, a connection that is no longer registered, or a handler that threw.
/// </remarks>
internal sealed class ErrorResponse : NacosResponse
{
}
