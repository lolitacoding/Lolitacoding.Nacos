using System.Net;
using Lolitacoding.Nacos.AI;

namespace Lolitacoding.Nacos.Tests.AI;

/// <summary>
/// Verifies how a skill is read from the registry, and what is refused.
/// </summary>
/// <remarks>
/// A skill is the one AI resource whose payload is an archive, so the tests cover both halves of the
/// read: the request the client builds and the values it takes from the response headers, and the
/// checks that stop a body which is not a safe archive from reaching a caller that will extract it.
/// </remarks>
public sealed class NacosSkillClientTests
{
    private static readonly byte[] Archive = AIHttpTestServer.CreateArchive(
        ("hello/SKILL.md", "# Hello"),
        ("hello/reference/notes.md", "notes"));

    [Fact]
    public async Task GetSkillAsync_ReturnsTheArchiveWithTheResolvedVersionAndHash()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.Zip(Archive, "hash-1", "1.2.0"));
        AITestServer server = CreateServer(http);
        await using NacosAIClient client = server.CreateClient();

        NacosSkill skill = await client.Skill.GetSkillAsync(
            "hello", label: "stable", ct: TestContext.Current.CancellationToken);

        skill.Name.ShouldBe("hello");

        // Reportable as null by a server that does not state it, so the assertion is on what was sent.
        skill.Version.ShouldBe("1.2.0");
        skill.Md5.ShouldBe("hash-1");
        skill.Archive.ToArray().ShouldBe(Archive);
    }

    [Fact]
    public async Task GetSkillAsync_AsksForTheSelectorsTheCallerNamed()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.Zip(Archive, "hash-1", "1.2.0"));
        AITestServer server = CreateServer(http);
        await using NacosAIClient client = server.CreateClient();

        await client.Skill.GetSkillAsync(
            "hello", version: "1.2.0", label: "stable", ct: TestContext.Current.CancellationToken);

        http.Requests[0].AbsolutePath.ShouldEndWith("/v3/client/ai/skills");
        http.Query(0, "namespaceId").ShouldBe("public");
        http.Query(0, "name").ShouldBe("hello");
        http.Query(0, "version").ShouldBe("1.2.0");
        http.Query(0, "label").ShouldBe("stable");

        // A plain read wants the content, so there is nothing to be answered with "not modified".
        http.Query(0, "md5").ShouldBeNull();
    }

    [Fact]
    public async Task GetSkillAsync_LeavesBothSelectorsUnsetForTheLatestVersion()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.Zip(Archive, "hash-1", "1.2.0"));
        AITestServer server = CreateServer(http);
        await using NacosAIClient client = server.CreateClient();

        await client.Skill.GetSkillAsync("hello", ct: TestContext.Current.CancellationToken);

        http.Query(0, "version").ShouldBeNull();
        http.Query(0, "label").ShouldBeNull();
    }

    [Fact]
    public async Task GetSkillAsync_RejectsABlankName()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.Zip(Archive));
        AITestServer server = CreateServer(http);
        await using NacosAIClient client = server.CreateClient();

        await Should.ThrowAsync<ArgumentException>(
            () => client.Skill.GetSkillAsync(" ", ct: TestContext.Current.CancellationToken));

        http.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetSkillAsync_ReportsAMissingSkillWithTheServersMessage()
    {
        var http = new AIHttpTestServer(
            (_, _) => AIHttpTestServer.NotFound("Skill not found: hello"));
        AITestServer server = CreateServer(http);
        await using NacosAIClient client = server.CreateClient();

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Skill.GetSkillAsync("hello", ct: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(404);
        exception.Message.ShouldBe("Skill not found: hello");
    }

    [Fact]
    public async Task GetSkillAsync_ReportsAnUnresolvedLabelAsAMissingVersion()
    {
        // Unlike a prompt, a skill label that does not resolve is not answered with latest, so the
        // server's version message reaches the caller instead of a silent substitution.
        var http = new AIHttpTestServer(
            (_, _) => AIHttpTestServer.NotFound("Skill version not found: hello"));
        AITestServer server = CreateServer(http);
        await using NacosAIClient client = server.CreateClient();

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Skill.GetSkillAsync(
                "hello", label: "nope", ct: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(404);
        exception.Message.ShouldBe("Skill version not found: hello");
    }

    [Fact]
    public async Task GetSkillAsync_ReportsAnAuthRejectionAsAnAuthFailure()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.Result(
            HttpStatusCode.Forbidden, 403, "user not found!"));
        AITestServer server = CreateServer(http);
        await using NacosAIClient client = server.CreateClient();

        NacosAuthException exception = await Should.ThrowAsync<NacosAuthException>(
            () => client.Skill.GetSkillAsync("hello", ct: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(403);
        exception.Message.ShouldBe("user not found!");
    }

    [Fact]
    public async Task GetSkillAsync_ReportsAnUnexpectedStatus()
    {
        var http = new AIHttpTestServer(
            (_, _) => AIHttpTestServer.Result(HttpStatusCode.BadGateway, 500, "upstream failed"));
        AITestServer server = CreateServer(http);
        await using NacosAIClient client = server.CreateClient();

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Skill.GetSkillAsync("hello", ct: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(502);
        exception.Message.ShouldBe("upstream failed");
    }

    [Fact]
    public async Task GetSkillAsync_RejectsABodyThatIsNotAnArchive()
    {
        var http = new AIHttpTestServer((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not a zip</html>"),
        });

        AITestServer server = CreateServer(http);
        await using NacosAIClient client = server.CreateClient();

        // A 200 carrying something else — an error page from an intermediary, say — must not reach
        // an extractor as a corrupt file.
        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Skill.GetSkillAsync("hello", ct: TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("not a ZIP archive");
    }

    [Fact]
    public async Task GetSkillAsync_RejectsACorruptArchive()
    {
        var archive = new byte[40];
        "PK\u0003\u0004"u8.CopyTo(archive);

        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.Zip(archive));
        AITestServer server = CreateServer(http);
        await using NacosAIClient client = server.CreateClient();

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Skill.GetSkillAsync("hello", ct: TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("corrupt ZIP archive");
    }

    [Fact]
    public async Task GetSkillAsync_RejectsAnEntryThatEscapesItsDirectory()
    {
        byte[] archive = AIHttpTestServer.CreateArchive(("hello/SKILL.md", "# Hello"), ("../evil.md", "x"));

        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.Zip(archive));
        AITestServer server = CreateServer(http);
        await using NacosAIClient client = server.CreateClient();

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Skill.GetSkillAsync("hello", ct: TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("escapes its directory");
    }

    [Fact]
    public async Task GetSkillAsync_RejectsAnAbsoluteEntry()
    {
        byte[] archive = AIHttpTestServer.CreateArchive(("/etc/passwd", "x"));

        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.Zip(archive));
        AITestServer server = CreateServer(http);
        await using NacosAIClient client = server.CreateClient();

        await Should.ThrowAsync<NacosServerException>(
            () => client.Skill.GetSkillAsync("hello", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetSkillAsync_DoesNotOpenTheAIConnection()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.Zip(Archive, "hash-1", "1.0.0"));
        AITestServer server = CreateServer(http);
        await using NacosAIClient client = server.CreateClient();

        await client.Skill.GetSkillAsync("hello", ct: TestContext.Current.CancellationToken);

        // Skills are served over HTTP only, so a caller that only reads skills never opens a gRPC
        // stream it would not use.
        server.Factory.RequestedAddresses.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetSkillAsync_RejectsAReadAfterTheClientIsDisposed()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.Zip(Archive));
        AITestServer server = CreateServer(http);
        NacosAIClient client = server.CreateClient();

        await client.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(
            () => client.Skill.GetSkillAsync("hello", ct: TestContext.Current.CancellationToken));
    }

    private static AITestServer CreateServer(AIHttpTestServer http) => new(httpHandler: http);
}
