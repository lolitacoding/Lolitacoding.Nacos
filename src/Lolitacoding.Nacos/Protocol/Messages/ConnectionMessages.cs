namespace Lolitacoding.Nacos.Protocol.Messages;

/// <summary>
/// Probes a server before opening the notification stream.
/// <c>metadata.type = "ServerCheckRequest"</c>.
/// </summary>
/// <remarks>
/// This is the first call of the handshake. The server answers inline with
/// <see cref="ServerCheckResponse"/>, which carries the connection identifier the client later
/// reports when it registers the stream.
/// </remarks>
internal sealed class ServerCheckRequest : NacosRequest
{
}

/// <summary>
/// The server's answer to <see cref="ServerCheckRequest"/>.
/// <c>metadata.type = "ServerCheckResponse"</c>.
/// </summary>
internal sealed class ServerCheckResponse : NacosResponse
{
    /// <summary>Gets or sets the server-assigned connection identifier.</summary>
    public string? ConnectionId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the server supports ability negotiation.
    /// </summary>
    public bool SupportAbilityNegotiation { get; set; }
}

/// <summary>
/// Registers the connection on the notification stream.
/// <c>metadata.type = "ConnectionSetupRequest"</c>.
/// </summary>
/// <remarks>
/// This must be the first frame the client writes to the bidirectional stream. The server
/// replies with <see cref="SetupAckRequest"/> — but only when <see cref="AbilityTable"/> is
/// non-null, so the client always sends a table in order to get an unambiguous handshake
/// acknowledgement.
/// </remarks>
internal sealed class ConnectionSetupRequest : NacosRequest
{
    /// <summary>Gets or sets the client version reported to the server.</summary>
    public string? ClientVersion { get; set; }

    /// <summary>Gets or sets the namespace the connection is scoped to.</summary>
    public string? Tenant { get; set; }

    /// <summary>Gets or sets connection labels, including the application name.</summary>
    public Dictionary<string, string>? Labels { get; set; }

    /// <summary>Gets or sets the client's advertised abilities.</summary>
    public Dictionary<string, bool>? AbilityTable { get; set; }
}

/// <summary>
/// The server's handshake acknowledgement, pushed over the stream.
/// <c>metadata.type = "SetupAckRequest"</c>.
/// </summary>
/// <remarks>
/// There is no <c>ConnectionSetupResponse</c>: the acknowledgement is a request-typed payload
/// that the client must not answer.
/// </remarks>
internal sealed class SetupAckRequest : NacosRequest
{
    /// <summary>Gets or sets the abilities the server advertises.</summary>
    public Dictionary<string, bool>? AbilityTable { get; set; }
}

/// <summary>
/// A liveness probe. <c>metadata.type = "HealthCheckRequest"</c>.
/// </summary>
internal sealed class HealthCheckRequest : NacosRequest
{
}

/// <summary>A liveness probe response. Carries no fields.</summary>
internal sealed class HealthCheckResponse : NacosResponse
{
}

/// <summary>
/// The server's liveness probe of a client connection.
/// <c>metadata.type = "ClientDetectionRequest"</c>.
/// </summary>
/// <remarks>
/// The server sends this to connections that have been idle beyond the keep-alive window. A
/// client that fails to answer in time has its connection unregistered, so the response must be
/// sent promptly on the same stream.
/// </remarks>
internal sealed class ClientDetectionRequest : NacosRequest
{
}

/// <summary>
/// Answers <see cref="ClientDetectionRequest"/>. Carries no fields.
/// </summary>
internal sealed class ClientDetectionResponse : NacosResponse
{
}

/// <summary>
/// Instructs the client to move to another server.
/// <c>metadata.type = "ConnectResetRequest"</c>.
/// </summary>
internal sealed class ConnectResetRequest : NacosRequest
{
    /// <summary>Gets or sets the target server IP address.</summary>
    public string? ServerIp { get; set; }

    /// <summary>
    /// Gets or sets the target server port.
    /// </summary>
    /// <remarks>
    /// A string on the wire: Nacos' own client test asserts <c>"serverPort":"8888"</c>.
    /// </remarks>
    public string? ServerPort { get; set; }

    /// <summary>Gets or sets the connection identifier being redirected.</summary>
    public string? ConnectionId { get; set; }
}

/// <summary>Answers <see cref="ConnectResetRequest"/>. Carries no fields.</summary>
internal sealed class ConnectResetResponse : NacosResponse
{
}

/// <summary>
/// Acknowledges a server-initiated push that carried a request identifier.
/// <c>metadata.type = "PushAckRequest"</c>.
/// </summary>
internal sealed class PushAckRequest : NacosRequest
{
    /// <summary>Gets or sets a value indicating whether the push was handled.</summary>
    public bool Success { get; set; }
}
