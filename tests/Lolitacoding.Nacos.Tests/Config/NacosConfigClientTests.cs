using System.Text;
using System.Text.Json;
using Lolitacoding.Nacos.Config;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Generated;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;
using Lolitacoding.Nacos.Tests.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.Tests.Config;

/// <summary>
/// Verifies the configuration client against a scripted in-memory server.
/// </summary>
public sealed class NacosConfigClientTests
{
    private const string DataId = "application.json";
    private const string Group = "DEFAULT_GROUP";

    private static readonly NacosConfigKey Key = new(DataId, Group, "public");

    [Fact]
    public async Task GetConfig_ReturnsTheContentAndHash()
    {
        using Harness harness = CreateHarness(request =>
        {
            PayloadMetadataAssertions.AssertType(request, "ConfigQueryRequest");
            return QueryResponse("hello", "json");
        });

        NacosConfigResult result = await harness.GetAsync(Key, TestContext.Current.CancellationToken);

        result.Content.ShouldBe("hello");
        result.ContentType.ShouldBe("json");
        result.Exists.ShouldBeTrue();
        result.Md5.ShouldBe(NacosMd5.Compute("hello"));
    }

    [Fact]
    public async Task GetConfig_SendsNamespaceGroupAndDataId()
    {
        using Harness harness = CreateHarness(_ => QueryResponse("hello", "text"));

        await harness.GetAsync(Key, TestContext.Current.CancellationToken);

        using JsonDocument body = harness.LastRequestBody();
        JsonElement root = body.RootElement;

        root.GetProperty("dataId").GetString().ShouldBe(DataId);
        root.GetProperty("group").GetString().ShouldBe(Group);
        // The protocol calls the namespace "tenant" on the wire.
        root.GetProperty("tenant").GetString().ShouldBe("public");
    }

    [Fact]
    public async Task GetConfig_AppliesTheConfiguredDefaultNamespace()
    {
        // A key without a namespace must inherit the configured one rather than sending nothing,
        // which the server would interpret as the public namespace.
        using Harness harness = CreateHarness(_ => QueryResponse("hello", "text"), @namespace: "tenant-a");

        await harness.GetAsync(new NacosConfigKey(DataId, Group), TestContext.Current.CancellationToken);

        using JsonDocument body = harness.LastRequestBody();
        body.RootElement.GetProperty("tenant").GetString().ShouldBe("tenant-a");
    }

    [Fact]
    public async Task GetConfig_ReportsNotFoundWithoutThrowing()
    {
        // A configuration that has not been created yet is an ordinary state, not a failure.
        using Harness harness = CreateHarness(_ => PayloadCodec.Encode(new ConfigQueryResponse
        {
            ResultCode = ConfigQueryResponse.ConfigNotFound,
            ErrorCode = ConfigQueryResponse.ConfigNotFound,
            Message = "not found",
        }));

        NacosConfigResult result = await harness.GetAsync(Key, TestContext.Current.CancellationToken);

        result.Exists.ShouldBeFalse();
        result.Content.ShouldBeNull();
    }

