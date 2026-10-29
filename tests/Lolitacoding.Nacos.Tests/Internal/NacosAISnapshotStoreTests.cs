using Lolitacoding.Nacos.AI;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Tests.Internal;

/// <summary>
/// Verifies the on-disk snapshot store the AI subscriptions fail over to.
/// </summary>
public sealed class NacosAISnapshotStoreTests : IDisposable
{
    private readonly string _root = TestPaths.CreateScratchDirectory("ai-snapshot");

    public void Dispose() => TestPaths.TryDelete(_root);

    private NacosAISnapshotStore Store => new(_root);

    [Fact]
    public async Task SaveThenLoad_RoundTripsTheResource()
    {
        NacosAISnapshotStore store = Store;
        NacosPrompt prompt = new() { PromptKey = "greeting", Version = "1.0.0", Template = "Hello {{name}}" };

        await store.SaveAsync(
            NacosAISnapshotStore.PromptKind,
            "greeting::latest",
            prompt,
            NacosJsonContext.Default.NacosPrompt,
            TestContext.Current.CancellationToken);

        NacosPrompt? loaded = await store.LoadAsync(
            NacosAISnapshotStore.PromptKind,
            "greeting::latest",
            NacosJsonContext.Default.NacosPrompt,
            TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded.PromptKey.ShouldBe("greeting");
        loaded.Version.ShouldBe("1.0.0");
        loaded.Template.ShouldBe("Hello {{name}}");
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsASkillArchive()
    {
        NacosAISnapshotStore store = Store;
        NacosSkill skill = new() { Name = "numbers", Version = "2.0.0", Archive = new byte[] { 1, 2, 3, 4 } };

        await store.SaveAsync(
            NacosAISnapshotStore.SkillKind,
            "numbers::latest",
            skill,
            NacosJsonContext.Default.NacosSkill,
            TestContext.Current.CancellationToken);

        NacosSkill? loaded = await store.LoadAsync(
            NacosAISnapshotStore.SkillKind,
            "numbers::latest",
            NacosJsonContext.Default.NacosSkill,
            TestContext.Current.CancellationToken);

        // A skill is a ZIP archive rather than a document, and the archive is the part that matters:
        // the round trip has to preserve the bytes.
        loaded.ShouldNotBeNull();
        loaded.Archive.ToArray().ShouldBe([1, 2, 3, 4]);
    }

    [Fact]
    public async Task Load_ReportsNothingWhenNoSnapshotWasWritten()
    {
        NacosPrompt? loaded = await Store.LoadAsync(
            NacosAISnapshotStore.PromptKind,
            "greeting::latest",
            NacosJsonContext.Default.NacosPrompt,
            TestContext.Current.CancellationToken);

        loaded.ShouldBeNull();
    }

    [Fact]
    public async Task Load_ReportsNothingWhenTheSnapshotCannotBeReadBack()
    {
        NacosAISnapshotStore store = Store;

        await store.SaveAsync(
            NacosAISnapshotStore.PromptKind,
            "greeting::latest",
            new NacosPrompt { PromptKey = "greeting" },
            NacosJsonContext.Default.NacosPrompt,
            TestContext.Current.CancellationToken);

        // A snapshot written by something else, or by a version that stored another shape, is no
        // better than one that was never written; the caller has to fall through to the server
        // rather than fail on the file.
        string snapshot = Directory.GetFiles(Path.Combine(_root, "ai")).ShouldHaveSingleItem();
        await File.WriteAllTextAsync(snapshot, "{ this is not json", TestContext.Current.CancellationToken);

        NacosPrompt? loaded = await store.LoadAsync(
            NacosAISnapshotStore.PromptKind,
            "greeting::latest",
            NacosJsonContext.Default.NacosPrompt,
            TestContext.Current.CancellationToken);

        loaded.ShouldBeNull();
    }

    [Fact]
    public async Task Remove_MakesTheSnapshotDisappear()
    {
        NacosAISnapshotStore store = Store;

        await store.SaveAsync(
            NacosAISnapshotStore.PromptKind,
            "greeting::latest",
            new NacosPrompt { PromptKey = "greeting" },
            NacosJsonContext.Default.NacosPrompt,
            TestContext.Current.CancellationToken);

        store.Remove(NacosAISnapshotStore.PromptKind, "greeting::latest");

        NacosPrompt? loaded = await store.LoadAsync(
            NacosAISnapshotStore.PromptKind,
            "greeting::latest",
            NacosJsonContext.Default.NacosPrompt,
            TestContext.Current.CancellationToken);

        loaded.ShouldBeNull();
    }

    [Fact]
    public async Task Save_KeepsTwoKindsWithTheSameKeyApart()
    {
        NacosAISnapshotStore store = Store;
        CancellationToken ct = TestContext.Current.CancellationToken;

        await store.SaveAsync(
            NacosAISnapshotStore.PromptKind,
            "shared::latest",
            new NacosPrompt { PromptKey = "shared", Template = "prompt" },
            NacosJsonContext.Default.NacosPrompt,
            ct);

        await store.SaveAsync(
            NacosAISnapshotStore.SkillKind,
            "shared::latest",
            new NacosSkill { Name = "shared", Archive = new byte[] { 9 } },
            NacosJsonContext.Default.NacosSkill,
            ct);

        // Two resources can legitimately share a name, so the kind is part of the identity: reading
        // one must never answer with the other.
        (await store.LoadAsync(
            NacosAISnapshotStore.PromptKind,
            "shared::latest",
            NacosJsonContext.Default.NacosPrompt,
            ct))!.Template.ShouldBe("prompt");

        (await store.LoadAsync(
            NacosAISnapshotStore.SkillKind,
            "shared::latest",
            NacosJsonContext.Default.NacosSkill,
            ct))!.Archive.ToArray().ShouldBe([9]);
    }

    [Fact]
    public async Task Save_ReplacesTheSnapshotOfTheSameSelector()
    {
        NacosAISnapshotStore store = Store;
        CancellationToken ct = TestContext.Current.CancellationToken;

        await store.SaveAsync(
            NacosAISnapshotStore.PromptKind,
            "greeting::latest",
            new NacosPrompt { PromptKey = "greeting", Template = "first" },
            NacosJsonContext.Default.NacosPrompt,
            ct);

        await store.SaveAsync(
            NacosAISnapshotStore.PromptKind,
            "greeting::latest",
            new NacosPrompt { PromptKey = "greeting", Template = "second" },
            NacosJsonContext.Default.NacosPrompt,
            ct);

        NacosPrompt? loaded = await store.LoadAsync(
            NacosAISnapshotStore.PromptKind,
            "greeting::latest",
            NacosJsonContext.Default.NacosPrompt,
            ct);

        loaded!.Template.ShouldBe("second");
    }

    [Fact]
    public void Constructor_RejectsABlankDirectory()
    {
        Should.Throw<ArgumentException>(() => new NacosAISnapshotStore("  "));
    }

    [Fact]
    public async Task Save_ReportsAWriteThatCouldNotBeCompleted()
    {
        // The AI store follows the same contract as the configuration and naming stores: the write
        // failure is swallowed so a read is never failed by caching, and reported so a missing
        // failover copy is visible.
        var log = new RecordingLoggerFactory();
        var store = new NacosAISnapshotStore(BlockedRoot(), log.CreateLogger("test"));
        NacosPrompt prompt = new() { PromptKey = "greeting", Version = "1.0.0", Template = "hi" };

        await store.SaveAsync(
            NacosAISnapshotStore.PromptKind,
            "greeting::latest",
            prompt,
            NacosJsonContext.Default.NacosPrompt,
            TestContext.Current.CancellationToken);

        RecordedLogEntry entry = log.ForEvent(8042).ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldStartWith("Caching prompt greeting::latest on disk failed:");
    }

    /// <summary>Creates a cache root whose snapshot directory cannot be created.</summary>
    /// <returns>The root directory.</returns>
    private string BlockedRoot()
    {
        string root = Path.Combine(_root, "blocked");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ai"), string.Empty);

        return root;
    }
}
