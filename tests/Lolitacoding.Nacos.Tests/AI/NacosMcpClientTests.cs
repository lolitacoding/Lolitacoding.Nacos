using System.Net;
using System.Text.Json;
using Lolitacoding.Nacos.AI;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Generated;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Tests.Transport;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lolitacoding.Nacos.Tests.AI;

/// <summary>
/// Verifies the MCP registry client's wire contract and its guard rails against a scripted AI
/// service.
/// </summary>
/// <remarks>
/// <para>
/// The contract worth pinning down is the part the server reads literally: the module label, the
/// name and version a query carries, the specifications a release carries, and the ability flags
/// that decide whether an operation may be attempted at all. The distinction the tests are most
/// careful about is the one between a server that said it cannot serve the registry and one that
/// said nothing, because only the first is a reason to refuse.
/// </para>
/// <para>
/// The transport choice is the second contract: the same read and release have two wire shapes, and
/// the tests pin both — the query string a read carries, the form a release posts, the status codes
/// the endpoint answers with, and the promise that <see cref="NacosAITransportMode.Http"/> never
/// opens the AI gRPC connection. The release is also the one operation that must not be retried on
/// the other transport, because the attempt that failed may already have created the version.
/// </para>
/// </remarks>
public sealed class NacosMcpClientTests
{
    [Fact]
    public async Task GetServerAsync_OpensTheAIConnectionOnlyWhenFirstUsed()
    {
        // An application that never asks the registry for anything should not carry a connection and
        // a heartbeat for the module it does not use.
        var server = new AITestServer();

        await using NacosAIClient client = server.CreateClient();

        server.Factory.RequestedAddresses.ShouldBeEmpty();
        server.Stream.Written.ShouldBeEmpty();

        server.OnQuery = _ => AITest.Detail(new NacosMcpServerDetail { Name = "weather" });

        await client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken);

        server.Factory.RequestedAddresses.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetServerAsync_LabelsTheConnectionForTheAIModuleWithItsPublisherIdentity()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        server.OnQuery = _ => AITest.Detail(new NacosMcpServerDetail { Name = "weather" });

