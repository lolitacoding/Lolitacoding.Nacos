using System.Net;
using System.Text.Json;
using Lolitacoding.Nacos.AI;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Tests.Transport;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lolitacoding.Nacos.Tests.AI;

/// <summary>
/// Verifies the RAD agent registry's HTTP transport against a scripted client OpenAPI.
/// </summary>
/// <remarks>
/// <para>
/// The scripted handler agrees with whatever the client builds, so what these tests pin down is the
/// contract the server reads: the path, the query names, the form fields whose values are JSON
/// documents of their own, and the result envelope the answer arrives in.
/// </para>
/// <para>
/// The transport choice is the second contract. A publish may not be retried over the other
/// transport, because the attempt that failed may already have created the version; a search and a
/// discovery are reads and may. <see cref="NacosAITransportMode.Http"/> never opens the gRPC
/// connection, which is what an HTTP-only ingress needs.
/// </para>
/// </remarks>
public sealed class NacosAgentRegistryHttpTests
{
    [Fact]
    public async Task PublishAgentAsync_OverHttp_PostsTheFormTheServerBinds()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.AgentVersion(
            "{\"agentName\":\"planner\",\"version\":\"1.0.0\",\"status\":\"reviewing\"}"));

        await using NacosAIClient client = CreateClient(
            new NacosAIOptions { TransportMode = NacosAITransportMode.Http },
            http,
            _ => throw new InvalidOperationException("Http must not open the AI gRPC connection."));

        var request = new NacosAgentPublishRequest
        {
            AgentName = "planner",
            DisplayName = "Planner",
            Description = "Plans things.",
            IconUrl = "https://example.test/planner.png",
            Provider = new NacosAgentRegistryProvider { Name = "acme", Url = "https://acme.test" },
            Tags = ["travel", "planning"],
            Extensions = new Dictionary<string, JsonElement>
            {
                ["vendor"] = JsonElement.Parse("\"acme\""),
            },
            Version = "1.0.0",
            CallInterfaces =
            [
                new NacosAgentCallInterface { Protocol = "a2a", ProtocolVersion = "1.0" },
            ],
            Author = "ci",
            ChangeDescription = "First version.",
            BasedOnVersion = "0.9.0",
            AutoSubmit = true,
        };

        NacosAgentVersionDetail detail = await client.Agent.PublishAgentAsync(
            request, TestContext.Current.CancellationToken);

        detail.AgentName.ShouldBe("planner");
        detail.Version.ShouldBe("1.0.0");
        detail.Status.ShouldBe("reviewing");

        http.Requests[0].AbsolutePath.ShouldEndWith("/v3/client/ai/agents");
        http.Form(0, "agentName").ShouldBe("planner");
        http.Form(0, "displayName").ShouldBe("Planner");
        http.Form(0, "description").ShouldBe("Plans things.");
        http.Form(0, "iconUrl").ShouldBe("https://example.test/planner.png");
        http.Form(0, "version").ShouldBe("1.0.0");
        http.Form(0, "author").ShouldBe("ci");
        http.Form(0, "changeDescription").ShouldBe("First version.");
        http.Form(0, "basedOnVersion").ShouldBe("0.9.0");

        // The flag travels as the literal the server parses, not as a JSON boolean in a string.
        http.Form(0, "autoSubmit").ShouldBe("true");

        // The complex members are JSON documents in fields of their own, so reading them back is what
        // proves the same contracts the gRPC request uses are what the server binds.
        using JsonDocument provider = JsonDocument.Parse(http.Form(0, "provider")!);
        provider.RootElement.GetProperty("name").GetString().ShouldBe("acme");
        provider.RootElement.GetProperty("url").GetString().ShouldBe("https://acme.test");

        using JsonDocument tags = JsonDocument.Parse(http.Form(0, "tags")!);
        tags.RootElement.EnumerateArray().Select(tag => tag.GetString())
            .ShouldBe(["travel", "planning"]);

        using JsonDocument extensions = JsonDocument.Parse(http.Form(0, "extensions")!);
        extensions.RootElement.GetProperty("vendor").GetString().ShouldBe("acme");

        using JsonDocument interfaces = JsonDocument.Parse(http.Form(0, "callInterfaces")!);
        interfaces.RootElement[0].GetProperty("protocol").GetString().ShouldBe("a2a");
        interfaces.RootElement[0].GetProperty("protocolVersion").GetString().ShouldBe("1.0");
    }

    [Fact]
    public async Task PublishAgentAsync_OverHttp_OmitsTheOptionalFieldsItWasNotGiven()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.AgentVersion(
            "{\"agentName\":\"planner\",\"version\":\"1.0.0\",\"status\":\"draft\"}"));

        await using NacosAIClient client = CreateClient(
            new NacosAIOptions { TransportMode = NacosAITransportMode.Http },
            http,
            _ => throw new InvalidOperationException("Http must not open the AI gRPC connection."));

        await client.Agent.PublishAgentAsync(
            new NacosAgentPublishRequest { AgentName = "planner", Version = "1.0.0" },
            TestContext.Current.CancellationToken);

        // A null member is not sent at all rather than as the string "null", which the server would
        // try to parse as JSON.
        http.Form(0, "displayName").ShouldBeNull();
        http.Form(0, "provider").ShouldBeNull();
        http.Form(0, "tags").ShouldBeNull();
        http.Form(0, "callInterfaces").ShouldBeNull();
        http.Form(0, "autoSubmit").ShouldBe("false");
    }

    [Fact]
    public async Task PublishAgentAsync_OverHttp_MapsAConflictToTheServersCode()
    {
        // A stock 3.3 server answers a RAD publish with a conflict while historical A2A authority
        // remains; the code is what a caller reacts to, so it has to survive the envelope.
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.Result(
            HttpStatusCode.Conflict,
            40901,
            "agent migration in progress",
            "RAD is unavailable while historical A2A remains authoritative."));

        await using NacosAIClient client = CreateClient(
            new NacosAIOptions { TransportMode = NacosAITransportMode.Http },
            http,
            _ => throw new InvalidOperationException("Http must not open the AI gRPC connection."));

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Agent.PublishAgentAsync(
                new NacosAgentPublishRequest { AgentName = "planner", Version = "1.0.0" },
                TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(409);
        exception.Message.ShouldContain("historical A2A");
    }

    [Fact]
    public async Task SearchAgentsAsync_OverHttp_SendsTheQueryAndReturnsThePage()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.AgentPage(
            "{\"totalCount\":2,\"pageNumber\":2,\"pagesAvailable\":3,\"pageItems\":["
            + "{\"agentName\":\"planner\",\"status\":\"enable\"},"
            + "{\"agentName\":\"researcher\",\"status\":\"enable\"}]}"));

        await using NacosAIClient client = CreateClient(
            new NacosAIOptions { TransportMode = NacosAITransportMode.Http },
            http,
            _ => throw new InvalidOperationException("Http must not open the AI gRPC connection."));

        var request = new NacosAgentSearchRequest
        {
            AgentNameContains = "plan",
            TagsAll = ["travel", "planning"],
            ProtocolsAny = ["a2a"],
            PageNo = 2,
            PageSize = 10,
        };

        NacosAgentPage<NacosAgentSummary> page = await client.Agent.SearchAgentsAsync(
            request, TestContext.Current.CancellationToken);

        page.TotalCount.ShouldBe(2);
        page.PageNumber.ShouldBe(2);
        page.PagesAvailable.ShouldBe(3);
        page.PageItems!.Select(agent => agent.AgentName).ShouldBe(["planner", "researcher"]);

        http.Requests[0].AbsolutePath.ShouldEndWith("/v3/client/ai/agents/search");
        http.Query(0, "agentNameContains").ShouldBe("plan");
        http.Query(0, "pageNo").ShouldBe("2");
        http.Query(0, "pageSize").ShouldBe("10");

        // A list is one parameter per value, which is how the server binds it; a comma-joined value
        // would be a single tag that matches nothing.
        QueryValues(http.Requests[0], "tagsAll").ShouldBe(["travel", "planning"]);
        QueryValues(http.Requests[0], "protocolsAny").ShouldBe(["a2a"]);
    }

    [Fact]
    public async Task SearchAgentsAsync_OverHttp_OmitsTheSelectorsItWasNotGiven()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.AgentPage(
            "{\"totalCount\":0,\"pageNumber\":1,\"pagesAvailable\":0,\"pageItems\":[]}"));

        await using NacosAIClient client = CreateClient(
            new NacosAIOptions { TransportMode = NacosAITransportMode.Http },
            http,
            _ => throw new InvalidOperationException("Http must not open the AI gRPC connection."));

        await client.Agent.SearchAgentsAsync(
            new NacosAgentSearchRequest(), TestContext.Current.CancellationToken);

        http.Query(0, "agentNameContains").ShouldBeNull();
        http.Query(0, "pageNo").ShouldBeNull();
        QueryValues(http.Requests[0], "tagsAll").ShouldBeEmpty();
    }

    [Fact]
    public async Task DiscoverAgentAsync_OverHttp_SendsTheReferenceAndTheFilter()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.AgentDiscovery(
            "{\"namespaceId\":\"staging\",\"agentName\":\"planner\",\"version\":\"1.0.0\","
            + "\"contentDigest\":\"abc\",\"callInterfaces\":[{\"protocol\":\"a2a\"}]}"));

        await using NacosAIClient client = CreateClient(
            new NacosAIOptions { TransportMode = NacosAITransportMode.Http },
            http,
            _ => throw new InvalidOperationException("Http must not open the AI gRPC connection."));

        var reference = new NacosAgentReference
        {
            AgentName = "planner",
            Version = "1.0.0",
            Label = "stable",
        };

        var filter = new NacosAgentDiscoveryFilter
        {
            Protocols = ["a2a"],
            ProtocolVersion = "1.0",
            Transports = ["JSONRPC"],
            EndpointSources = ["RUNTIME"],
            MetadataSelector = new Dictionary<string, string> { ["region"] = "eu" },
        };

        NacosAgentDiscoveryResult result = await client.Agent.DiscoverAgentAsync(
            reference, filter, TestContext.Current.CancellationToken);

        result.AgentName.ShouldBe("planner");
        result.Version.ShouldBe("1.0.0");
        result.ContentDigest.ShouldBe("abc");
        result.CallInterfaces![0].Protocol.ShouldBe("a2a");

        http.Requests[0].AbsolutePath.ShouldEndWith("/v3/client/ai/agents");
        http.Query(0, "agentName").ShouldBe("planner");
        http.Query(0, "version").ShouldBe("1.0.0");
        http.Query(0, "label").ShouldBe("stable");
        http.Query(0, "protocolVersion").ShouldBe("1.0");
        QueryValues(http.Requests[0], "protocol").ShouldBe(["a2a"]);
        QueryValues(http.Requests[0], "transport").ShouldBe(["JSONRPC"]);
        QueryValues(http.Requests[0], "endpointSource").ShouldBe(["RUNTIME"]);

        // The selector is a JSON document in one parameter, not one parameter per key.
        using JsonDocument selector = JsonDocument.Parse(http.Query(0, "metadataSelector")!);
        selector.RootElement.GetProperty("region").GetString().ShouldBe("eu");
    }

    [Fact]
    public async Task DiscoverAgentAsync_OverHttp_MapsAnUnknownAgentToTheNotFoundCode()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.NotFound("Agent not found: planner"));

        await using NacosAIClient client = CreateClient(
            new NacosAIOptions { TransportMode = NacosAITransportMode.Http },
            http,
            _ => throw new InvalidOperationException("Http must not open the AI gRPC connection."));

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Agent.DiscoverAgentAsync(
                new NacosAgentReference { AgentName = "planner" },
                ct: TestContext.Current.CancellationToken));

        // A watch decides what a missing agent means by the code, not by the transport, so the real
        // endpoint's 404 has to arrive as the code the gRPC result carries.
        exception.ErrorCode.ShouldBe(404);
        exception.Message.ShouldContain("planner");
    }

    [Fact]
    public async Task SearchAgentsAsync_OverHttp_MapsARejectedReadToAnAuthException()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.Result(
            HttpStatusCode.Forbidden,
            403,
            "access denied",
            "The token does not allow this read."));

        await using NacosAIClient client = CreateClient(
            new NacosAIOptions { TransportMode = NacosAITransportMode.Http },
            http,
            _ => throw new InvalidOperationException("Http must not open the AI gRPC connection."));

        NacosAuthException exception = await Should.ThrowAsync<NacosAuthException>(
            () => client.Agent.SearchAgentsAsync(
                new NacosAgentSearchRequest(), TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("token");
    }

    [Fact]
    public async Task DiscoverAgentAsync_OverHttp_ReportsASuccessWithNoResultAsAContractFailure()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.AgentDiscovery("null"));

        await using NacosAIClient client = CreateClient(
            new NacosAIOptions { TransportMode = NacosAITransportMode.Http },
            http,
            _ => throw new InvalidOperationException("Http must not open the AI gRPC connection."));

        // A missing agent is a failure status, so reaching a caller with a success and no result
        // would be a contract break rather than a missing resource.
        await Should.ThrowAsync<NacosServerException>(
            () => client.Agent.DiscoverAgentAsync(
                new NacosAgentReference { AgentName = "planner" },
                ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SearchAgentsAsync_InHttpMode_DoesNotOpenTheGrpcConnection()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.AgentPage(
            "{\"totalCount\":0,\"pageNumber\":1,\"pagesAvailable\":0,\"pageItems\":[]}"));

        int connections = 0;
        await using NacosAIClient client = CreateClient(
            new NacosAIOptions { TransportMode = NacosAITransportMode.Http },
            http,
            _ =>
            {
                connections++;
                throw new NacosConnectionException("The AI connection is down.");
            });

        await client.Agent.SearchAgentsAsync(
            new NacosAgentSearchRequest(), TestContext.Current.CancellationToken);

        // Http is the property that makes the registry usable behind an ingress that only exposes the
        // client OpenAPI: the AI connection is never opened at all.
        connections.ShouldBe(0);
        http.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task SearchAgentsAsync_InAutoMode_FallsBackToHttpWhenGrpcCannotConnect()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.AgentPage(
            "{\"totalCount\":1,\"pageNumber\":1,\"pagesAvailable\":1,"
            + "\"pageItems\":[{\"agentName\":\"planner\",\"status\":\"enable\"}]}"));

        int attempts = 0;
        await using NacosAIClient client = CreateClient(
            new NacosAIOptions { TransportMode = NacosAITransportMode.Auto },
            http,
            _ =>
            {
                attempts++;
                throw new NacosConnectionException("The AI connection is down.");
            });

        NacosAgentPage<NacosAgentSummary> page = await client.Agent.SearchAgentsAsync(
            new NacosAgentSearchRequest(), TestContext.Current.CancellationToken);

        page.TotalCount.ShouldBe(1);

        // Auto is gRPC first: the fallback only happens after the attempt failed, so the HTTP request
        // is the second thing the search did rather than the only thing.
        attempts.ShouldBe(1);
        http.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task DiscoverAgentAsync_InGrpcMode_DoesNotFallBackToHttp()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.AgentDiscovery(
            "{\"agentName\":\"planner\"}"));

        await using NacosAIClient client = CreateClient(
            new NacosAIOptions { TransportMode = NacosAITransportMode.Grpc },
            http,
            _ => throw new NacosConnectionException("The AI connection is down."));

        await Should.ThrowAsync<NacosConnectionException>(
            () => client.Agent.DiscoverAgentAsync(
                new NacosAgentReference { AgentName = "planner" },
                ct: TestContext.Current.CancellationToken));

        // Grpc is the "fail rather than silently change transports" value, which a caller picks when
        // an HTTP fallback would be a different failure mode than the one it prepared for.
        http.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task PublishAgentAsync_InAutoMode_DoesNotRetryAFailedGrpcPublishOverHttp()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.AgentVersion(
            "{\"agentName\":\"planner\",\"version\":\"1.0.0\",\"status\":\"draft\"}"));

        int attempts = 0;
        await using NacosAIClient client = CreateClient(
            new NacosAIOptions { TransportMode = NacosAITransportMode.Auto },
            http,
            _ =>
            {
                attempts++;
                throw new NacosConnectionException("The AI connection is down.");
            });

        await Should.ThrowAsync<NacosConnectionException>(
            () => client.Agent.PublishAgentAsync(
                new NacosAgentPublishRequest { AgentName = "planner", Version = "1.0.0" },
                TestContext.Current.CancellationToken));

        // A publish is not a read: the attempt may have reached the server, so re-sending it over the
        // other transport could conflict with itself or publish twice.
        attempts.ShouldBe(1);
        http.RequestCount.ShouldBe(0);

        // The failure still starts the window, so the next operation is sent over HTTP directly
        // rather than paying the same failed connect again.
        NacosAgentVersionDetail detail = await client.Agent.PublishAgentAsync(
            new NacosAgentPublishRequest { AgentName = "planner", Version = "1.0.0" },
            TestContext.Current.CancellationToken);

        detail.Version.ShouldBe("1.0.0");
        attempts.ShouldBe(1);
        http.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task AgentTransportMode_OverridesTheDefaultTransport()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.AgentPage(
            "{\"totalCount\":0,\"pageNumber\":1,\"pagesAvailable\":0,\"pageItems\":[]}"));

        int connections = 0;
        await using NacosAIClient client = CreateClient(
            new NacosAIOptions
            {
                // The default stays on gRPC; only this registry moves to HTTP.
                TransportMode = NacosAITransportMode.Grpc,
                AgentTransportMode = NacosAITransportMode.Http,
            },
            http,
            _ =>
            {
                connections++;
                throw new NacosConnectionException("The AI connection is down.");
            });

        await client.Agent.SearchAgentsAsync(
            new NacosAgentSearchRequest(), TestContext.Current.CancellationToken);

        connections.ShouldBe(0);
        http.RequestCount.ShouldBe(1);
    }

    [Fact]
    public void Validate_RejectsAnUndefinedAgentTransportMode()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AI = new NacosAIOptions { AgentTransportMode = (NacosAITransportMode)42 },
        };

        ValidateOptionsResult result = new NacosOptionsValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("AgentTransportMode");
    }

    /// <summary>
    /// Reads every value a request carries for one query parameter.
    /// </summary>
    /// <param name="uri">The request URI.</param>
    /// <param name="name">The parameter name.</param>
    /// <returns>The decoded values, in order.</returns>
    private static List<string> QueryValues(Uri uri, string name)
    {
        var values = new List<string>();

        if (uri.Query.Length <= 1)
        {
            return values;
        }

        foreach (string pair in uri.Query[1..].Split('&'))
        {
            int separator = pair.IndexOf('=', StringComparison.Ordinal);

            if (separator < 0)
            {
                continue;
            }

            if (string.Equals(
                Uri.UnescapeDataString(pair[..separator]),
                name,
                StringComparison.Ordinal))
            {
                values.Add(Uri.UnescapeDataString(pair[(separator + 1)..]));
            }
        }

        return values;
    }

    /// <summary>
    /// Builds a client whose AI connection is the given channel.
    /// </summary>
    /// <param name="ai">The AI options.</param>
    /// <param name="http">The scripted client OpenAPI.</param>
    /// <param name="channel">Produces the AI channel, or throws to simulate an unreachable server.</param>
    /// <returns>The client.</returns>
    private static NacosAIClient CreateClient(
        NacosAIOptions ai,
        HttpMessageHandler http,
        Func<NacosServerAddress, INacosChannel> channel) => new(
            new NacosOptions
            {
                ServerAddresses = ["127.0.0.1:8848"],
                AppName = "ai-tests",
                Namespace = "staging",
                ConnectionTimeout = TimeSpan.FromSeconds(2),
                AI = ai,
            },
            [AITestServer.Server],
            new FakeNacosChannelFactory(channel),
            new HttpClient(http),
            TimeProvider.System,
            NullLoggerFactory.Instance);
}
