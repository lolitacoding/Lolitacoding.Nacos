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
/// timeout. Each connect gets a fresh stream, so a test can drop the connection and watch the client
/// replay what it still owns.
/// </remarks>
internal sealed class AiTestServer
{
    /// <summary>The address the channel and the client are built for.</summary>
    public static readonly NacosServerAddress Server = NacosServerAddress.Parse("127.0.0.1:8848");

    private readonly Queue<FakeNacosDuplexStream> _pending = new(
        Enumerable.Range(0, 4).Select(_ => new FakeNacosDuplexStream()));

    private readonly List<FakeNacosChannel> _channels = [];
    private readonly List<QueryMcpServerRequest> _queries = [];
    private readonly List<ReleaseMcpServerRequest> _releases = [];
    private readonly List<McpServerEndpointRequest> _endpoints = [];
    private readonly List<QueryPromptRequest> _prompts = [];

    private readonly Dictionary<string, bool> _abilities;
    private readonly HttpMessageHandler? _httpHandler;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiTestServer"/> class.
    /// </summary>
    /// <param name="abilities">
    /// The ability table the connection setup is answered with, or <see langword="null"/> for none.
    /// </param>
    /// <param name="httpHandler">
    /// The handler the AI module's HTTP-only resources are read through, or <see langword="null"/>
    /// when the test does not use them.
    /// </param>
    public AiTestServer(
        Dictionary<string, bool>? abilities = null,
        HttpMessageHandler? httpHandler = null)
    {
        _abilities = abilities ?? [];
        _httpHandler = httpHandler;

        // A supply of streams so a test can drop one and let a fresh one replace it. They are
        // prepared up front because the ability table is part of the handshake answer.
        foreach (FakeNacosDuplexStream stream in _pending)
        {
            stream.SetupAckAbilityTable = _abilities;
        }

        Factory = new FakeNacosChannelFactory(NextChannel);
    }

    /// <summary>Gets the stream the current connection talks over.</summary>
    public FakeNacosDuplexStream Stream => _channels.Count > 0 ? _channels[^1].Stream : _pending.Peek();

    /// <summary>Gets the most recent channel the client connected over.</summary>
    public FakeNacosChannel Channel => _channels[^1];

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

    /// <summary>Gets the MCP endpoint requests the client sent, in order.</summary>
    public IReadOnlyList<McpServerEndpointRequest> Endpoints
    {
        get
        {
            lock (_endpoints)
            {
                return [.. _endpoints];
            }
        }
    }

    /// <summary>Gets the prompt queries the client sent, in order.</summary>
    public IReadOnlyList<QueryPromptRequest> Prompts
    {
        get
        {
            lock (_prompts)
            {
                return [.. _prompts];
            }
        }
    }

    /// <summary>Gets how many channels the client has connected over.</summary>
    public int ConnectionCount => _channels.Count;

    /// <summary>Counts the unary requests of a payload type name across every channel.</summary>
    /// <param name="typeName">The payload type name.</param>
    /// <returns>The number of matching requests.</returns>
    public int CountRequests(string typeName) =>
        _channels.Sum(channel => channel.UnaryRequests.Count(
            payload => PayloadCodec.ReadTypeName(payload) == typeName));

    /// <summary>Gets or sets how the server answers an MCP query.</summary>
    public Func<QueryMcpServerRequest, QueryMcpServerResponse> OnQuery { get; set; } =
        _ => throw new InvalidOperationException("The test did not script an answer for an MCP query.");

    /// <summary>Gets or sets how the server answers an MCP release.</summary>
    public Func<ReleaseMcpServerRequest, ReleaseMcpServerResponse> OnRelease { get; set; } =
        _ => throw new InvalidOperationException("The test did not script an answer for an MCP release.");

    /// <summary>Gets or sets how the server answers an MCP endpoint operation.</summary>
    public Func<McpServerEndpointRequest, McpServerEndpointResponse> OnEndpoint { get; set; } =
        request => AiTest.Endpoint(request.Type!);

    /// <summary>Gets or sets how the server answers a prompt query.</summary>
    public Func<QueryPromptRequest, QueryPromptResponse> OnPrompt { get; set; } =
        _ => throw new InvalidOperationException("The test did not script an answer for a prompt query.");

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

