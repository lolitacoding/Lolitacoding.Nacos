using System.Collections.Concurrent;
using System.Globalization;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Ai;

/// <summary>
/// Owns the MCP server endpoints this process has published, and replays them after a reconnect.
/// </summary>
/// <remarks>
/// <para>
/// An endpoint is registered through the AI module but stored as an instance of the Naming service
/// the server's <c>REF</c> endpoint names, so it is exactly like any other instance registration: the
/// server ties it to the connection that registered it and removes it when that connection closes.
/// The registration therefore has to be replayed after a reconnect, which is the whole reason this
/// type keeps a record of what it published.
/// </para>
/// <para>
/// A registration the server refused is not kept. The caller saw the failure and can decide whether
/// to try again, so silently publishing an endpoint later — after a reconnect the caller never
/// connected to the attempt — would report a state the caller did not ask for. This is the same
/// replay contract the naming client uses.
/// </para>
/// </remarks>
internal sealed class NacosMcpEndpointPublication
{
    private readonly ConcurrentDictionary<string, Publication> _publications = new(StringComparer.Ordinal);
    private readonly Func<CancellationToken, Task<NacosGrpcConnection>> _connection;
    private readonly string? _namespace;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosMcpEndpointPublication"/> class.
    /// </summary>
    /// <param name="connection">Gets the AI registry connection, creating it if needed.</param>
    /// <param name="namespace">The namespace the endpoints are published in.</param>
    /// <param name="logger">The logger.</param>
    public NacosMcpEndpointPublication(
        Func<CancellationToken, Task<NacosGrpcConnection>> connection,
        string? @namespace,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(logger);

        _connection = connection;
        _namespace = @namespace;
        _logger = logger;
    }

    /// <summary>
    /// Publishes one endpoint of an MCP server.
    /// </summary>
    /// <param name="name">The MCP server name.</param>
    /// <param name="address">The endpoint address.</param>
    /// <param name="port">The endpoint port.</param>
    /// <param name="version">
    /// The version the endpoint serves, or <see langword="null"/> for every version of the server.
    /// </param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server accepted the endpoint.</returns>
    public async Task RegisterAsync(
        string name,
        string address,
        int port,
        string? version,
        CancellationToken ct)
    {
        var publication = new Publication(name, address, port, version);
        string key = Key(publication);

        // Recorded before it is sent, so a reconnect that races the first attempt still replays it.
        _publications[key] = publication;

        try
        {
            await SendAsync(publication, NacosAiRemote.RegisterEndpoint, ct).ConfigureAwait(false);
        }
        catch
        {
            _publications.TryRemove(new KeyValuePair<string, Publication>(key, publication));
            throw;
        }

        NacosAiLog.McpEndpointRegistered(_logger, name, address, port);
    }

    /// <summary>
    /// Removes one endpoint of an MCP server.
    /// </summary>
    /// <param name="name">The MCP server name.</param>
    /// <param name="address">The endpoint address.</param>
    /// <param name="port">The endpoint port.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server acknowledged the removal.</returns>
    /// <remarks>
    /// <para>
    /// The record is dropped before the request is sent, so a reconnect cannot replay an endpoint
    /// that is being taken away.
    /// </para>
    /// <para>
    /// The version the endpoint was published for travels with the removal, because Nacos resolves
    /// the service to remove the instance from by that version. The official client sends no version
    /// and lets the server resolve the one it currently labels latest, which removes nothing when the
    /// endpoint was pinned to a version that has since been superseded.
    /// </para>
    /// </remarks>
    public async Task DeregisterAsync(string name, string address, int port, CancellationToken ct)
    {
        _publications.TryRemove(PortableKey(name, address, port), out Publication? publication);

        await SendAsync(
            new Publication(name, address, port, publication?.Version),
            NacosAiRemote.DeregisterEndpoint,
            ct).ConfigureAwait(false);

        NacosAiLog.McpEndpointDeregistered(_logger, name, address, port);
    }

    /// <summary>
    /// Republishes every endpoint this process still owns.
    /// </summary>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>A task that completes once every endpoint was attempted.</returns>
    /// <remarks>
    /// One failing endpoint does not stop the others: it stays recorded so the next reconnect tries
    /// it again rather than losing it.
    /// </remarks>
    public async Task RestoreAsync(CancellationToken ct)
    {
        int restored = 0;

        foreach (Publication publication in _publications.Values)
        {
            try
            {
                await SendAsync(publication, NacosAiRemote.RegisterEndpoint, ct).ConfigureAwait(false);
                restored++;
            }
            catch (NacosException ex)
            {
                NacosAiLog.McpEndpointRestoreFailed(_logger, publication.Name, ex.Message);
            }
        }

        if (restored > 0)
        {
            NacosAiLog.McpEndpointsRestored(_logger, restored);
        }
    }

    /// <summary>
    /// Sends one endpoint operation.
    /// </summary>
    /// <param name="publication">The endpoint the operation is about.</param>
    /// <param name="type">The operation, one of the <see cref="NacosAiRemote"/> endpoint types.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server answered.</returns>
    private async Task SendAsync(Publication publication, string type, CancellationToken ct)
    {
        NacosGrpcConnection connection = await _connection(ct).ConfigureAwait(false);

        var request = new McpServerEndpointRequest
        {
            NamespaceId = _namespace,
            McpName = publication.Name,
            Address = publication.Address,
            Port = publication.Port,
            Version = publication.Version,
            Type = type,
        };

        await connection.RequestAsync<McpServerEndpointResponse>(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the key of one endpoint.
    /// </summary>
    /// <param name="publication">The endpoint.</param>
    /// <returns>The key.</returns>
    private static string Key(Publication publication)
        => PortableKey(publication.Name, publication.Address, publication.Port);

    /// <summary>
    /// Builds the key of one endpoint from its parts.
    /// </summary>
    /// <param name="name">The MCP server name.</param>
    /// <param name="address">The endpoint address.</param>
    /// <param name="port">The endpoint port.</param>
    /// <returns>The key.</returns>
    /// <remarks>
    /// The address and port are part of the key so a server that publishes several endpoints keeps
    /// them all across a reconnect. The official client keys its replay record by server name alone,
    /// which leaves all but the last endpoint of a name unpublished after a reconnect.
    /// </remarks>
    private static string PortableKey(string name, string address, int port)
        => string.Concat(name, "::", address, ":", port.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// One published endpoint.
    /// </summary>
    private sealed class Publication
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Publication"/> class.
        /// </summary>
        /// <param name="name">The MCP server name.</param>
        /// <param name="address">The endpoint address.</param>
        /// <param name="port">The endpoint port.</param>
        /// <param name="version">The version the endpoint serves, when it is pinned to one.</param>
        public Publication(string name, string address, int port, string? version)
        {
            Name = name;
            Address = address;
            Port = port;
            Version = version;
        }

        /// <summary>Gets the MCP server name.</summary>
        public string Name { get; }

        /// <summary>Gets the endpoint address.</summary>
        public string Address { get; }

        /// <summary>Gets the endpoint port.</summary>
        public int Port { get; }

        /// <summary>Gets the version the endpoint serves, when it is pinned to one.</summary>
        public string? Version { get; }
    }
}
