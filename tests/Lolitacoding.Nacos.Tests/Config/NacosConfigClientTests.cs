using System.Text;
using System.Text.Json;
using Lolitacoding.Nacos.Config;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Generated;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;
using Lolitacoding.Nacos.Tests.Transport;
using Lolitacoding.Nacos.Tests.Support;
using Microsoft.Extensions.Logging;
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

    private static readonly NacosConfigKey CipherKey =
        new("cipher-AES-application.json", Group, "public");

    [Fact]
    public void ServerStatus_IsDownUntilTheConnectionIsEstablished()
    {
        // A health check must not need a request, and before the first connect there is no server
        // to answer one: the answer comes from the transport state.
        using Harness harness = CreateHarness();

        harness.Client.ServerStatus.ShouldBe(NacosServerStatus.Down);
    }

    [Fact]
    public async Task ServerStatus_IsUpWhileTheConnectionIsEstablished()
    {
        using Harness harness = CreateHarness();

        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        harness.Client.ServerStatus.ShouldBe(NacosServerStatus.Up);
    }

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
    public async Task GetConfigConditional_ReusesTheLocalCopyWhenTheServerReportsNoChange()
    {
        // The whole point of the conditional read: a matching hash is answered without the content,
        // and the copy the hash belongs to is returned instead of a second download.
        using Harness harness = CreateHarness(_ => QueryResponse("hello", "json"), snapshots: true);

        NacosConfigResult first = await harness.GetAsync(Key, TestContext.Current.CancellationToken);
        string md5 = first.Md5!;

        harness.Handler = request =>
        {
            PayloadMetadataAssertions.AssertType(request, "ConfigQueryRequest");
            return NotModifiedResponse(md5, "yaml");
        };

        NacosConfigResult result = await harness.GetConditionalAsync(
            Key, md5, TestContext.Current.CancellationToken);

        result.Content.ShouldBe("hello");
        result.Md5.ShouldBe(md5);
        // The metadata the 304 carries describes the version the server confirmed, so it wins over
        // the copy stored beside the content.
        result.ContentType.ShouldBe("yaml");
        result.LastModified.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_100_000));
        // The server answered, so this is not the outage failover the flag exists for.
        result.FromLocalCache.ShouldBeFalse();

        using JsonDocument body = harness.LastRequestBody();
        body.RootElement.GetProperty("localMd5").GetString().ShouldBe(md5);
    }

    [Fact]
    public async Task GetConfigConditional_ReadsTheContentWhenTheHashIsNotTheSnapshot()
    {
        // A hash the client cannot restore content for must not be sent: a 304 would leave the
        // caller with nothing. The server's content is authoritative in that case.
        using Harness harness = CreateHarness(_ => QueryResponse("v1", "text"), snapshots: true);

        await harness.GetAsync(Key, TestContext.Current.CancellationToken);

        harness.Handler = request =>
        {
            using JsonDocument body = JsonDocument.Parse(PayloadCodec.ReadBodyBytes(request));
            body.RootElement.TryGetProperty("localMd5", out _).ShouldBeFalse();
            return QueryResponse("v2", "text");
        };

        NacosConfigResult result = await harness.GetConditionalAsync(
            Key, NacosMd5.Compute("some-other-version"), TestContext.Current.CancellationToken);

        result.Content.ShouldBe("v2");
        result.FromLocalCache.ShouldBeFalse();
    }

    [Fact]
    public async Task GetConfigConditional_ReadsUnconditionallyWithoutASnapshot()
    {
        // Conditional reads depend on a local copy. Without one — no cache directory, or nothing
        // read yet — the hash is not sent and the read behaves like an ordinary one.
        using Harness harness = CreateHarness(
            request =>
            {
                using JsonDocument body = JsonDocument.Parse(PayloadCodec.ReadBodyBytes(request));
                body.RootElement.TryGetProperty("localMd5", out _).ShouldBeFalse();
                return QueryResponse("hello", "text");
            },
            snapshots: true);

        NacosConfigResult result = await harness.GetConditionalAsync(
            Key, NacosMd5.Compute("hello"), TestContext.Current.CancellationToken);

        result.Content.ShouldBe("hello");
    }

    [Fact]
    public async Task GetConfigConditional_AppliesFiltersToTheRestoredCopy()
    {
        // A restored value takes the same path as a downloaded one, so a filter cannot be skipped
        // just because the content came from the snapshot.
        using Harness harness = CreateHarness(_ => QueryResponse("hello", "text"), snapshots: true);

        harness.Client.AddConfigFilter(new DelegateFilter("upper", 0, (context, _) =>
        {
            if (context.Stage == NacosConfigFilterStage.Read)
            {
                context.Content = context.Content?.ToUpperInvariant();
            }

            return ValueTask.CompletedTask;
        }));

        NacosConfigResult first = await harness.GetAsync(Key, TestContext.Current.CancellationToken);
        first.Content.ShouldBe("HELLO");

        string md5 = first.Md5!;
        harness.Handler = _ => NotModifiedResponse(md5);

        NacosConfigResult result = await harness.GetConditionalAsync(
            Key, md5, TestContext.Current.CancellationToken);

        result.Content.ShouldBe("HELLO");
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
    public async Task Listener_ReplayTellsTheServerWhichHashTheClientHolds()
    {
        // The listen request is what lets the server answer "nothing changed for you" instead of
        // declaring every listened configuration changed. The hash it carries has to be the one of
        // the value the client was actually given, which is the initial read.
        using Harness harness = CreateHarness(ListenThenQuery("v1"));

        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncDisposable handle = await harness.Client.AddListenerAsync(
            Key, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        await harness.DropAndReconnectAsync(TestContext.Current.CancellationToken);

        // The reconnect is only finished once the replay has been sent; the handshake alone is not
        // the request this test is about.
        await harness.WaitForRequestAsync(
            "ConfigBatchListenRequest", TestContext.Current.CancellationToken);

        using JsonDocument body = harness.LastRequestBody();
        JsonElement root = body.RootElement;

        root.GetProperty("listen").GetBoolean().ShouldBeTrue();

        JsonElement context = root.GetProperty("configListenContexts")[0];
        context.GetProperty("dataId").GetString().ShouldBe(DataId);
        context.GetProperty("group").GetString().ShouldBe(Group);
        context.GetProperty("md5").GetString().ShouldBe(NacosMd5.Compute("v1"));
    }

    [Fact]
    public async Task Listener_ReceivesAConfigurationThatChangedWhileTheConnectionWasDown()
    {
        // A listener dies with the connection, so nothing can be pushed during an outage and a
        // change would sit unnoticed until something else changed the same configuration. The
        // answer to the replay names every configuration that no longer matches the hash the client
        // holds, and those changes must reach the listener.
        using Harness harness = CreateHarness(ListenThenQuery("v1"));

        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        var changes = new List<NacosConfigChange>();

        await using IAsyncDisposable handle = await harness.Client.AddListenerAsync(
            Key,
            (change, _) =>
            {
                lock (changes)
                {
                    changes.Add(change);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        changes.Count.ShouldBe(1);
        changes[0].Content.ShouldBe("v1");

        // The configuration changes while the client is disconnected, so the push that would have
        // carried it went to nobody.
        harness.Handler = request =>
            PayloadCodec.ReadTypeName(request) == "ConfigBatchListenRequest"
                ? PayloadCodec.Encode(new ConfigChangeBatchListenResponse
                {
                    ChangedConfigs =
                    [
                        new ConfigContext { DataId = DataId, Group = Group, Tenant = "public" },
                    ],
                })
                : QueryResponse("v2", "text");

        await harness.DropAndReconnectAsync(TestContext.Current.CancellationToken);

        await WaitForNotificationsAsync(changes, 2, TestContext.Current.CancellationToken);

        changes[1].Content.ShouldBe("v2");
        changes[1].PreviousMd5.ShouldBe(NacosMd5.Compute("v1"));
    }

    [Fact]
    public async Task Listener_DropsANotificationTheServerReportsAsNotModified()
    {
        // A repeated notification fetches with the hash the listener already delivered, and the
        // server answers 304. That is an unchanged value, not a failed fetch: it must not reach the
        // callback, and it must be logged as ignored rather than as a content-fetch failure.
        var log = new RecordingLoggerFactory();
        using Harness harness = CreateHarness(
            ListenThenQuery("v1"),
            logger: log.CreateLogger<NacosConfigClient>());

        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        var changes = new List<NacosConfigChange>();
        await using IAsyncDisposable handle = await harness.Client.AddListenerAsync(
            Key,
            (change, _) =>
            {
                lock (changes)
                {
                    changes.Add(change);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        changes.Count.ShouldBe(1);

        string md5 = NacosMd5.Compute("v1");
        harness.Handler = request =>
            PayloadCodec.ReadTypeName(request) == "ConfigBatchListenRequest"
                ? PayloadCodec.Encode(new ConfigChangeBatchListenResponse())
                : NotModifiedResponse(md5);

        await harness.PushChangeAsync(Key);

        await WaitForLogAsync(log, 4008, TestContext.Current.CancellationToken);

        // A 304 is not a failed fetch, so the warning must not have been written either.
        log.ForEvent(4029).ShouldBeEmpty();

        lock (changes)
        {
            changes.Count.ShouldBe(1);
        }
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
    public async Task PublishConfig_RunsFiltersInOrder()
    {
        // Filters see the previous filter's mutations, and a lower order runs first regardless of
        // the order the filters were added in.
        var observed = new List<string>();
        using Harness harness = CreateHarness(_ => PayloadCodec.Encode(new ConfigPublishResponse()));

        harness.Client.AddConfigFilter(new DelegateFilter("second", 2, (context, _) =>
        {
            lock (observed)
            {
                observed.Add("second");
            }

            context.Content += "-2";
            return ValueTask.CompletedTask;
        }));

        harness.Client.AddConfigFilter(new DelegateFilter("zeroth", 0, (context, _) =>
        {
            lock (observed)
            {
                observed.Add("zeroth");
            }

            context.Content += "-0";
            return ValueTask.CompletedTask;
        }));

        harness.Client.AddConfigFilter(new DelegateFilter("first", 1, (context, _) =>
        {
            lock (observed)
            {
                observed.Add("first");
            }

            context.Content += "-1";
            return ValueTask.CompletedTask;
        }));

        await harness.ConnectAsync(TestContext.Current.CancellationToken);
        await harness.Client.PublishConfigAsync(
            Key, "content", ct: TestContext.Current.CancellationToken);

        observed.ShouldBe(["zeroth", "first", "second"]);

        using JsonDocument body = harness.LastRequestBody();
        body.RootElement.GetProperty("content").GetString().ShouldBe("content-0-1-2");
    }

    [Fact]
    public async Task PublishConfig_IgnoresASecondFilterWithTheSameName()
    {
        // The Java chain keeps the first registration for a name; a second one would otherwise
        // transform the value twice.
        using Harness harness = CreateHarness(_ => PayloadCodec.Encode(new ConfigPublishResponse()));

        harness.Client.AddConfigFilter(new DelegateFilter("duplicate", 0, (context, _) =>
        {
            context.Content += "-first";
            return ValueTask.CompletedTask;
        }));

        harness.Client.AddConfigFilter(new DelegateFilter("duplicate", 1, (context, _) =>
        {
            context.Content += "-second";
            return ValueTask.CompletedTask;
        }));

        await harness.ConnectAsync(TestContext.Current.CancellationToken);
        await harness.Client.PublishConfigAsync(
            Key, "content", ct: TestContext.Current.CancellationToken);

        using JsonDocument body = harness.LastRequestBody();
        body.RootElement.GetProperty("content").GetString().ShouldBe("content-first");
    }

    [Fact]
    public async Task PublishConfig_SendsTheEncryptedPairForACipherDataId()
    {
        // The server stores the ciphertext and the wrapped key; neither is a top-level field, so
        // both have to travel in the addition map.
        var provider = new FakeEncryptionProvider();
        using Harness harness = CreateHarness(
            _ => PayloadCodec.Encode(new ConfigPublishResponse()),
            configureConfig: config => config.EncryptionProviders.Add(provider));

        await harness.ConnectAsync(TestContext.Current.CancellationToken);
        await harness.Client.PublishConfigAsync(
            CipherKey, "plain", "text", ct: TestContext.Current.CancellationToken);

        using JsonDocument body = harness.LastRequestBody();
        JsonElement root = body.RootElement;

        root.GetProperty("content").GetString().ShouldBe("cipher(plain)");
        root.GetProperty("additionMap").GetProperty("encryptedDataKey").GetString()
            .ShouldBe("wrapped(secret)");
    }

    [Fact]
    public async Task PublishConfig_SendsAnEmptyEncryptedDataKeyWhenNothingEncrypts()
    {
        // The Java client always writes the key, using "" for "not encrypted". The server reads
        // the field unconditionally, so omitting it is not equivalent.
        using Harness harness = CreateHarness(_ => PayloadCodec.Encode(new ConfigPublishResponse()));

        await harness.ConnectAsync(TestContext.Current.CancellationToken);
        await harness.Client.PublishConfigAsync(
            Key, "content", ct: TestContext.Current.CancellationToken);

        using JsonDocument body = harness.LastRequestBody();
        body.RootElement.GetProperty("additionMap").GetProperty("encryptedDataKey").GetString()
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task PublishConfig_ThrowsWhenAFilterRemovesTheContent()
    {
        // Publishing an empty document because a filter returned null would silently wipe the
        // configuration, so it is an error instead.
        using Harness harness = CreateHarness(
            _ => throw new NacosServerException("the publish must not reach the server"));

        harness.Client.AddConfigFilter(new DelegateFilter("removing", 0, (context, _) =>
        {
            context.Content = null;
            return ValueTask.CompletedTask;
        }));

        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(
            () => harness.Client.PublishConfigAsync(
                Key, "content", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PublishConfig_StoresTheEncryptedPairInTheSnapshot()
    {
        // The snapshot has to keep what the server stored, key included, or a failover read would
        // hand ciphertext to the caller.
        var provider = new FakeEncryptionProvider();
        using Harness harness = CreateHarness(
            _ => PayloadCodec.Encode(new ConfigPublishResponse()),
            snapshots: true,
            configureConfig: config => config.EncryptionProviders.Add(provider));

        await harness.ConnectAsync(TestContext.Current.CancellationToken);
        await harness.Client.PublishConfigAsync(
            CipherKey, "published", "text", ct: TestContext.Current.CancellationToken);

        harness.Handler = _ => throw new NacosConnectionException("server is down");

        NacosConfigResult result = await harness.GetAsync(CipherKey, TestContext.Current.CancellationToken);

        result.Content.ShouldBe("published");
        result.FromLocalCache.ShouldBeTrue();
        result.EncryptedDataKey.ShouldBe("wrapped(secret)");
    }

    [Fact]
    public async Task GetConfig_ReturnsTheFilteredValueWithTheStoredHash()
    {
        // The hash describes what the server stored, not what the caller sees. Recomputing it
        // after filtering would make every notification look like a change.
        using Harness harness = CreateHarness(_ => QueryResponse("hello", "text"));

        harness.Client.AddConfigFilter(new DelegateFilter("upper", 0, (context, _) =>
        {
            context.Content = context.Content!.ToUpperInvariant();
            return ValueTask.CompletedTask;
        }));

        NacosConfigResult result = await harness.GetAsync(Key, TestContext.Current.CancellationToken);

        result.Content.ShouldBe("HELLO");
        result.Md5.ShouldBe(NacosMd5.Compute("hello"));
    }

    [Fact]
    public async Task GetConfig_ReturnsDecryptedContentForACipherDataId()
    {
        var provider = new FakeEncryptionProvider();
        using Harness harness = CreateHarness(
            _ => EncryptedQueryResponse("plain"),
            configureConfig: config => config.EncryptionProviders.Add(provider));

        NacosConfigResult result = await harness.GetAsync(
            CipherKey, TestContext.Current.CancellationToken);

        result.Content.ShouldBe("plain");
        result.Md5.ShouldBe(NacosMd5.Compute("cipher(plain)"));

        // The stored key is exposed as-is; the plaintext key never leaves the provider.
        result.EncryptedDataKey.ShouldBe("wrapped(secret)");
    }

    [Fact]
    public async Task GetConfig_DoesNotRunFiltersForAMissingConfiguration()
    {
        using Harness harness = CreateHarness(_ => PayloadCodec.Encode(new ConfigQueryResponse
        {
            ResultCode = ConfigQueryResponse.ConfigNotFound,
            ErrorCode = ConfigQueryResponse.ConfigNotFound,
            Message = "not found",
        }));

        harness.Client.AddConfigFilter(new DelegateFilter("exploding", 0, (_, _) =>
            throw new InvalidOperationException("a filter must not run without content")));

        NacosConfigResult result = await harness.GetAsync(Key, TestContext.Current.CancellationToken);

        result.Exists.ShouldBeFalse();
    }

    [Fact]
    public async Task GetConfig_PropagatesAFilterFailure()
    {
        // A read has a caller to report to, so a broken filter fails the read rather than handing
        // back a value that was never transformed.
        using Harness harness = CreateHarness(_ => QueryResponse("hello", "text"));

        harness.Client.AddConfigFilter(new DelegateFilter("exploding", 0, (_, _) =>
            throw new InvalidOperationException("cannot decrypt")));

        await Should.ThrowAsync<InvalidOperationException>(
            () => harness.GetAsync(Key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetConfig_ServesASnapshotThroughTheFilterChain()
    {
        // The snapshot keeps the stored value, so the same chain has to run on the failover path;
        // otherwise an outage would expose ciphertext.
        var provider = new FakeEncryptionProvider();
        using Harness harness = CreateHarness(
            _ => EncryptedQueryResponse("v1"),
            snapshots: true,
            configureConfig: config => config.EncryptionProviders.Add(provider));

        (await harness.GetAsync(CipherKey, TestContext.Current.CancellationToken))
            .Content.ShouldBe("v1");

        harness.Handler = _ => throw new NacosConnectionException("server is down");

        NacosConfigResult result = await harness.GetAsync(
            CipherKey, TestContext.Current.CancellationToken);

        result.Content.ShouldBe("v1");
        result.FromLocalCache.ShouldBeTrue();
        result.EncryptedDataKey.ShouldBe("wrapped(secret)");
    }

    [Fact]
    public async Task Listener_DeliversTheFilteredValueForAPushedChange()
    {
        var provider = new FakeEncryptionProvider();
        using Harness harness = CreateHarness(
            ListenThenEncryptedQuery("v1"),
            configureConfig: config => config.EncryptionProviders.Add(provider));

        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        var changes = new List<NacosConfigChange>();
        await using IAsyncDisposable handle = await harness.Client.AddListenerAsync(
            CipherKey,
            (change, _) =>
            {
                lock (changes)
                {
                    changes.Add(change);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        changes.ShouldHaveSingleItem().Content.ShouldBe("v1");

        harness.Handler = request => PayloadCodec.ReadTypeName(request) == "ConfigBatchListenRequest"
            ? PayloadCodec.Encode(new ConfigChangeBatchListenResponse())
            : EncryptedQueryResponse("v2");

        await harness.PushChangeAsync(CipherKey);
        await WaitForNotificationsAsync(changes, 2, TestContext.Current.CancellationToken);

        changes[1].Content.ShouldBe("v2");
    }

    [Fact]
    public async Task Listener_KeepsTheSubscriptionWhenAFilterFailsOnAPush()
    {
        // A pushed change has no caller to report to, so a failing filter must be logged and
        // skipped. The subscription has to survive: the next change is still delivered.
        var reachedFilter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using Harness harness = CreateHarness(ListenThenQuery("v1"));

        harness.Client.AddConfigFilter(new DelegateFilter("failing", 0, (context, _) =>
        {
            if (context.Stage == NacosConfigFilterStage.Read && context.Content == "bad")
            {
                reachedFilter.TrySetResult();
                throw new InvalidOperationException("cannot decrypt");
            }

            return ValueTask.CompletedTask;
        }));

        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        var changes = new List<NacosConfigChange>();
        await using IAsyncDisposable handle = await harness.Client.AddListenerAsync(
            Key,
            (change, _) =>
            {
                lock (changes)
                {
                    changes.Add(change);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        harness.Handler = request => PayloadCodec.ReadTypeName(request) == "ConfigBatchListenRequest"
            ? PayloadCodec.Encode(new ConfigChangeBatchListenResponse())
            : QueryResponse("bad", "text");

        await harness.PushChangeAsync(Key);
        await reachedFilter.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        harness.Handler = request => PayloadCodec.ReadTypeName(request) == "ConfigBatchListenRequest"
            ? PayloadCodec.Encode(new ConfigChangeBatchListenResponse())
            : QueryResponse("v2", "text");

        await harness.PushChangeAsync(Key);
        await WaitForNotificationsAsync(changes, 2, TestContext.Current.CancellationToken);

        // The failed value was never delivered; the next genuine change was.
        changes.Count.ShouldBe(2);
        changes[1].Content.ShouldBe("v2");
    }

    [Fact]
    public async Task Listener_LogsItsRegistrationTheNotificationAndTheDelivery()
    {
        var log = new RecordingLoggerFactory();
        using Harness harness = CreateHarness(
            ListenThenQuery("v1"),
            logger: log.CreateLogger<NacosConfigClient>());

        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        var changes = new List<NacosConfigChange>();
        await using IAsyncDisposable handle = await harness.Client.AddListenerAsync(
            Key,
            (change, _) =>
            {
                lock (changes)
                {
                    changes.Add(change);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        changes.ShouldHaveSingleItem().Content.ShouldBe("v1");

        log.ForEvent(4023).ShouldHaveSingleItem().Message.ShouldBe(
            $"Added a listener for configuration {Key}; it now has 1 listener(s).");

        harness.Handler = request => PayloadCodec.ReadTypeName(request) == "ConfigBatchListenRequest"
            ? PayloadCodec.Encode(new ConfigChangeBatchListenResponse())
            : QueryResponse("v2", "text");

        await harness.PushChangeAsync(Key);

        RecordedLogEntry received = await WaitForLogAsync(
            log, 4025, TestContext.Current.CancellationToken);
        received.Message.ShouldBe($"Received a change notification for configuration {Key}.");

        // The delivery is logged after every callback ran, which is later than the callback itself,
        // so it is waited for rather than asserted straight after the push.
        RecordedLogEntry delivered = await WaitForLogAsync(
            log, 4028, TestContext.Current.CancellationToken);
        delivered.Message.ShouldBe($"Delivered the change of {Key} to 1 listener(s).");

        await handle.DisposeAsync();

        log.ForEvent(4024).ShouldHaveSingleItem().Message.ShouldBe(
            $"Removed a listener for configuration {Key}; 0 listener(s) remain.");
    }

    [Fact]
    public async Task Listener_LogsANotificationForAConfigurationNobodyListensTo()
    {
        // A push can arrive after the last listener was removed. Saying so is what separates a
        // dropped notification from one that was never received.
        var log = new RecordingLoggerFactory();
        using Harness harness = CreateHarness(
            _ => QueryResponse("hello", "text"),
            logger: log.CreateLogger<NacosConfigClient>());

        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        var other = new NacosConfigKey("other.json", Group, "public");
        await harness.PushChangeAsync(other);

        RecordedLogEntry entry = await WaitForLogAsync(
            log, 4027, TestContext.Current.CancellationToken);
        entry.Message.ShouldBe(
            $"Ignoring a change notification for {other}: no listener is registered for it.");
    }

    [Fact]
    public async Task Listener_LogsAMalformedNotification()
    {
        var log = new RecordingLoggerFactory();
        using Harness harness = CreateHarness(
            _ => QueryResponse("hello", "text"),
            logger: log.CreateLogger<NacosConfigClient>());

        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await harness.PushNotificationAsync(new ConfigChangeNotifyRequest
        {
            DataId = string.Empty,
            Group = Group,
            Tenant = "public",
        });

        RecordedLogEntry entry = await WaitForLogAsync(
            log, 4026, TestContext.Current.CancellationToken);
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldBe(
            "Ignoring a change notification with missing coordinates (dataId='', "
                + "group='DEFAULT_GROUP').");
    }

    [Fact]
    public async Task Listener_LogsAFailedContentFetch()
    {
        var log = new RecordingLoggerFactory();
        using Harness harness = CreateHarness(
            ListenThenQuery("v1"),
            logger: log.CreateLogger<NacosConfigClient>());

        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncDisposable handle = await harness.Client.AddListenerAsync(
            Key, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        harness.Handler = request => PayloadCodec.ReadTypeName(request) == "ConfigBatchListenRequest"
            ? PayloadCodec.Encode(new ConfigChangeBatchListenResponse())
            : throw new NacosServerException("the fetch failed");

        await harness.PushChangeAsync(Key);

        RecordedLogEntry entry = await WaitForLogAsync(
            log, 4029, TestContext.Current.CancellationToken);
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldBe(
            $"Could not fetch the new content of {Key} after a change notification: "
                + "the fetch failed");
    }

    [Fact]
    public void AddConfigFilter_RejectsANullFilter()
    {
        using Harness harness = CreateHarness();

        Should.Throw<ArgumentNullException>(() => harness.Client.AddConfigFilter(null!));
    }

    [Fact]
    public void AddConfigFilter_RejectsAnEmptyName()
    {
        using Harness harness = CreateHarness();

        Should.Throw<ArgumentException>(
            () => harness.Client.AddConfigFilter(new DelegateFilter(" ", 0, (_, _) =>
                ValueTask.CompletedTask)));
    }

    [Fact]
    public async Task AddConfigFilter_ThrowsAfterDispose()
    {
        using Harness harness = CreateHarness();

        await harness.Client.DisposeAsync();

        Should.Throw<ObjectDisposedException>(
            () => harness.Client.AddConfigFilter(new DelegateFilter("late", 0, (_, _) =>
                ValueTask.CompletedTask)));
    }

    [Fact]
    public async Task RemoveConfig_SendsTheCoordinates()    {
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

    /// <summary>
    /// Builds the <c>304</c> reply the server sends when a conditional read's hash still matches.
    /// </summary>
    /// <param name="md5">The hash of the content the client already holds.</param>
    /// <param name="contentType">The content type the server reports for that version.</param>
    /// <returns>The encoded response, carrying metadata but no content.</returns>
    private static Payload NotModifiedResponse(string md5, string? contentType = "text") =>
        PayloadCodec.Encode(new ConfigQueryResponse
        {
            ResultCode = NacosResponse.FailureCode,
            ErrorCode = ConfigQueryResponse.ConfigNotModified,
            Message = "config not modified, use local cache",
            Md5 = md5,
            ContentType = contentType,
            LastModified = 1_700_000_100_000,
        });

    /// <summary>Answers a listener registration and the read that follows it.</summary>
    /// <param name="content">The content the server holds.</param>
    /// <returns>The scripted handler.</returns>
    /// <remarks>
    /// A registration that is answered with configuration content instead of an acknowledgement
    /// fails the whole registration, so the two request kinds have to be told apart.
    /// </remarks>
    private static Func<Payload, Payload> ListenThenQuery(string content) => request =>
        PayloadCodec.ReadTypeName(request) == "ConfigBatchListenRequest"
            ? PayloadCodec.Encode(new ConfigChangeBatchListenResponse())
            : QueryResponse(content, "text");

    /// <summary>Builds a successful query response for encrypted content.</summary>
    /// <param name="plaintext">The value before encryption.</param>
    /// <returns>The encoded response, carrying the ciphertext and its wrapped key.</returns>
    /// <remarks>
    /// The hash is computed over the ciphertext because that is what the server stores and what
    /// the client verifies before running the filter chain.
    /// </remarks>
    private static Payload EncryptedQueryResponse(string plaintext)
    {
        string ciphertext = string.Concat("cipher(", plaintext, ")");

        return PayloadCodec.Encode(new ConfigQueryResponse
        {
            Content = ciphertext,
            Md5 = NacosMd5.Compute(ciphertext),
            ContentType = "text",
            EncryptedDataKey = "wrapped(secret)",
            LastModified = 1_700_000_000_000,
        });
    }

    /// <summary>Answers a listener registration and the encrypted read that follows it.</summary>
    /// <param name="plaintext">The value the server holds, before encryption.</param>
    /// <returns>The scripted handler.</returns>
    private static Func<Payload, Payload> ListenThenEncryptedQuery(string plaintext) => request =>
        PayloadCodec.ReadTypeName(request) == "ConfigBatchListenRequest"
            ? PayloadCodec.Encode(new ConfigChangeBatchListenResponse())
            : EncryptedQueryResponse(plaintext);

    /// <summary>Waits until a listener has been notified a given number of times.</summary>
    /// <param name="changes">The notifications observed so far.</param>
    /// <param name="expected">How many are expected.</param>
    /// <param name="ct">A token that cancels the wait.</param>
    /// <returns>A task that completes once the expected number was reached.</returns>
    private static async Task WaitForNotificationsAsync(
        List<NacosConfigChange> changes,
        int expected,
        CancellationToken ct)
    {
        for (int i = 0; i < 1000; i++)
        {
            lock (changes)
            {
                if (changes.Count >= expected)
                {
                    return;
                }
            }

            await Task.Delay(10, ct);
        }

        throw new TimeoutException("The listener was never notified of the missed change.");
    }

    /// <summary>Waits until an event was logged.</summary>
    /// <param name="log">The recording factory.</param>
    /// <param name="eventId">The event id to wait for.</param>
    /// <param name="ct">A token that cancels the wait.</param>
    /// <returns>The first entry logged for the event.</returns>
    private static async Task<RecordedLogEntry> WaitForLogAsync(
        RecordingLoggerFactory log,
        int eventId,
        CancellationToken ct)
    {
        for (int i = 0; i < 1000; i++)
        {
            IReadOnlyList<RecordedLogEntry> entries = log.ForEvent(eventId);

            if (entries.Count > 0)
            {
                return entries[0];
            }

            await Task.Delay(10, ct);
        }

        throw new TimeoutException($"Event {eventId} was not logged within the timeout.");
    }

    private static Harness CreateHarness(
        Func<Payload, Payload>? handler = null,
        string @namespace = "public",
        bool verifyMd5 = true,
        bool snapshots = false,
        Action<NacosConfigOptions>? configureConfig = null,
        ILogger<NacosConfigClient>? logger = null) =>
        new(
            handler ?? (_ => QueryResponse("hello", "text")),
            @namespace,
            verifyMd5,
            snapshots,
            configureConfig,
            logger);

    /// <summary>
    /// A filter whose body is supplied by the test.
    /// </summary>
    private sealed class DelegateFilter(
        string name,
        int order,
        Func<NacosConfigFilterContext, CancellationToken, ValueTask> invoke) : INacosConfigFilter
    {
        public string Name => name;

        public int Order => order;

        public ValueTask InvokeAsync(NacosConfigFilterContext context, CancellationToken ct) =>
            invoke(context, ct);
    }

    /// <summary>
    /// Wires a config client to a fake transport and provides request inspection.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly List<FakeNacosChannel> _channels = [];
        private readonly Queue<FakeNacosDuplexStream> _streams;
        private readonly string _snapshotRoot;

        private FakeNacosDuplexStream _currentStream = null!;

        public Harness(
            Func<Payload, Payload> handler,
            string @namespace,
            bool verifyMd5,
            bool snapshots,
            Action<NacosConfigOptions>? configureConfig = null,
            ILogger<NacosConfigClient>? logger = null)
        {
            Handler = handler;
            _snapshotRoot = TestPaths.CreateScratchDirectory("config-client");

            // A supply of streams so a test can drop one and let the reconnect replace it, the same
            // way the transport tests exercise recovery.
            _streams = new Queue<FakeNacosDuplexStream>(
                Enumerable.Range(0, 4).Select(_ => new FakeNacosDuplexStream()));

            var factory = new FakeNacosChannelFactory(address =>
            {
                var channel = new FakeNacosChannel(
                    address,
                    // The handshake must always be answered, or nothing else can be exercised;
                    // every other request goes to the test's scripted server.
                    request => PayloadCodec.ReadTypeName(request) == "ServerCheckRequest"
                        ? FakeServerCheck.Responding()(request)
                        // Routed through the property rather than closing over the argument so a
                        // test can swap the server's behaviour midway.
                        : Handler(request),
                    NextStream());

                _channels.Add(channel);
                return channel;
            });

            var options = new NacosOptions
            {
                ServerAddresses = ["127.0.0.1:8848"],
                Namespace = @namespace,
                Config = new NacosConfigOptions { VerifyMd5 = verifyMd5 },
                Reconnect = new NacosReconnectOptions
                {
                    InitialDelay = TimeSpan.FromMilliseconds(1),
                    MaxDelay = TimeSpan.FromMilliseconds(2),
                    JitterFactor = 0,
                },
            };

            configureConfig?.Invoke(options.Config);

            var connection = new NacosGrpcConnection(
                options,
                NacosConnectionModule.Config,
                [NacosServerAddress.Parse("127.0.0.1:8848")],
                factory,
                TimeProvider.System,
                NullLogger.Instance);

            var snapshotStore = snapshots ? new NacosConfigSnapshotStore(_snapshotRoot) : null;

            Client = new NacosConfigClient(
                options,
                connection,
                TimeProvider.System,
                logger ?? NullLogger<NacosConfigClient>.Instance,
                snapshotStore);

            // The production factory does this wiring; without it a pushed notification would be
            // acknowledged but never turned into a listener callback.
            connection.ServerRequestReceived += (request, ct) =>
            {
                if (request is ConfigChangeNotifyRequest notification)
                {
                    // Deliberately not awaited: the fetch needs the read loop to keep running.
                    _ = Client.OnConfigChangedAsync(notification, ct);
                    return Task.FromResult<NacosResponse?>(
                        new ConfigChangeNotifyResponse { RequestId = notification.RequestId });
                }

                return Task.FromResult<NacosResponse?>(null);
            };

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

        /// <summary>Reads a configuration conditionally, connecting the transport first.</summary>
        /// <param name="key">The configuration to read.</param>
        /// <param name="localMd5">The hash the caller already holds.</param>
        /// <param name="ct">A token that cancels the operation.</param>
        /// <returns>The result.</returns>
        public async Task<NacosConfigResult> GetConditionalAsync(
            NacosConfigKey key,
            string localMd5,
            CancellationToken ct)
        {
            await ConnectAsync(ct);
            return await Client.GetConfigConditionalAsync(key, localMd5, ct);
        }

        /// <summary>
        /// Connects the transport, which every request needs before it can be sent.
        /// </summary>
        /// <param name="ct">A token that cancels the operation.</param>
        /// <returns>A task that completes once connected.</returns>
        public Task ConnectAsync(CancellationToken ct) => Connection.ConnectAsync(ct);

        /// <summary>Drops the current connection and waits for the reconnect to replace it.</summary>
        /// <param name="ct">A token that cancels the wait.</param>
        /// <returns>A task that completes once a fresh channel is in use.</returns>
        public async Task DropAndReconnectAsync(CancellationToken ct)
        {
            int channelsBefore = _channels.Count;
            _currentStream.EndStream();

            await WaitForAsync(() => _channels.Count > channelsBefore, ct);

            // Wait for the fresh channel to have completed a handshake before returning.
            await WaitForAsync(() => _channels[^1].UnaryRequests.Count >= 1, ct);
        }

        /// <summary>Gets the JSON body of the most recent unary request.</summary>
        /// <returns>The parsed body.</returns>
        public JsonDocument LastRequestBody()
        {
            Payload payload = _channels[^1].UnaryRequests[^1];
            return JsonDocument.Parse(PayloadCodec.ReadBodyBytes(payload));
        }

        /// <summary>Waits until the current channel has sent a request of a given payload type.</summary>
        /// <param name="typeName">The <c>metadata.type</c> to wait for.</param>
        /// <param name="ct">A token that cancels the wait.</param>
        /// <returns>A task that completes once such a request was recorded.</returns>
        public Task WaitForRequestAsync(string typeName, CancellationToken ct) =>
            WaitForAsync(
                () => _channels[^1].UnaryRequests.Any(
                    request => PayloadCodec.ReadTypeName(request) == typeName),
                ct);

        /// <summary>Pushes a change notification for a configuration, as the server would.</summary>
        /// <param name="key">The configuration that changed.</param>
        /// <returns>A task that completes once the frame was handed to the transport.</returns>
        public Task PushChangeAsync(NacosConfigKey key) =>
            PushNotificationAsync(new ConfigChangeNotifyRequest
            {
                DataId = key.DataId,
                Group = key.Group,
                Tenant = key.Namespace,
            });

        /// <summary>Pushes a raw change notification, as the server would.</summary>
        /// <param name="notification">The notification to push.</param>
        /// <returns>A task that completes once the frame was handed to the transport.</returns>
        public Task PushNotificationAsync(ConfigChangeNotifyRequest notification)
        {
            _currentStream.Push(TestPayload.ForRequest(notification));

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            Client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Connection.DisposeAsync().AsTask().GetAwaiter().GetResult();

            TestPaths.TryDelete(_snapshotRoot);
        }

        /// <summary>Waits until a condition holds, or throws when it never does.</summary>
        /// <param name="condition">The condition to wait for.</param>
        /// <param name="ct">A token that cancels the wait.</param>
        /// <returns>A task that completes once the condition holds.</returns>
        private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
        {
            for (int i = 0; i < 1000; i++)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(10, ct);
            }

            throw new TimeoutException("The expected condition was not met within the timeout.");
        }

        private FakeNacosDuplexStream NextStream()
        {
            _currentStream = _streams.Count > 0 ? _streams.Dequeue() : new FakeNacosDuplexStream();
            return _currentStream;
        }
    }
}

