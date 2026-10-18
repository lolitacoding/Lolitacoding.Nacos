using System.IO.Compression;
using Lolitacoding.Nacos.Ai;
using Lolitacoding.Nacos.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.IntegrationTests;

/// <summary>
/// Verifies the skill registry end to end against a real Nacos server.
/// </summary>
/// <remarks>
/// <para>
/// A skill is the one AI resource whose payload is an archive, so what is worth checking against the
/// real thing is the shape of that archive and the headers that describe it: that the version a
/// selector resolved to is reported, that the hash the server publishes comes back, and that the
/// files are the ones that were stored. A label that points at an older version, a version that was
/// taken offline, and a name that was never published are the three answers the server decides rather
/// than the client.
/// </para>
/// <para>
/// The fixture publishes through the HTTP admin API, because there is no client-side way to publish a
/// skill — the client surface is the read path under test. A new skill is created from its card; a
/// later version is forked from the previous one and edited, and publishing the draft is what makes it
/// readable.
/// </para>
/// <para>
/// The container is started from the AI image, which is newer than the server the rest of the suite is
/// pinned to: the skill registry shipped in Nacos 3.2.
/// </para>
/// </remarks>
[Trait(NacosTraits.Category, NacosTraits.Integration)]
public sealed class SkillIntegrationTests
{
    /// <summary>The version the fixture publishes first.</summary>
    private const string FirstVersion = "1.0.0";

    /// <summary>The version the fixture publishes second, which the latest label moves to.</summary>
    private const string SecondVersion = "2.0.0";

    [Fact]
    public async Task PublishAndRead_RoundTripsThroughARealServer()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AiImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;
        string skillName = NacosHttp.UniqueId("skill");

        using var http = new HttpClient();
        string token = await container.CreateAccessTokenAsync(ct);

        await NacosHttp.PublishSkillAsync(
            http,
            container.HttpAddress,
            skillName,
            FirstVersion,
            NacosHttp.SkillMarkdown(skillName, FirstVersion, "# Instructions\n\nDo the thing."),
            basedOnVersion: null,
            resource: ("reference/notes.txt", "notes from 1.0.0"),
            accessToken: token,
            ct: ct);

        // A publish answers before the version is servable, and a read that gets there first is
        // answered the way an unknown skill is. The archive is what the version serves, and it is
        // stored a moment after the version row, so the wait is on the archive being there rather
        // than only on the version resolving — a read that stopped at the version would hand back a
        // skill with nothing in it.
        NacosSkill skill = await NacosEventually.BecomesAsync(
            () => client.Ai.Skill.GetSkillAsync(skillName, ct: ct),
            value => value.Version == FirstVersion && value.Archive.Length > 0,
            ct,
            tolerateFailures: true);

        skill.Name.ShouldBe(skillName);
        skill.Version.ShouldBe(FirstVersion);
        skill.Md5.ShouldNotBeNullOrWhiteSpace();

        // The archive is the server's own layout: the skill's directory, its descriptor, and the
        // resource files that were stored with it.
        IReadOnlyDictionary<string, string> entries = ReadEntries(skill);

        entries.Keys.ShouldBe(
            [string.Concat(skillName, "/SKILL.md"), string.Concat(skillName, "/reference/notes.txt")],
            ignoreOrder: true);

