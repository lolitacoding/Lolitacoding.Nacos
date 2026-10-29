namespace Lolitacoding.Nacos.AI;

/// <summary>
/// One runtime endpoint an A2A agent is reachable at.
/// </summary>
/// <remarks>
/// <para>
/// An endpoint is where a call goes, not part of the card: a card registered as a <c>SERVICE</c>
/// endpoint names no address, because the agent's address is only known once an instance registers
/// one. Registering an endpoint against the card is therefore what makes a service-backed agent
/// callable, and the registration lives and dies with the connection that made it.
/// </para>
/// <para>
/// The members carry the defaults the A2A binding uses, so an endpoint built from an address and a
/// port is already a <c>JSONRPC</c> endpoint over <c>HTTP</c> — which is what a caller means unless
/// it says otherwise.
/// </para>
/// </remarks>
public sealed class NacosAgentEndpoint
{
    /// <summary>Gets or sets the transport, such as <c>JSONRPC</c>.</summary>
    public string? Transport { get; set; } = NacosA2a.DefaultTransport;

    /// <summary>Gets or sets the host the agent is reached at.</summary>
    public string? Address { get; set; }

    /// <summary>Gets or sets the port the agent listens on.</summary>
    public int Port { get; set; }

    /// <summary>Gets or sets the path the agent's endpoint is served at.</summary>
    /// <remarks>Empty rather than unset, because the address is assembled from it either way.</remarks>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the endpoint is reached over TLS.</summary>
    /// <remarks>
    /// This is what turns the assembled address from <c>http</c> into <c>https</c>; the scheme is not
    /// carried separately.
    /// </remarks>
    public bool SupportTls { get; set; }

    /// <summary>Gets or sets the agent card version the endpoint serves.</summary>
    /// <remarks>
    /// Required: an endpoint is registered against one version of a card, because the service the
    /// registration becomes an instance of is named after the pair. The server refuses a registration
    /// without one, and so does the client before it sends it.
    /// </remarks>
    public string? Version { get; set; }

    /// <summary>Gets or sets the A2A protocol version the endpoint speaks.</summary>
    /// <remarks>
    /// Nacos keeps this in a reserved metadata key on the stored endpoint rather than in the card,
    /// so it travels beside the address rather than inside it.
    /// </remarks>
    public string? ProtocolVersion { get; set; }

    /// <summary>Gets or sets the tenant the endpoint serves.</summary>
    public string? Tenant { get; set; }

    /// <summary>Gets or sets the protocol, <c>HTTP</c> unless the agent speaks something else.</summary>
    public string? Protocol { get; set; } = NacosA2a.DefaultProtocol;

    /// <summary>Gets or sets the query string the agent's URL carries.</summary>
    public string? Query { get; set; }
}
