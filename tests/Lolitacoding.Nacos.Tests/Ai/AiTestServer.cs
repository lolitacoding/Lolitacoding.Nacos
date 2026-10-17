using Lolitacoding.Nacos.Ai;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Generated;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Tests.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.Tests.Ai;

/// <summary>
/// A scripted stand-in for the Nacos AI registry service.
/// </summary>
/// <remarks>
/// It answers the per-connection probe as the real server does, records every AI request it was
/// asked to serve, and delegates the answer to the test. An unscripted request fails the test where
/// it was made, so a client sending something unexpected is reported as such rather than as a
/// timeout.
/// </remarks>
internal sealed class AiTestServer
{
    /// <summary>The address the channel and the client are built for.</summary>
    public static readonly NacosServerAddress Server = NacosServerAddress.Parse("127.0.0.1:8848");

    private readonly List<QueryMcpServerRequest> _queries = [];
    private readonly List<ReleaseMcpServerRequest> _releases = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="AiTestServer"/> class.
    /// </summary>
    /// <param name="abilities">
    /// The ability table the connection setup is answered with, or <see langword="null"/> for none.
    /// </param>
    public AiTestServer(Dictionary<string, bool>? abilities = null)
    {
        Stream = new FakeNacosDuplexStream
        {
            SetupAckAbilityTable = abilities ?? [],
        };

        var channel = new FakeNacosChannel(Server, Handle, Stream);
        Channel = channel;
        Factory = new FakeNacosChannelFactory(_ => channel);
    }

    /// <summary>Gets the channel the AI connection talks over.</summary>
    public FakeNacosChannel Channel { get; }

    /// <summary>Gets the stream the AI connection talks over.</summary>
    public FakeNacosDuplexStream Stream { get; }

    /// <summary>Gets the channel factory the connection was built with.</summary>
    public FakeNacosChannelFactory Factory { get; }

    /// <summary>Gets the MCP queries the client sent, in order.</summary>
    public IReadOnlyList<QueryMcpServerRequest> Queries
    {
        get
        {
            lock (_queries)
            {
                return [.. _queries];
            }
        }
    }

    /// <summary>Gets the MCP releases the client sent, in order.</summary>
    public IReadOnlyList<ReleaseMcpServerRequest> Releases
    {
        get
        {
            lock (_releases)
            {
                return [.. _releases];
            }
        }
    }

    /// <summary>Gets or sets how the server answers an MCP query.</summary>
    public Func<QueryMcpServerRequest, QueryMcpServerResponse> OnQuery { get; set; } =
        _ => throw new InvalidOperationException("The test did not script an answer for an MCP query.");

    /// <summary>Gets or sets how the server answers an MCP release.</summary>
    public Func<ReleaseMcpServerRequest, ReleaseMcpServerResponse> OnRelease { get; set; } =
        _ => throw new InvalidOperationException("The test did not script an answer for an MCP release.");

    /// <summary>
    /// Builds an AI client that talks to this server.
    /// </summary>
    /// <param name="options">The options to use, or <see langword="null"/> for the test defaults.</param>
    /// <returns>The client.</returns>
    public NacosAiClient CreateClient(NacosOptions? options = null)
    {
        options ??= new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "ai-tests",
            ConnectionTimeout = TimeSpan.FromSeconds(5),
        };

        return new NacosAiClient(
            options,
            [Server],
            Factory,
            TimeProvider.System,
            NullLoggerFactory.Instance,
            new Random(1234));
    }

    private Payload Handle(Payload payload)
    {
        switch (PayloadCodec.ReadTypeName(payload))
        {
            case "ServerCheckRequest":
                return FakeServerCheck.Responding()(payload);

            case "QueryMcpServerRequest":
            {
                QueryMcpServerRequest request = PayloadCodec.Decode<QueryMcpServerRequest>(payload)!;

                lock (_queries)
                {
                    _queries.Add(request);
                }

                return PayloadCodec.Encode(OnQuery(request));
            }

            case "ReleaseMcpServerRequest":
            {
                ReleaseMcpServerRequest request = PayloadCodec.Decode<ReleaseMcpServerRequest>(payload)!;

                lock (_releases)
                {
                    _releases.Add(request);
                }

                return PayloadCodec.Encode(OnRelease(request));
            }

            default:
                throw new InvalidOperationException(
                    $"The AI server does not serve '{PayloadCodec.ReadTypeName(payload)}'.");
        }
    }
}

/// <summary>
/// The answers a scripted AI service gives.
/// </summary>
internal static class AiTest
{
    /// <summary>Builds a successful MCP query answer.</summary>
    /// <param name="detail">The detail to return.</param>
    /// <returns>The answer.</returns>
    public static QueryMcpServerResponse Detail(NacosMcpServerDetail detail) => new()
    {
        McpServerDetailInfo = detail,
    };

    /// <summary>Builds a failure answer, the way the server reports a missing resource.</summary>
    /// <param name="errorCode">The error code the server reports.</param>
    /// <param name="message">The message the server reports.</param>
    /// <returns>The answer.</returns>
    public static QueryMcpServerResponse Failure(int errorCode, string message) => new()
    {
        ResultCode = NacosResponse.FailureCode,
        ErrorCode = errorCode,
        Message = message,
    };
}
