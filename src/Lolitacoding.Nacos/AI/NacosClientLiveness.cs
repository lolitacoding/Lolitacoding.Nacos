namespace Lolitacoding.Nacos.AI;

/// <summary>
/// The intervals the AI module keeps an HTTP publisher alive with.
/// </summary>
/// <remarks>
/// <para>
/// A gRPC endpoint publication lives exactly as long as the connection that registered it, so the
/// client never has to know how long that is. An HTTP publication has no connection, so the server
/// returns the three intervals that describe its lifetime instead: how often the client should
/// heartbeat, when the publication stops counting as healthy, and when it is deleted. The client
/// uses the first to schedule its heartbeat; the other two are what an operator compares a failing
/// deployment against.
/// </para>
/// </remarks>
internal sealed class NacosClientLivenessInfo
{
    /// <summary>Gets or sets how often the client should heartbeat, in milliseconds.</summary>
    public long HeartbeatIntervalMillis { get; set; }

    /// <summary>Gets or sets how long a silent publication stays healthy, in milliseconds.</summary>
    public long UnhealthyTimeoutMillis { get; set; }

    /// <summary>Gets or sets how long a silent publication is kept before it is deleted.</summary>
    public long ExpireTimeoutMillis { get; set; }
}

/// <summary>
/// The result envelope an AI endpoint registration or heartbeat is answered with.
/// </summary>
/// <remarks>
/// The client OpenAPI wraps every answer in a result object, so the liveness intervals sit in
/// <c>data</c>. A null detail on success is possible and means the server accepted the request
/// without reporting one.
/// </remarks>
internal sealed class NacosClientLivenessResult
{
    /// <summary>Gets or sets the result code, zero for success.</summary>
    public int Code { get; set; }

    /// <summary>Gets or sets the message, which names the code rather than the reason.</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets the liveness intervals.</summary>
    public NacosClientLivenessInfo? Data { get; set; }
}
