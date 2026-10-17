using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Ai;

/// <summary>
/// The MCP server registry, served over the AI registry connection.
/// </summary>
/// <remarks>
/// The client is deliberately thin: Nacos owns the version manifest, the endpoint resolution and
/// the label that points at the latest version, so every operation here is one request and the
/// answer the server gives is the answer the caller sees. Nothing is cached, which is why a read
/// always reports what the server currently serves.
/// </remarks>
internal sealed class NacosMcpClient : INacosMcpRegistry
{
    /// <summary>The error code Nacos uses for a feature a server does not implement.</summary>
    private const int NotImplementedErrorCode = 501;

    private readonly NacosAiClient _owner;
    private readonly NacosOptions _options;
    private readonly ILogger<NacosMcpClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosMcpClient"/> class.
    /// </summary>
    /// <param name="owner">The AI client that owns the connection.</param>
    /// <param name="options">The client options.</param>
    /// <param name="logger">The logger.</param>
    public NacosMcpClient(NacosAiClient owner, NacosOptions options, ILogger<NacosMcpClient> logger)
    {
        _owner = owner;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<NacosMcpServerDetail> GetServerAsync(
        string name,
        string? version = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        NacosGrpcConnection connection = await _owner.GetConnectionAsync(ct).ConfigureAwait(false);
        EnsureSupported(connection, name);

        var request = new QueryMcpServerRequest
        {
            NamespaceId = _options.Namespace,
            McpName = name,
            Version = version,
        };

        QueryMcpServerResponse response =
            await connection.RequestAsync<QueryMcpServerResponse>(request, ct).ConfigureAwait(false);

        NacosMcpServerDetail? detail = response.McpServerDetailInfo;

        if (detail is null)
        {
            // A missing server or version is reported as a failure result code, which the transport
            // has already turned into an exception. Reaching here therefore means the reply did not
            // match the contract, and reporting it as a missing server would hide that.
            NacosAiLog.McpServerDetailMissing(_logger, name, version);

            throw new NacosServerException(
                string.Concat("The Nacos server returned no detail for MCP server '", name, "'."));
        }

        return detail;
    }

    /// <inheritdoc />
    public async Task<string?> ReleaseServerAsync(
        NacosMcpServer server,
        NacosMcpToolSpecification? tools = null,
        NacosMcpResourceSpecification? resources = null,
        NacosMcpEndpointSpec? endpoint = null,
        bool createDraft = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(server.Name, nameof(server));

        string? version = VersionOf(server);

        if (version is null)
        {
            // The version is what the release creates; without one the server would have to invent
            // it, and a later release could not name the same version again.
            throw new ArgumentException(
                "An MCP server release must carry a version, either through VersionDetail or Version.",
                nameof(server));
        }

        NacosGrpcConnection connection = await _owner.GetConnectionAsync(ct).ConfigureAwait(false);
        EnsureSupported(connection, server.Name);
        EnsureDraftSupported(connection, server.Name, createDraft);

        var request = new ReleaseMcpServerRequest
        {
            NamespaceId = _options.Namespace,
            McpName = server.Name,
            ServerSpecification = server,
            ToolSpecification = tools,
            ResourceSpecification = resources,
            EndpointSpecification = endpoint,
            CreateDraft = createDraft,
        };

        ReleaseMcpServerResponse response =
            await connection.RequestAsync<ReleaseMcpServerResponse>(request, ct).ConfigureAwait(false);

        if (response.McpId is not null)
        {
            NacosAiLog.McpServerReleased(_logger, server.Name, version, response.McpId);
        }

        return response.McpId;
    }

    /// <summary>
    /// Reads the version a release will create.
    /// </summary>
    /// <param name="server">The server specification.</param>
    /// <returns>The version, or <see langword="null"/> when the specification carries none.</returns>
    /// <remarks>
    /// Nacos moved the version into the version detail object so it could carry a release date and
    /// a latest flag, and kept the plain string for servers that predate the move. A specification
    /// may legitimately use either, so both are read, with the structured form winning.
    /// </remarks>
    private static string? VersionOf(NacosMcpServer server)
    {
        string? version = server.VersionDetail?.Version;

        if (!string.IsNullOrWhiteSpace(version))
        {
            return version;
        }

        return string.IsNullOrWhiteSpace(server.Version) ? null : server.Version;
    }

    /// <summary>
    /// Fails the operation when the server does not implement the MCP registry.
    /// </summary>
    /// <param name="connection">The AI registry connection.</param>
    /// <param name="name">The MCP server the operation is about.</param>
    /// <exception cref="NacosServerException">The server advertises that it cannot serve the registry.</exception>
    /// <remarks>
    /// Only an explicit rejection is treated as one. A server that says nothing about the ability
    /// may still serve the request, and letting the request fail on its own reports the truth more
    /// precisely than guessing from the absence of a field.
    /// </remarks>
    private static void EnsureSupported(NacosGrpcConnection connection, string name)
    {
        if (connection.ServerAbilities.TryGetValue(NacosAiServerAbility.Mcp, out bool supported)
            && !supported)
        {
            throw new NacosServerException(
                NotImplementedErrorCode,
                string.Concat(
                    "The connected Nacos server does not implement the MCP registry, so MCP server '",
                    name,
                    "' cannot be used."));
        }
    }

    /// <summary>
    /// Fails a draft release when the server does not implement lifecycle drafts.
    /// </summary>
    /// <param name="connection">The AI registry connection.</param>
    /// <param name="name">The MCP server being released.</param>
    /// <param name="createDraft">Whether a draft was requested.</param>
    /// <exception cref="NacosServerException">The server advertises that it cannot store drafts.</exception>
    /// <remarks>
    /// Failing here rather than at the server matters: an older server would read
    /// <c>createDraft</c> as an unknown field and publish the version directly, which is the
    /// opposite of what a caller asking for a draft wants.
    /// </remarks>
    private static void EnsureDraftSupported(
        NacosGrpcConnection connection,
        string name,
        bool createDraft)
    {
        if (!createDraft)
        {
            return;
        }

        if (connection.ServerAbilities.TryGetValue(NacosAiServerAbility.McpDraftRelease, out bool supported)
            && !supported)
        {
            throw new NacosServerException(
                NotImplementedErrorCode,
                string.Concat(
                    "The connected Nacos server does not implement MCP lifecycle drafts, so a draft of '",
                    name,
                    "' cannot be created."));
        }
    }
}