        await client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken);

        ConnectionSetupRequest setup =
            PayloadCodec.Decode<ConnectionSetupRequest>(server.Stream.Written[0])!;

        setup.Labels![ProtocolHeaders.Module].ShouldBe("ai");

        // The server records the publisher identity with every endpoint the connection registers, so
        // the label has to be there before the handshake completes and has to survive a reconnect.
        setup.Labels[NacosAIRemote.ClientUuidLabel].ShouldNotBeNullOrEmpty();
        Guid.TryParse(setup.Labels[NacosAIRemote.ClientUuidLabel], out _).ShouldBeTrue();
    }

    [Fact]
    public async Task GetServerAsync_AsksForTheNamedVersionInTheConfiguredNamespace()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient(new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "ai-tests",
            Namespace = "staging",
        });

        server.OnQuery = _ => AITest.Detail(new NacosMcpServerDetail { Name = "weather" });

        await client.Mcp.GetServerAsync("weather", "1.2.3", TestContext.Current.CancellationToken);

        QueryMcpServerRequest request = server.Queries[0];
        request.McpName.ShouldBe("weather");
        request.Version.ShouldBe("1.2.3");
        request.NamespaceId.ShouldBe("staging");

        // The query travels on the unary channel method, not on the duplex stream the setup and the
        // server's pushes use.
        PayloadCodec.ReadTypeName(server.Channel.UnaryRequests[^1]).ShouldBe("QueryMcpServerRequest");
    }

    [Fact]
    public async Task GetServerAsync_LeavesTheVersionUnsetForTheLatestOne()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        server.OnQuery = _ => AITest.Detail(new NacosMcpServerDetail { Name = "weather" });

        await client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken);

        // A client that asked for "latest" by name would need to know which label the server uses;
        // leaving the field unset lets the server answer with whatever it currently serves.
        server.Queries[0].Version.ShouldBeNull();
    }

    [Fact]
    public async Task GetServerAsync_ReadsTheDetailTheServerReturns()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        server.OnQuery = _ => AITest.Detail(new NacosMcpServerDetail
        {
            Name = "weather",
            Protocol = NacosMcpProtocol.Streamable,
            Version = "1.2.3",
            Enabled = true,
            Status = NacosMcpStatus.Active,
            Capabilities = [NacosMcpCapability.Tool, NacosMcpCapability.Resource],
            ToolSpec = new NacosMcpToolSpecification
            {
                Tools =
                [
                    new NacosMcpTool { Name = "forecast", Description = "Tomorrow's weather" },
                ],
            },
            BackendEndpoints =
            [
                new NacosMcpEndpointInfo { Protocol = "http", Address = "10.0.0.7", Port = 8080 },
            ],
        });

        NacosMcpServerDetail detail =
            await client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken);

        detail.Name.ShouldBe("weather");
        detail.Version.ShouldBe("1.2.3");
        detail.Protocol.ShouldBe("mcp-streamable");
        detail.Capabilities.ShouldBe([NacosMcpCapability.Tool, NacosMcpCapability.Resource]);
        detail.ToolSpec!.Tools!.Count.ShouldBe(1);
        detail.ToolSpec.Tools[0].Name.ShouldBe("forecast");
        detail.BackendEndpoints!.Single().ShouldSatisfyAllConditions(
            endpoint => endpoint.Address.ShouldBe("10.0.0.7"),
            endpoint => endpoint.Port.ShouldBe(8080));
    }

    [Fact]
    public async Task GetServerAsync_RejectsABlankNameBeforeSendingAnything()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        await Should.ThrowAsync<ArgumentException>(
            () => client.Mcp.GetServerAsync("  ", ct: TestContext.Current.CancellationToken));

        server.Factory.RequestedAddresses.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetServerAsync_FailsWhenTheServerSaysItHasNoMcpRegistry()
    {
        var server = new AITestServer(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [NacosAIServerAbility.Mcp] = false,
        });

        await using NacosAIClient client = server.CreateClient();

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken));

        // 501 is the code Nacos uses for a feature a server does not implement.
        exception.ErrorCode.ShouldBe(501);

        // Refusing locally matters because an operation the server cannot serve would otherwise
        // surface as an unknown-request-type failure from the transport.
        server.Queries.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetServerAsync_ProceedsWhenTheAbilityTableSaysNothingAboutMcp()
    {
        // A key the server did not mention means it did not state the ability, which is not the same
        // as stating it is unsupported: an older server that predates the key still serves MCP.
        var server = new AITestServer(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [NacosAIServerAbility.Lock] = true,
        });

        await using NacosAIClient client = server.CreateClient();

        server.OnQuery = _ => AITest.Detail(new NacosMcpServerDetail { Name = "weather" });

        NacosMcpServerDetail detail =
            await client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken);

        detail.Name.ShouldBe("weather");
    }

    [Fact]
    public async Task GetServerAsync_SurfacesTheErrorCodeTheServerReported()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        server.OnQuery = _ => AITest.Failure(20004, "MCP server not found.");

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(20004);
        exception.Message.ShouldContain("not found");
    }

    [Fact]
    public async Task GetServerAsync_ReportsASuccessWithNoDetailAsAProtocolViolation()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        // A missing server is reported as a failure code, so reaching a caller with a success and no
        // detail would be a contract break rather than a missing resource.
        server.OnQuery = _ => new QueryMcpServerResponse();

        await Should.ThrowAsync<NacosServerException>(
            () => client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReleaseServerAsync_SendsTheSpecificationsTheServerReads()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient(new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "ai-tests",
            Namespace = "staging",
        });

        server.OnRelease = _ => new ReleaseMcpServerResponse { McpId = "mcp-id-1" };

        var serverSpecification = new NacosMcpServer
        {
            Name = "weather",
            Protocol = NacosMcpProtocol.Streamable,
            VersionDetail = new NacosMcpVersionDetail { Version = "1.2.3" },
        };

        var tools = new NacosMcpToolSpecification
        {
            Tools = [new NacosMcpTool { Name = "forecast" }],
        };

        var resources = new NacosMcpResourceSpecification
        {
            Resources = [],
        };

        var endpoint = new NacosMcpEndpointSpec
        {
            Type = NacosMcpEndpointType.Direct,
            Data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [NacosMcpEndpointData.Address] = "10.0.0.7",
                [NacosMcpEndpointData.Port] = "8080",
            },
        };

        await client.Mcp.ReleaseServerAsync(
            serverSpecification,
            tools,
            resources,
            endpoint,
            ct: TestContext.Current.CancellationToken);

        ReleaseMcpServerRequest request = server.Releases[0];
        request.McpName.ShouldBe("weather");
        request.NamespaceId.ShouldBe("staging");
        request.CreateDraft.ShouldBeFalse();

        // The request is read back off the wire, so the specifications are compared by what the
        // server would read rather than by object identity.
        request.ServerSpecification!.Name.ShouldBe("weather");
        request.ServerSpecification.Protocol.ShouldBe(NacosMcpProtocol.Streamable);
        request.ServerSpecification.VersionDetail!.Version.ShouldBe("1.2.3");
        request.ToolSpecification!.Tools![0].Name.ShouldBe("forecast");
        request.ResourceSpecification!.Resources.ShouldBeEmpty();
        request.EndpointSpecification!.Type.ShouldBe(NacosMcpEndpointType.Direct);
        request.EndpointSpecification.Data![NacosMcpEndpointData.Address].ShouldBe("10.0.0.7");
        request.EndpointSpecification.Data[NacosMcpEndpointData.Port].ShouldBe("8080");
    }

    [Fact]
    public async Task ReleaseServerAsync_DerivesTheNameFromTheSpecification()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        server.OnRelease = _ => new ReleaseMcpServerResponse { McpId = "mcp-id-1" };

        await client.Mcp.ReleaseServerAsync(
            new NacosMcpServer
            {
                Name = "weather",
                Version = "1.2.3",
            },
            ct: TestContext.Current.CancellationToken);

        // The name is the request's routing key, and the server rejects a release whose name differs
        // from the specification's, so taking it from the specification removes the chance of a
        // mismatch.
        server.Releases[0].McpName.ShouldBe("weather");
        server.Releases[0].ServerSpecification!.Version.ShouldBe("1.2.3");
    }

    [Fact]
    public async Task ReleaseServerAsync_ReturnsTheIdentifierTheServerAssigned()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        server.OnRelease = _ => new ReleaseMcpServerResponse { McpId = "mcp-id-1" };

        string? id = await client.Mcp.ReleaseServerAsync(
            new NacosMcpServer { Name = "weather", Version = "1.2.3" },
            ct: TestContext.Current.CancellationToken);

        id.ShouldBe("mcp-id-1");
    }

    [Fact]
    public async Task ReleaseServerAsync_RejectsASpecificationWithoutAName()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        await Should.ThrowAsync<ArgumentException>(
            () => client.Mcp.ReleaseServerAsync(
                new NacosMcpServer { Version = "1.2.3" },
                ct: TestContext.Current.CancellationToken));

        server.Factory.RequestedAddresses.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReleaseServerAsync_RejectsASpecificationWithoutAVersion()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        // The version is what the release creates, so a specification without one would have to
        // leave the version for the server to invent — and a later release could not name it again.
        await Should.ThrowAsync<ArgumentException>(
            () => client.Mcp.ReleaseServerAsync(
                new NacosMcpServer { Name = "weather" },
                ct: TestContext.Current.CancellationToken));

        server.Factory.RequestedAddresses.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReleaseServerAsync_RejectsANullSpecification()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        await Should.ThrowAsync<ArgumentNullException>(
            () => client.Mcp.ReleaseServerAsync(null!, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReleaseServerAsync_RejectsADraftWhenTheServerDoesNotStoreDrafts()
    {
        var server = new AITestServer(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [NacosAIServerAbility.Mcp] = true,
            [NacosAIServerAbility.McpDraftRelease] = false,
        });

        await using NacosAIClient client = server.CreateClient();

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Mcp.ReleaseServerAsync(
                new NacosMcpServer { Name = "weather", Version = "1.2.3" },
                createDraft: true,
                ct: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(501);
        server.Releases.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReleaseServerAsync_PublishesDirectlyWhenTheDraftAbilityIsAbsentAndNoDraftWasAsked()
    {
        var server = new AITestServer(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [NacosAIServerAbility.Mcp] = true,
        });

        await using NacosAIClient client = server.CreateClient();

        server.OnRelease = _ => new ReleaseMcpServerResponse { McpId = "mcp-id-1" };

        await client.Mcp.ReleaseServerAsync(
            new NacosMcpServer { Name = "weather", Version = "1.2.3" },
            ct: TestContext.Current.CancellationToken);

        // An unstated draft ability is not a rejection, and the flag the server would ignore is not
        // sent anyway.
        server.Releases[0].CreateDraft.ShouldBeFalse();
    }

    [Fact]
    public async Task GetServerAsync_FailsAfterDispose()
    {
        var server = new AITestServer();
        NacosAIClient client = server.CreateClient();

        await client.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(
            () => client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetServerAsync_OverHttp_ReadsTheClientEndpointWithTheSelectors()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.McpServer(
            "{\"name\":\"weather\",\"protocol\":\"mcp-streamable\",\"versionDetail\":{\"version\":\"1.2.3\",\"release_date\":\"2026-01-01\",\"is_latest\":true},\"toolSpec\":{\"tools\":[{\"name\":\"forecast\"}]}}"));

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "ai-tests",
            Namespace = "staging",
            AI = new NacosAIOptions { TransportMode = NacosAITransportMode.Http },
        });

        NacosMcpServerDetail detail = await client.Mcp.GetServerAsync(
            "weather", "1.2.3", TestContext.Current.CancellationToken);

        detail.Name.ShouldBe("weather");
        detail.Protocol.ShouldBe(NacosMcpProtocol.Streamable);
        detail.VersionDetail!.Version.ShouldBe("1.2.3");

        // The registry-compatible version object keeps its snake-case names inside the camel-case
        // envelope, so reading them back is what proves the two policies still meet.
        detail.VersionDetail.ReleaseDate.ShouldBe("2026-01-01");
        detail.VersionDetail.IsLatest.ShouldBe(true);
        detail.ToolSpec!.Tools![0].Name.ShouldBe("forecast");

        http.Requests[0].AbsolutePath.ShouldEndWith("/v3/client/ai/mcp");
        http.Query(0, "namespaceId").ShouldBe("staging");
        http.Query(0, "mcpName").ShouldBe("weather");
        http.Query(0, "version").ShouldBe("1.2.3");

        // HTTP means HTTP: the AI connection is never opened, which is what makes the registry usable
        // behind an ingress that only exposes the client OpenAPI.
        server.Factory.RequestedAddresses.ShouldBeEmpty();
        server.Queries.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetServerAsync_OverHttp_LeavesTheVersionUnsetForTheLatestOne()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.McpServer("{\"name\":\"weather\"}"));

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "ai-tests",
            AI = new NacosAIOptions { TransportMode = NacosAITransportMode.Http },
        });

        await client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken);

        // Both transports leave the field unset rather than naming "latest": which version is current
        // is the server's answer, not the client's.
        http.Query(0, "version").ShouldBeNull();
    }

    [Fact]
    public async Task GetServerAsync_OverHttp_MapsAMissingServerToTheNotFoundCode()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.NotFound("MCP server not found"));

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "ai-tests",
            AI = new NacosAIOptions { TransportMode = NacosAITransportMode.Http },
        });

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken));

        // The subscription cache decides what a missing version means by the code, not by the
        // transport, so HTTP has to report the code the gRPC transport reports.
        exception.ErrorCode.ShouldBe(404);
    }

    [Fact]
    public async Task GetServerAsync_OverHttp_ReportsASuccessWithNoDetailAsAProtocolViolation()
    {
        var http = new AIHttpTestServer((_, _) =>
            AIHttpTestServer.Result(HttpStatusCode.OK, 0, "success"));

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "ai-tests",
            AI = new NacosAIOptions { TransportMode = NacosAITransportMode.Http },
        });

        // A missing server is reported as a failure code, so a success with no detail is a contract
        // break rather than a missing resource.
        await Should.ThrowAsync<NacosServerException>(
            () => client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetServerAsync_OverHttp_DoesNotGateOnTheMcpAbility()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.McpServer("{\"name\":\"weather\"}"));

        AITestServer server = new(
            new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [NacosAIServerAbility.Mcp] = false,
            },
            http);

        await using NacosAIClient client = server.CreateClient(new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "ai-tests",
            AI = new NacosAIOptions { TransportMode = NacosAITransportMode.Http },
        });

        NacosMcpServerDetail detail =
            await client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken);

        // The ability table is part of the gRPC handshake. There is no handshake here, and the HTTP
        // endpoint exists only on a server that implements the registry, so there is nothing to gate
        // on and refusing locally would be inventing a limitation.
        detail.Name.ShouldBe("weather");
        server.Factory.RequestedAddresses.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetServerAsync_WithAGrpcOverride_UsesGrpcWhileTheGlobalTransportIsHttp()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.McpServer("{\"name\":\"weather\"}"));

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "ai-tests",
            AI = new NacosAIOptions
            {
                TransportMode = NacosAITransportMode.Http,
                McpTransportMode = NacosAITransportMode.Grpc,
            },
        });

        server.OnQuery = _ => AITest.Detail(new NacosMcpServerDetail { Name = "weather" });

        await client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken);

        // The per-module value wins, which is the point of having one: a deployment can move a single
        // resource without moving the rest.
        server.Queries.Count.ShouldBe(1);
        http.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetServerAsync_InAutoMode_FallsBackToHttpWhenGrpcCannotConnect()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.McpServer("{\"name\":\"weather\"}"));

        int attempts = 0;
        await using NacosAIClient client = CreateClient(
            NacosAITransportMode.Auto,
            http,
            _ =>
            {
                attempts++;
                throw new NacosConnectionException("The AI connection is down.");
            });

        NacosMcpServerDetail detail =
            await client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken);

        detail.Name.ShouldBe("weather");

        // Auto is gRPC first: the fallback only happens after the attempt failed, so the HTTP request
        // is the second thing the read did rather than the only thing.
        attempts.ShouldBe(1);
        http.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetServerAsync_InAutoMode_DoesNotRetryTheFailedGrpcAttemptOnEveryRead()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.McpServer("{\"name\":\"weather\"}"));

        int attempts = 0;
        await using NacosAIClient client = CreateClient(
            NacosAITransportMode.Auto,
            http,
            _ =>
            {
                attempts++;
                throw new NacosConnectionException("The AI connection is down.");
            });

        await client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken);
        await client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken);

        // A failed gRPC attempt costs the connection timeout, so paying it on every read would make
        // Auto slower than pinning HTTP. The second read goes straight to the endpoint; gRPC is tried
        // again only after the retry window has passed.
        attempts.ShouldBe(1);
        http.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task GetServerAsync_InGrpcMode_DoesNotFallBackToHttp()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.McpServer("{\"name\":\"weather\"}"));

        await using NacosAIClient client = CreateClient(
            NacosAITransportMode.Grpc,
            http,
            _ => throw new NacosConnectionException("The AI connection is down."));

        await Should.ThrowAsync<NacosConnectionException>(
            () => client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken));

        // Grpc is the "fail rather than silently change transports" value, which a caller picks when
        // an HTTP fallback would be a different failure mode than the one it prepared for.
        http.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task ReleaseServerAsync_OverHttp_PostsTheFormTheServerBinds()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.McpRelease("mcp-id-1"));

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "ai-tests",
            Namespace = "staging",
            AI = new NacosAIOptions { TransportMode = NacosAITransportMode.Http },
        });

        string? id = await client.Mcp.ReleaseServerAsync(
            new NacosMcpServer
            {
                Name = "weather",
                Protocol = NacosMcpProtocol.Streamable,
                VersionDetail = new NacosMcpVersionDetail
                {
                    Version = "1.2.3",
                    ReleaseDate = "2026-01-01",
                },
            },
            new NacosMcpToolSpecification
            {
                Tools = [new NacosMcpTool { Name = "forecast" }],
            },
            new NacosMcpResourceSpecification
            {
                Resources = [],
            },
            new NacosMcpEndpointSpec
            {
                Type = NacosMcpEndpointType.Direct,
                Data = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [NacosMcpEndpointData.Address] = "10.0.0.7",
                    [NacosMcpEndpointData.Port] = "8080",
                },
            },
            ct: TestContext.Current.CancellationToken);

        id.ShouldBe("mcp-id-1");

        http.Requests[0].AbsolutePath.ShouldEndWith("/v3/client/ai/mcp");
        http.Form(0, "namespaceId").ShouldBe("staging");
        http.Form(0, "mcpName").ShouldBe("weather");
        http.Form(0, "createDraft").ShouldBe("false");

        // The complex members are JSON in a field of their own rather than one JSON body, so each is
        // read back the way the server binds it.
        using JsonDocument specification = JsonDocument.Parse(http.Form(0, "serverSpecification")!);
        specification.RootElement.GetProperty("name").GetString().ShouldBe("weather");
        specification.RootElement.GetProperty("protocol").GetString().ShouldBe(NacosMcpProtocol.Streamable);
        specification.RootElement.GetProperty("versionDetail").GetProperty("version").GetString().ShouldBe("1.2.3");
        specification.RootElement.GetProperty("versionDetail").GetProperty("release_date").GetString()
            .ShouldBe("2026-01-01");

        using JsonDocument tools = JsonDocument.Parse(http.Form(0, "toolSpecification")!);
        tools.RootElement.GetProperty("tools")[0].GetProperty("name").GetString().ShouldBe("forecast");

        using JsonDocument resources = JsonDocument.Parse(http.Form(0, "resourceSpecification")!);
        resources.RootElement.GetProperty("resources").GetArrayLength().ShouldBe(0);

        using JsonDocument endpoint = JsonDocument.Parse(http.Form(0, "endpointSpecification")!);
        endpoint.RootElement.GetProperty("type").GetString().ShouldBe(NacosMcpEndpointType.Direct);

        server.Factory.RequestedAddresses.ShouldBeEmpty();
        server.Releases.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReleaseServerAsync_OverHttp_SendsTheDraftFlagWithoutGatingOnTheAbility()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.McpRelease("mcp-id-1"));

        AITestServer server = new(
            new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [NacosAIServerAbility.Mcp] = true,
                [NacosAIServerAbility.McpDraftRelease] = false,
            },
            http);

        await using NacosAIClient client = server.CreateClient(new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "ai-tests",
            AI = new NacosAIOptions { TransportMode = NacosAITransportMode.Http },
        });

        string? id = await client.Mcp.ReleaseServerAsync(
            new NacosMcpServer { Name = "weather", Version = "1.2.3" },
            createDraft: true,
            ct: TestContext.Current.CancellationToken);

        id.ShouldBe("mcp-id-1");

        // The draft ability is a gRPC handshake field. The HTTP endpoint exists only on a server that
        // implements drafts, and the flag is a boolean the form parser validates itself.
        http.Form(0, "createDraft").ShouldBe("true");
        server.Releases.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReleaseServerAsync_InAutoMode_DoesNotRetryAFailedGrpcReleaseOverHttp()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.McpRelease("mcp-id-1"));

        int attempts = 0;
        await using NacosAIClient client = CreateClient(
            NacosAITransportMode.Auto,
            http,
            _ =>
            {
                attempts++;
                throw new NacosConnectionException("The AI connection is down.");
            });

        await Should.ThrowAsync<NacosConnectionException>(
            () => client.Mcp.ReleaseServerAsync(
                new NacosMcpServer { Name = "weather", Version = "1.2.3" },
                ct: TestContext.Current.CancellationToken));

        // A release is not a read: the attempt may have reached the server, so re-sending it over the
        // other transport could conflict with itself or publish twice.
        attempts.ShouldBe(1);
        http.RequestCount.ShouldBe(0);

        // The failure still starts the window, so the next operation is sent over HTTP directly
        // rather than paying the same failed connect again.
        string? id = await client.Mcp.ReleaseServerAsync(
            new NacosMcpServer { Name = "weather", Version = "1.2.3" },
            ct: TestContext.Current.CancellationToken);

        id.ShouldBe("mcp-id-1");
        attempts.ShouldBe(1);
        http.RequestCount.ShouldBe(1);
    }

    [Fact]
    public void Validate_RejectsAnUndefinedMcpTransportMode()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AI = new NacosAIOptions { McpTransportMode = (NacosAITransportMode)42 },
        };

        ValidateOptionsResult result = new NacosOptionsValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("McpTransportMode");
    }

    [Fact]
    public void Validate_AcceptsAnUnsetMcpTransportMode()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AI = new NacosAIOptions { McpTransportMode = null },
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