            // Reconnect tests need the retry to happen at once rather than after the production
            // delay, and with no jitter so the waits are deterministic.
            Reconnect = new NacosReconnectOptions
            {
                InitialDelay = TimeSpan.FromMilliseconds(1),
                MaxDelay = TimeSpan.FromMilliseconds(2),
                JitterFactor = 0,
            },
        };

        return new NacosAiClient(
            options,
            [Server],
            Factory,
            _httpHandler is null ? new HttpClient() : new HttpClient(_httpHandler),
            TimeProvider.System,
            NullLoggerFactory.Instance,
            new Random(1234));
    }

    /// <summary>
    /// Drops the current stream and waits for the automatic reconnect to connect.
    /// </summary>
    /// <returns>A task that completes once a fresh channel is in use.</returns>
    public async Task DropAndReconnectAsync()
    {
        int channelsBefore = _channels.Count;

        Stream.EndStream();

        await WaitForAsync(() => _channels.Count > channelsBefore);

        // Wait for the fresh channel to have completed a handshake before returning.
        await WaitForAsync(() => _channels[^1].UnaryRequests.Count >= 1);
    }

    /// <summary>Waits until a condition holds, or throws when it never does.</summary>
    /// <param name="condition">The condition to wait for.</param>
    /// <returns>A task that completes once the condition holds.</returns>
    public static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 1000; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The expected condition was not met within the timeout.");
    }

    private FakeNacosChannel NextChannel(NacosServerAddress address)
    {
        // Each attempt gets a stream of its own, so a reconnect writes to a stream the test can
        // inspect separately from the one that was dropped.
        var channel = new FakeNacosChannel(address, Handle, _pending.Dequeue());
        _channels.Add(channel);

        return channel;
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

            case "McpServerEndpointRequest":
            {
                McpServerEndpointRequest request = PayloadCodec.Decode<McpServerEndpointRequest>(payload)!;

                lock (_endpoints)
                {
                    _endpoints.Add(request);
                }

                return PayloadCodec.Encode(OnEndpoint(request));
            }

            case "QueryPromptRequest":
            {
                QueryPromptRequest request = PayloadCodec.Decode<QueryPromptRequest>(payload)!;

                lock (_prompts)
                {
                    _prompts.Add(request);
                }

                return PayloadCodec.Encode(OnPrompt(request));
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

    /// <summary>Builds a successful MCP endpoint answer.</summary>
    /// <param name="type">The operation the server performed.</param>
    /// <returns>The answer.</returns>
    public static McpServerEndpointResponse Endpoint(string type) => new() { Type = type };

    /// <summary>Builds a failed MCP endpoint answer.</summary>
    /// <param name="errorCode">The error code the server reports.</param>
    /// <param name="message">The message the server reports.</param>
    /// <returns>The answer.</returns>
    public static McpServerEndpointResponse EndpointFailure(int errorCode, string message) => new()
    {
        ResultCode = NacosResponse.FailureCode,
        ErrorCode = errorCode,
        Message = message,
    };

    /// <summary>Builds an MCP server detail that carries a version.</summary>
    /// <param name="name">The server name.</param>
    /// <param name="version">The version.</param>
    /// <param name="isLatest">Whether the server flags the version as the latest one.</param>
    /// <param name="description">An optional description, used to make two versions differ.</param>
    /// <returns>The detail.</returns>
    public static NacosMcpServerDetail Server(
        string name,
        string version,
        bool isLatest = true,
        string? description = null) => new()
    {
        Name = name,
        Protocol = NacosMcpProtocol.Streamable,
        Description = description,
        VersionDetail = new NacosMcpVersionDetail { Version = version, IsLatest = isLatest },
        Status = NacosMcpStatus.Active,
    };

    /// <summary>Builds a successful prompt query answer.</summary>
    /// <param name="prompt">The prompt to return.</param>
    /// <returns>The answer.</returns>
    public static QueryPromptResponse Prompt(NacosPrompt prompt) => new()
    {
        PromptInfo = prompt,
    };

    /// <summary>Builds a failed prompt query answer, the way the server reports a missing or unchanged one.</summary>
    /// <param name="errorCode">The error code the server reports.</param>
    /// <param name="message">The message the server reports.</param>
    /// <returns>The answer.</returns>
    public static QueryPromptResponse PromptFailure(int errorCode, string message) => new()
    {
        ResultCode = NacosResponse.FailureCode,
        ErrorCode = errorCode,
        Message = message,
    };

    /// <summary>Builds a prompt a server would serve.</summary>
    /// <param name="promptKey">The prompt key.</param>
    /// <param name="version">The resolved version.</param>
    /// <param name="template">The template.</param>
    /// <param name="md5">The content hash, defaulted from the template so two templates differ.</param>
    /// <param name="variables">The placeholders the template uses.</param>
    /// <returns>The prompt.</returns>
    public static NacosPrompt CreatePrompt(
        string promptKey,
        string version,
        string template,
        string? md5 = null,
        IList<NacosPromptVariable>? variables = null) => new()
    {
        PromptKey = promptKey,
        Version = version,
        Template = template,
        Md5 = md5 ?? template,
        Variables = variables,
    };
}