        entries[string.Concat(skillName, "/SKILL.md")].ShouldContain("Do the thing.");
        entries[string.Concat(skillName, "/reference/notes.txt")].ShouldBe("notes from 1.0.0");
    }

    [Fact]
    public async Task GetSkillAsync_ResolvesAVersionAndALabelTheWayTheServerDoes()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AiImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;
        string skillName = NacosHttp.UniqueId("skill-pin");

        using var http = new HttpClient();
        string token = await container.CreateAccessTokenAsync(ct);

        await NacosHttp.PublishSkillAsync(
            http,
            container.HttpAddress,
            skillName,
            FirstVersion,
            NacosHttp.SkillMarkdown(skillName, FirstVersion, "# Instructions\n\nVersion one."),
            basedOnVersion: null,
            resource: null,
            accessToken: token,
            ct: ct);

        await NacosHttp.PublishSkillAsync(
            http,
            container.HttpAddress,
            skillName,
            SecondVersion,
            NacosHttp.SkillMarkdown(skillName, SecondVersion, "# Instructions\n\nVersion two."),
            basedOnVersion: FirstVersion,
            resource: null,
            accessToken: token,
            ct: ct);

        // The labels are written as a whole map, and the reserved latest label is included so
        // binding stable does not drop it: a read without a selector resolves through latest, so
        // leaving it out would make that read a missing version rather than the current one.
        await NacosHttp.BindSkillLabelsAsync(
            http,
            container.HttpAddress,
            skillName,
            $$"""{"stable":"{{FirstVersion}}","latest":"{{SecondVersion}}"}""",
            token,
            ct);

        // A read that names no version resolves to the version labelled latest.
        NacosSkill latest = await NacosEventually.BecomesAsync(
            () => client.Ai.Skill.GetSkillAsync(skillName, ct: ct),
            value => value.Version == SecondVersion && value.Archive.Length > 0,
            ct,
            tolerateFailures: true);

        latest.Version.ShouldBe(SecondVersion);
        ReadEntries(latest)[string.Concat(skillName, "/SKILL.md")].ShouldContain("Version two.");

        // A read that names a version resolves to that version rather than to the current one, which
        // is what lets a caller pin an older version after a newer one was published.
        NacosSkill pinned = await client.Ai.Skill.GetSkillAsync(skillName, FirstVersion, ct: ct);

        pinned.Version.ShouldBe(FirstVersion);
        ReadEntries(pinned)[string.Concat(skillName, "/SKILL.md")].ShouldContain("Version one.");

        // A label is a pointer the server moves; reading it back is what shows the client sends the
        // label rather than guessing which version it points at.
        NacosSkill labelled = await client.Ai.Skill.GetSkillAsync(skillName, label: "stable", ct: ct);

        labelled.Version.ShouldBe(FirstVersion);
    }

    [Fact]
    public async Task GetSkillAsync_ReportsAnUnknownSkillAndAnUnknownVersion()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AiImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;

        // A release first, so that the version error below is the registry's answer to a version
        // rather than to a skill that does not exist at all. The two are different failures.
        string skillName = NacosHttp.UniqueId("skill-absent");

        using var http = new HttpClient();
        string token = await container.CreateAccessTokenAsync(ct);

        await NacosHttp.PublishSkillAsync(
            http,
            container.HttpAddress,
            skillName,
            FirstVersion,
            NacosHttp.SkillMarkdown(skillName, FirstVersion, "# Instructions\n\nPresent."),
            basedOnVersion: null,
            resource: null,
            accessToken: token,
            ct: ct);

        NacosServerException unknownSkill = await Should.ThrowAsync<NacosServerException>(
            () => client.Ai.Skill.GetSkillAsync(NacosHttp.UniqueId("skill-nothing"), ct: ct));

        unknownSkill.ErrorCode.ShouldBe(404);

        NacosServerException unknownVersion = await Should.ThrowAsync<NacosServerException>(
            () => client.Ai.Skill.GetSkillAsync(skillName, "9.9.9", ct: ct));

        unknownVersion.ErrorCode.ShouldBe(404);
    }

    [Fact]
    public async Task SubscribeSkillAsync_ReportsANewVersion()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AiImage);
        await using NacosClientFactory client = CreateClient(container, TimeSpan.FromMilliseconds(500));

        CancellationToken ct = TestContext.Current.CancellationToken;
        string skillName = NacosHttp.UniqueId("skill-watch");

        using var http = new HttpClient();
        string token = await container.CreateAccessTokenAsync(ct);

        await NacosHttp.PublishSkillAsync(
            http,
            container.HttpAddress,
            skillName,
            FirstVersion,
            NacosHttp.SkillMarkdown(skillName, FirstVersion, "# Instructions\n\nVersion one."),
            basedOnVersion: null,
            resource: null,
            accessToken: token,
            ct: ct);

        await using IAsyncEnumerator<NacosSkill?> changes =
            client.Ai.Skill.SubscribeSkillAsync(skillName, ct: ct).GetAsyncEnumerator(ct);

        (await changes.MoveNextAsync()).ShouldBeTrue();
        changes.Current.ShouldNotBeNull();
        changes.Current.Version.ShouldBe(FirstVersion);

        await NacosHttp.PublishSkillAsync(
            http,
            container.HttpAddress,
            skillName,
            SecondVersion,
            NacosHttp.SkillMarkdown(skillName, SecondVersion, "# Instructions\n\nVersion two."),
            basedOnVersion: FirstVersion,
            resource: null,
            accessToken: token,
            ct: ct);

        // Nacos pushes no skill change, so the subscription is the refresh loop noticing that the
        // version a read without a selector resolves to has moved.
        await NacosEventually.ReportsAsync(changes, skill => skill?.Version == SecondVersion, ct);

        changes.Current!.Version.ShouldBe(SecondVersion);
    }

    [Fact]
    public async Task SubscribeSkillAsync_ReportsAWithdrawal()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AiImage);
        await using NacosClientFactory client = CreateClient(container, TimeSpan.FromMilliseconds(500));

        CancellationToken ct = TestContext.Current.CancellationToken;
        string skillName = NacosHttp.UniqueId("skill-withdraw");

        using var http = new HttpClient();
        string token = await container.CreateAccessTokenAsync(ct);

        await NacosHttp.PublishSkillAsync(
            http,
            container.HttpAddress,
            skillName,
            FirstVersion,
            NacosHttp.SkillMarkdown(skillName, FirstVersion, "# Instructions\n\nGoing away."),
            basedOnVersion: null,
            resource: null,
            accessToken: token,
            ct: ct);

        await using IAsyncEnumerator<NacosSkill?> changes =
            client.Ai.Skill.SubscribeSkillAsync(skillName, ct: ct).GetAsyncEnumerator(ct);

        (await changes.MoveNextAsync()).ShouldBeTrue();
        changes.Current.ShouldNotBeNull();
        changes.Current.Version.ShouldBe(FirstVersion);

        await NacosHttp.OfflineSkillAsync(http, container.HttpAddress, skillName, FirstVersion, token, ct);

        // A skill that is gone is reported rather than waited out, which is what the null element is:
        // a subscriber that was never told would keep loading an archive that no longer exists.
        await NacosEventually.ReportsAsync(changes, skill => skill is null, ct);

        changes.Current.ShouldBeNull();
    }

    /// <summary>
    /// Reads the entries of a skill archive.
    /// </summary>
    /// <param name="skill">The skill whose archive is read.</param>
    /// <returns>The entry names and their text content.</returns>
    private static Dictionary<string, string> ReadEntries(NacosSkill skill)
    {
        using var stream = new MemoryStream(skill.Archive.ToArray());
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        var entries = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open(), System.Text.Encoding.UTF8);

            entries[entry.FullName] = reader.ReadToEnd();
        }

        return entries;
    }

    private static NacosClientFactory CreateClient(
        NacosContainer container,
        TimeSpan? cacheRefreshInterval = null) =>
        NacosClientFactory.Create(
            new NacosOptions
            {
                ServerAddresses = [container.HttpAddress],
                ConnectionTimeout = TimeSpan.FromSeconds(15),
                Ai = new NacosAiOptions
                {
                    CacheRefreshInterval = cacheRefreshInterval ?? TimeSpan.FromSeconds(10),
                },
                Reconnect = new NacosReconnectOptions
                {
                    InitialDelay = TimeSpan.FromMilliseconds(500),
                    MaxDelay = TimeSpan.FromSeconds(5),
                    JitterFactor = 0.1,
                },
            },
            httpClient: null,
            channelFactory: null,
            timeProvider: TimeProvider.System,
            loggerFactory: NullLoggerFactory.Instance);
}