    [Fact]
    public async Task GetConfig_RejectsContentThatFailsItsIntegrityCheck()
    {
        // Applying content whose hash does not match what the server reported would silently
        // misconfigure the application, so it must fail loudly instead.
        using Harness harness = CreateHarness(request =>
        {
            PayloadMetadataAssertions.AssertType(request, "ConfigQueryRequest");
            return PayloadCodec.Encode(new ConfigQueryResponse
            {
                Content = "actual",
                Md5 = NacosMd5.Compute("something-else"),
                ContentType = "text",
            });
        });

        await Should.ThrowAsync<NacosServerException>(
            () => harness.GetAsync(Key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetConfig_AcceptsContentWhenVerificationIsDisabled()
    {
        using Harness harness = CreateHarness(request => PayloadCodec.Encode(new ConfigQueryResponse
        {
            Content = "actual",
            Md5 = "a-hash-from-somewhere-else",
            ContentType = "text",
        }), verifyMd5: false);

        NacosConfigResult result = await harness.GetAsync(Key, TestContext.Current.CancellationToken);

        result.Content.ShouldBe("actual");
    }

    [Fact]
    public async Task GetConfig_ComputesTheHashWhenTheServerOmitsIt()
    {
        using Harness harness = CreateHarness(request => PayloadCodec.Encode(new ConfigQueryResponse
        {
            Content = "hello",
            ContentType = "text",
        }));

        NacosConfigResult result = await harness.GetAsync(Key, TestContext.Current.CancellationToken);

        result.Md5.ShouldBe(NacosMd5.Compute("hello"));
    }

    [Fact]
    public async Task GetConfig_ServesASnapshotWhenTheServerIsUnreachable()
    {
        // This is the failover path: a process that restarts during a control-plane outage must
        // still see the configuration it was last given.
        using Harness harness = CreateHarness(snapshots: true);

        // Seed the snapshot through a successful read first.
        harness.Handler = _ => QueryResponse("from-snapshot", "text");
        await harness.GetAsync(Key, TestContext.Current.CancellationToken);

        harness.Handler = _ => throw new NacosConnectionException("server is down");

        NacosConfigResult result = await harness.GetAsync(Key, TestContext.Current.CancellationToken);

        result.Content.ShouldBe("from-snapshot");
        result.FromLocalCache.ShouldBeTrue();
    }

    [Fact]
    public async Task GetConfig_ThrowsWhenThereIsNoSnapshotToFallBackOn()
    {
        using Harness harness = CreateHarness(
            _ => throw new NacosConnectionException("server is down"), snapshots: true);

        await Should.ThrowAsync<NacosConnectionException>(
            () => harness.GetAsync(Key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PublishConfig_SendsContentTypeInsideTheAdditionMap()
    {
        // The server reads the content type out of the free-form addition map under "type"; it is
        // not a field of its own on the wire.
        using Harness harness = CreateHarness(request =>
        {
            PayloadMetadataAssertions.AssertType(request, "ConfigPublishRequest");
            return PayloadCodec.Encode(new ConfigPublishResponse());
        });

        await harness.ConnectAsync(TestContext.Current.CancellationToken);
        await harness.Client.PublishConfigAsync(
            Key, "content", "json", ct: TestContext.Current.CancellationToken);

        using JsonDocument body = harness.LastRequestBody();
        JsonElement root = body.RootElement;

        root.GetProperty("content").GetString().ShouldBe("content");
        root.GetProperty("additionMap").GetProperty("type").GetString().ShouldBe("json");
    }

    [Fact]
    public async Task PublishConfig_AppliesTheConfiguredDefaultContentType()
    {
        using Harness harness = CreateHarness(_ => PayloadCodec.Encode(new ConfigPublishResponse()));

        await harness.ConnectAsync(TestContext.Current.CancellationToken);
        await harness.Client.PublishConfigAsync(Key, "content", ct: TestContext.Current.CancellationToken);

        using JsonDocument body = harness.LastRequestBody();
        body.RootElement.GetProperty("additionMap").GetProperty("type").GetString()
            .ShouldBe(NacosConstants.DefaultConfigType);
    }

    [Fact]
    public async Task PublishConfig_ForwardsTheCompareAndSwapGuard()
    {
        using Harness harness = CreateHarness(_ => PayloadCodec.Encode(new ConfigPublishResponse()));

        await harness.ConnectAsync(TestContext.Current.CancellationToken);
        await harness.Client.PublishConfigAsync(
            Key, "content", casMd5: "expected-md5", ct: TestContext.Current.CancellationToken);

        using JsonDocument body = harness.LastRequestBody();
        body.RootElement.GetProperty("casMd5").GetString().ShouldBe("expected-md5");
    }

    [Fact]
    public async Task PublishConfig_ThrowsWhenTheServerRejectsIt()
    {
        // A real response type carrying an error, rather than a test-only one: the payload codec
        // only knows how to serialize types the protocol actually defines.
        using Harness harness = CreateHarness(_ => PayloadCodec.Encode(new ConfigPublishResponse
        {
            ResultCode = NacosResponse.FailureCode,
            ErrorCode = 20005,
            Message = "conflict",
        }));

        NacosServerException ex = await Should.ThrowAsync<NacosServerException>(async () =>
        {
            await harness.ConnectAsync(TestContext.Current.CancellationToken);
            await harness.Client.PublishConfigAsync(
                Key, "content", ct: TestContext.Current.CancellationToken);
        });

        // The server's numeric code is preserved so callers can branch on it.
        ex.ErrorCode.ShouldBe(20005);
    }

    [Fact]
    public async Task PublishConfig_UpdatesTheSnapshot()
    {
        using Harness harness = CreateHarness(
            _ => PayloadCodec.Encode(new ConfigPublishResponse()), snapshots: true);

        await harness.ConnectAsync(TestContext.Current.CancellationToken);
        await harness.Client.PublishConfigAsync(
            Key, "published", ct: TestContext.Current.CancellationToken);

        // A publish is now the newest known value, so a later outage must serve it rather than an
        // older value that happened to be cached.
        harness.Handler = _ => throw new NacosConnectionException("server is down");

        NacosConfigResult result = await harness.GetAsync(Key, TestContext.Current.CancellationToken);

        result.Content.ShouldBe("published");
        result.FromLocalCache.ShouldBeTrue();
    }

    [Fact]
    public async Task RemoveConfig_SendsTheCoordinates()
    {
        using Harness harness = CreateHarness(request =>
        {
            PayloadMetadataAssertions.AssertType(request, "ConfigRemoveRequest");
            return PayloadCodec.Encode(new ConfigRemoveResponse());
        });

        await harness.ConnectAsync(TestContext.Current.CancellationToken);
        await harness.Client.RemoveConfigAsync(Key, TestContext.Current.CancellationToken);

        using JsonDocument body = harness.LastRequestBody();
        body.RootElement.GetProperty("dataId").GetString().ShouldBe(DataId);
        body.RootElement.GetProperty("group").GetString().ShouldBe(Group);
    }

    [Fact]
    public async Task RemoveConfig_DropsTheSnapshot()
    {
        // Keeping a deleted value on disk would resurrect it during the next outage.
        using Harness harness = CreateHarness(_ => QueryResponse("doomed", "text"), snapshots: true);

        await harness.GetAsync(Key, TestContext.Current.CancellationToken);

        harness.Handler = _ => PayloadCodec.Encode(new ConfigRemoveResponse());
        await harness.ConnectAsync(TestContext.Current.CancellationToken);
        await harness.Client.RemoveConfigAsync(Key, TestContext.Current.CancellationToken);

        harness.Handler = _ => throw new NacosConnectionException("server is down");

        await Should.ThrowAsync<NacosConnectionException>(
            () => harness.GetAsync(Key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetConfig_ThrowsAfterDispose()
    {
        using Harness harness = CreateHarness(_ => QueryResponse("hello", "text"));

        await harness.Client.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(
            () => harness.GetAsync(Key, TestContext.Current.CancellationToken));
    }

    /// <summary>Builds a successful query response for the given content.</summary>
    private static Payload QueryResponse(string content, string? contentType) =>
        PayloadCodec.Encode(new ConfigQueryResponse
        {
            Content = content,
            Md5 = NacosMd5.Compute(content),
            ContentType = contentType,
            LastModified = 1_700_000_000_000,
        });

    private static Harness CreateHarness(
        Func<Payload, Payload>? handler = null,
        string @namespace = "public",
        bool verifyMd5 = true,
        bool snapshots = false) =>
        new(handler ?? (_ => QueryResponse("hello", "text")), @namespace, verifyMd5, snapshots);

    /// <summary>
    /// Wires a config client to a fake transport and provides request inspection.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly List<ConfigQueryResponse> _responses = [];
        private readonly FakeNacosChannel _channel;
        private readonly string _snapshotRoot;

        public Harness(
            Func<Payload, Payload> handler,
            string @namespace,
            bool verifyMd5,
            bool snapshots)
        {
            Handler = handler;
            _snapshotRoot = TestPaths.CreateScratchDirectory("config-client");

            var stream = new FakeNacosDuplexStream();
            Func<Payload, Payload> serverCheck = FakeServerCheck.Responding();

            _channel = new FakeNacosChannel(
                NacosServerAddress.Parse("127.0.0.1:8848"),
                // The handshake must always be answered, or nothing else can be exercised; every
                // other request goes to the test's scripted server.
                request => PayloadCodec.ReadTypeName(request) == "ServerCheckRequest"
                    ? serverCheck(request)
                    // Routed through the property rather than closing over the argument so a test
                    // can swap the server's behaviour midway.
                    : Handler(request),
                stream);

            var options = new NacosOptions
            {
                ServerAddresses = ["127.0.0.1:8848"],
                Namespace = @namespace,
                Config = new NacosConfigOptions { VerifyMd5 = verifyMd5 },
            };

            var connection = new NacosGrpcConnection(
                options,
                NacosConnectionModule.Config,
                [NacosServerAddress.Parse("127.0.0.1:8848")],
                new FakeNacosChannelFactory(_ => _channel),
                TimeProvider.System,
                NullLogger.Instance);

            var snapshotStore = snapshots ? new NacosConfigSnapshotStore(_snapshotRoot) : null;

            Client = new NacosConfigClient(
                options, connection, TimeProvider.System, NullLogger<NacosConfigClient>.Instance, snapshotStore);

            Connection = connection;
        }

        /// <summary>Gets or sets the request handler, so a test can change behaviour midway.</summary>
        public Func<Payload, Payload> Handler { get; set; }

        public NacosConfigClient Client { get; }

        private NacosGrpcConnection Connection { get; }

        /// <summary>Reads a configuration, connecting the transport first.</summary>
        /// <param name="key">The configuration to read.</param>
        /// <param name="ct">A token that cancels the operation.</param>
        /// <returns>The result.</returns>
        public async Task<NacosConfigResult> GetAsync(NacosConfigKey key, CancellationToken ct)
        {
            await ConnectAsync(ct);
            return await Client.GetConfigAsync(key, ct);
        }

        /// <summary>
        /// Connects the transport, which every request needs before it can be sent.
        /// </summary>
        /// <param name="ct">A token that cancels the operation.</param>
        /// <returns>A task that completes once connected.</returns>
        public Task ConnectAsync(CancellationToken ct) => Connection.ConnectAsync(ct);

        /// <summary>Gets the JSON body of the most recent unary request.</summary>
        /// <returns>The parsed body.</returns>
        public JsonDocument LastRequestBody()
        {
            Payload payload = _channel.UnaryRequests[^1];
            return JsonDocument.Parse(PayloadCodec.ReadBodyBytes(payload));
        }

        public void Dispose()
        {
            Client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Connection.DisposeAsync().AsTask().GetAwaiter().GetResult();

            TestPaths.TryDelete(_snapshotRoot);
        }
    }
}

