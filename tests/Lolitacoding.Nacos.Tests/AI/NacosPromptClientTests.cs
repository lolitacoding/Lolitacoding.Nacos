using System.Net;
using Lolitacoding.Nacos.AI;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Tests.Transport;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lolitacoding.Nacos.Tests.AI;

/// <summary>
/// Verifies the prompt registry client's wire contract and its guard rails against a scripted AI
/// service.
/// </summary>
/// <remarks>
/// <para>
/// The contract worth pinning down is the part the server reads literally: the module label, the
/// selector a read carries, and the version resolution the server owns. The client deliberately
/// makes no resolution of its own — a version and a label are both sent and the server decides —
/// so the tests assert what was sent rather than what a client-side rule would have chosen.
/// </para>
/// <para>
/// The transport choice is the second contract: the same read has two wire shapes, and the tests
/// pin both — the request the HTTP endpoint is given, the status codes it answers with, and the
/// promise that <see cref="NacosAITransportMode.Http"/> never opens the AI gRPC connection.
/// </para>
/// </remarks>
public sealed class NacosPromptClientTests
{
    [Fact]
    public async Task GetPromptAsync_AsksForTheNamedSelectorsInTheConfiguredNamespace()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient(new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "ai-tests",
            Namespace = "staging",
        });

        server.OnPrompt = _ => AITest.Prompt(AITest.CreatePrompt("greeting", "1.0.0", "Hello"));

        await client.Prompt.GetPromptAsync(
            "greeting",
            "1.2.3",
            "stable",
            TestContext.Current.CancellationToken);

        QueryPromptRequest request = server.Prompts[0];
        request.PromptKey.ShouldBe("greeting");
        request.Version.ShouldBe("1.2.3");
        request.Label.ShouldBe("stable");
        request.NamespaceId.ShouldBe("staging");

        // The query travels on the unary channel method, not on the duplex stream the setup and the
        // server's pushes use.
        PayloadCodec.ReadTypeName(server.Channel.UnaryRequests[^1]).ShouldBe("QueryPromptRequest");
    }

    [Fact]
    public async Task GetPromptAsync_LeavesTheSelectorsUnsetForTheLatestVersion()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        server.OnPrompt = _ => AITest.Prompt(AITest.CreatePrompt("greeting", "1.0.0", "Hello"));

        await client.Prompt.GetPromptAsync("greeting", ct: TestContext.Current.CancellationToken);

        // A client that asked for "latest" by name would need to know which label the server uses;
        // leaving both selectors unset lets the server answer with whatever it currently serves.
        server.Prompts[0].Version.ShouldBeNull();
        server.Prompts[0].Label.ShouldBeNull();
    }

    [Fact]
    public async Task GetPromptAsync_SendsNoHashSoTheContentComesBackUnconditionally()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        server.OnPrompt = _ => AITest.Prompt(AITest.CreatePrompt("greeting", "1.0.0", "Hello"));

        await client.Prompt.GetPromptAsync("greeting", ct: TestContext.Current.CancellationToken);

        // A read is unconditional: the caller asked for the prompt, so there is nothing that could be
        // answered with "not modified".
        server.Prompts[0].Md5.ShouldBeNull();
    }

    [Fact]
    public async Task GetPromptAsync_ReadsThePromptTheServerReturns()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        server.OnPrompt = _ => AITest.Prompt(new NacosPrompt
        {
            PromptKey = "greeting",
            Version = "1.2.3",
            Template = "Hello {{name}}",
            Md5 = "0f1e2d",
            Variables =
            [
                new NacosPromptVariable
                {
                    Name = "name",
                    DefaultValue = "Nacos",
                    Description = "Who to greet",
                },
            ],
        });

        NacosPrompt prompt = await client.Prompt.GetPromptAsync(
            "greeting",
            ct: TestContext.Current.CancellationToken);

        prompt.Version.ShouldBe("1.2.3");
        prompt.Template.ShouldBe("Hello {{name}}");
        prompt.Md5.ShouldBe("0f1e2d");
        prompt.Variables!.Single().ShouldSatisfyAllConditions(
            variable => variable.Name.ShouldBe("name"),
            variable => variable.DefaultValue.ShouldBe("Nacos"),
            variable => variable.Description.ShouldBe("Who to greet"));
    }

    [Fact]
    public async Task GetPromptAsync_RejectsABlankKeyBeforeSendingAnything()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        await Should.ThrowAsync<ArgumentException>(
            () => client.Prompt.GetPromptAsync("  ", ct: TestContext.Current.CancellationToken));

        server.Factory.RequestedAddresses.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetPromptAsync_SurfacesTheErrorCodeTheServerReported()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        server.OnPrompt = _ => AITest.PromptFailure(404, "Prompt not found: greeting");

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Prompt.GetPromptAsync("greeting", ct: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(404);
        exception.Message.ShouldContain("not found");
    }

    [Fact]
    public async Task GetPromptAsync_SurfacesTheNotModifiedCodeRatherThanAnEmptyPrompt()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        // 304 is what a conditional read is answered with; a channel that seeded from a cached hash
        // would see it, and turning it into an empty prompt would report a change that did not happen.
        server.OnPrompt = _ => AITest.PromptFailure(304, "prompt data is up to date");

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Prompt.GetPromptAsync("greeting", ct: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(304);
    }

    [Fact]
    public async Task GetPromptAsync_ReportsASuccessWithNoPromptAsAProtocolViolation()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        // A missing prompt and an unchanged hash are both reported as failure codes, so a success
        // with no prompt would be a contract break rather than a missing resource.
        server.OnPrompt = _ => new QueryPromptResponse();

        await Should.ThrowAsync<NacosServerException>(
            () => client.Prompt.GetPromptAsync("greeting", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetPromptAsync_FailsAfterDispose()
    {
        var server = new AITestServer();
        NacosAIClient client = server.CreateClient();

        await client.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(
            () => client.Prompt.GetPromptAsync("greeting", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetPromptAsync_OverHttp_ReadsTheClientEndpointWithTheSelectors()
    {
        var http = new AIHttpTestServer((_, _) =>
            AIHttpTestServer.Prompt("greeting", "1.2.3", "Hello {{name}}"));

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "ai-tests",
            Namespace = "staging",
            AI = new NacosAIOptions { TransportMode = NacosAITransportMode.Http },
        });

        NacosPrompt prompt = await client.Prompt.GetPromptAsync(
            "greeting",
            "1.2.3",
            "stable",
            TestContext.Current.CancellationToken);

        prompt.PromptKey.ShouldBe("greeting");
        prompt.Version.ShouldBe("1.2.3");
        prompt.Template.ShouldBe("Hello {{name}}");

        http.Requests[0].AbsolutePath.ShouldEndWith("/v3/client/ai/prompt");
        http.Query(0, "namespaceId").ShouldBe("staging");
        http.Query(0, "promptKey").ShouldBe("greeting");
        http.Query(0, "version").ShouldBe("1.2.3");
        http.Query(0, "label").ShouldBe("stable");

        // A plain read wants the content, so there is nothing to be answered with "not modified".
        http.Query(0, "md5").ShouldBeNull();

        // HTTP means HTTP: the AI connection is never opened, which is what makes the registry
        // usable behind an ingress that only exposes the client OpenAPI.
        server.Factory.RequestedAddresses.ShouldBeEmpty();
        server.Prompts.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetPromptAsync_OverHttp_LeavesBothSelectorsUnsetForTheLatestVersion()
    {
        var http = new AIHttpTestServer((_, _) =>
            AIHttpTestServer.Prompt("greeting", "1.0.0", "Hello"));

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "ai-tests",
            AI = new NacosAIOptions { TransportMode = NacosAITransportMode.Http },
        });

        await client.Prompt.GetPromptAsync("greeting", ct: TestContext.Current.CancellationToken);

        // Both transports leave the selectors unset rather than naming "latest": which label is
        // current is the server's answer, not the client's.
        http.Query(0, "version").ShouldBeNull();
        http.Query(0, "label").ShouldBeNull();
    }

    [Fact]
    public async Task GetPromptAsync_OverHttp_MapsAMissingPromptToTheNotFoundCode()
    {
        var http = new AIHttpTestServer((_, _) =>
            AIHttpTestServer.NotFound("Prompt not found: greeting"));

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "ai-tests",
            AI = new NacosAIOptions { TransportMode = NacosAITransportMode.Http },
        });

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Prompt.GetPromptAsync("greeting", ct: TestContext.Current.CancellationToken));

        // The subscription cache decides what a missing prompt means by the code, not by the
        // transport, so HTTP has to report the same code gRPC does.
        exception.ErrorCode.ShouldBe(404);
    }

    [Fact]
    public async Task GetPromptAsync_OverHttp_MapsAnUnchangedHashToTheNotModifiedCode()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.NotModified());

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "ai-tests",
            AI = new NacosAIOptions { TransportMode = NacosAITransportMode.Http },
        });

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Prompt.GetPromptAsync("greeting", ct: TestContext.Current.CancellationToken));

        // 304 is a successful answer with no body, and turning it into an empty prompt would
        // report a change that did not happen. The refresh loop is what normally sees it.
        exception.ErrorCode.ShouldBe(304);
    }

    [Fact]
    public async Task GetPromptAsync_WithAGrpcOverride_UsesGrpcWhileTheGlobalTransportIsHttp()
    {
        var http = new AIHttpTestServer((_, _) =>
            AIHttpTestServer.Prompt("greeting", "1.0.0", "Hello"));

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "ai-tests",
            AI = new NacosAIOptions
            {
                TransportMode = NacosAITransportMode.Http,
                PromptTransportMode = NacosAITransportMode.Grpc,
            },
        });

        server.OnPrompt = _ => AITest.Prompt(AITest.CreatePrompt("greeting", "1.0.0", "Hello"));

        await client.Prompt.GetPromptAsync("greeting", ct: TestContext.Current.CancellationToken);

        // The per-module value wins, which is the point of having one: a deployment can move a
        // single resource without moving the rest.
        server.Prompts.Count.ShouldBe(1);
        http.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetPromptAsync_InAutoMode_FallsBackToHttpWhenGrpcCannotConnect()
    {
        var http = new AIHttpTestServer((_, _) =>
            AIHttpTestServer.Prompt("greeting", "1.0.0", "Hello"));

        int attempts = 0;
        await using NacosAIClient client = CreateClient(
            NacosAITransportMode.Auto,
            http,
            _ =>
            {
                attempts++;
                throw new NacosConnectionException("The AI connection is down.");
            });

        NacosPrompt prompt = await client.Prompt.GetPromptAsync(
            "greeting", ct: TestContext.Current.CancellationToken);

        prompt.Template.ShouldBe("Hello");

        // Auto is gRPC first: the fallback only happens after the attempt failed, so the HTTP
        // request is the second thing the read did rather than the only thing.
        attempts.ShouldBe(1);
        http.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetPromptAsync_InAutoMode_DoesNotRetryTheFailedGrpcAttemptOnEveryRead()
    {
        var http = new AIHttpTestServer((_, _) =>
            AIHttpTestServer.Prompt("greeting", "1.0.0", "Hello"));

        int attempts = 0;
        await using NacosAIClient client = CreateClient(
            NacosAITransportMode.Auto,
            http,
            _ =>
            {
                attempts++;
                throw new NacosConnectionException("The AI connection is down.");
            });

        await client.Prompt.GetPromptAsync("greeting", ct: TestContext.Current.CancellationToken);
        await client.Prompt.GetPromptAsync("greeting", ct: TestContext.Current.CancellationToken);

        // A failed gRPC attempt costs the connection timeout, so paying it on every read would make
        // Auto slower than pinning HTTP. The second read goes straight to the endpoint; gRPC is
        // tried again only after the retry window has passed.
        attempts.ShouldBe(1);
        http.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task GetPromptAsync_InGrpcMode_DoesNotFallBackToHttp()
    {
        var http = new AIHttpTestServer((_, _) =>
            AIHttpTestServer.Prompt("greeting", "1.0.0", "Hello"));

        await using NacosAIClient client = CreateClient(
            NacosAITransportMode.Grpc,
            http,
            _ => throw new NacosConnectionException("The AI connection is down."));

        await Should.ThrowAsync<NacosConnectionException>(
            () => client.Prompt.GetPromptAsync("greeting", ct: TestContext.Current.CancellationToken));

        // Grpc is the "fail rather than silently change transports" value, which a caller picks
        // when an HTTP fallback would be a different failure mode than the one it prepared for.
        http.RequestCount.ShouldBe(0);
    }

    [Fact]
    public void Validate_RejectsAnUndefinedTransportMode()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AI = new NacosAIOptions { TransportMode = (NacosAITransportMode)42 },
        };

        ValidateOptionsResult result = new NacosOptionsValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("TransportMode");
    }

    [Fact]
    public void Validate_RejectsAnUndefinedPromptTransportMode()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AI = new NacosAIOptions { PromptTransportMode = (NacosAITransportMode)(-1) },
        };

        ValidateOptionsResult result = new NacosOptionsValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("PromptTransportMode");
    }

    [Fact]
    public void Validate_AcceptsAnUnsetPromptTransportMode()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AI = new NacosAIOptions { PromptTransportMode = null },
        };

        new NacosOptionsValidator().Validate(null, options).Succeeded.ShouldBeTrue();
    }

    /// <summary>
    /// Builds a client whose gRPC channel is scripted by the test.
    /// </summary>
    /// <param name="mode">The transport the AI options select.</param>
    /// <param name="http">The handler the HTTP endpoint is served from.</param>
    /// <param name="channel">Produces the channel, or throws to simulate an unreachable server.</param>
    /// <returns>The client.</returns>
    private static NacosAIClient CreateClient(
        NacosAITransportMode mode,
        HttpMessageHandler http,
        Func<NacosServerAddress, INacosChannel> channel) => new(
            new NacosOptions
            {
                ServerAddresses = ["127.0.0.1:8848"],
                AppName = "ai-tests",
                ConnectionTimeout = TimeSpan.FromSeconds(2),
                AI = new NacosAIOptions { TransportMode = mode },
            },
            [AITestServer.Server],
            new FakeNacosChannelFactory(channel),
            new HttpClient(http),
            TimeProvider.System,
            NullLoggerFactory.Instance);
}
