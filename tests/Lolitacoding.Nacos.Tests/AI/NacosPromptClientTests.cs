using Lolitacoding.Nacos.AI;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Messages;

namespace Lolitacoding.Nacos.Tests.AI;

/// <summary>
/// Verifies the prompt registry client's wire contract and its guard rails against a scripted AI
/// service.
/// </summary>
/// <remarks>
/// The contract worth pinning down is the part the server reads literally: the module label, the
/// selector a read carries, and the version resolution the server owns. The client deliberately
/// makes no resolution of its own — a version and a label are both sent and the server decides —
/// so the tests assert what was sent rather than what a client-side rule would have chosen.
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
}
