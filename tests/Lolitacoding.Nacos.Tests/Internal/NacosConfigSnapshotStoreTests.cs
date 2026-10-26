using Lolitacoding.Nacos.Config;
using Lolitacoding.Nacos.Internal;

namespace Lolitacoding.Nacos.Tests.Internal;

/// <summary>
/// Verifies the on-disk snapshot store used for failover.
/// </summary>
public sealed class NacosConfigSnapshotStoreTests : IDisposable
{
    private readonly string _root = TestPaths.CreateScratchDirectory("config-snapshot");

    public void Dispose() => TestPaths.TryDelete(_root);

    [Fact]
    public async Task SaveThenLoad_RoundTripsContentAndType()
    {
        var store = new NacosConfigSnapshotStore(_root);
        var key = new NacosConfigKey("app.json", "DEFAULT_GROUP", "public");

        await store.SaveAsync(
            key, """{"a":1}""", "json", ct: TestContext.Current.CancellationToken);

        (string Content, string? ContentType, string? EncryptedDataKey)? loaded =
            await store.LoadAsync(key, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded.Value.Content.ShouldBe("""{"a":1}""");
        loaded.Value.ContentType.ShouldBe("json");
        loaded.Value.EncryptedDataKey.ShouldBeNull();
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsTheEncryptedDataKey()
    {
        // The key is what makes an encrypted value readable after an outage. Losing it would turn
        // a usable snapshot into ciphertext nobody can decrypt.
        var store = new NacosConfigSnapshotStore(_root);
        var key = new NacosConfigKey("cipher-AES-app", "DEFAULT_GROUP", "public");

        await store.SaveAsync(
            key,
            "ciphertext",
            "text",
            encryptedDataKey: "wrapped-key",
            ct: TestContext.Current.CancellationToken);

        (string Content, string? ContentType, string? EncryptedDataKey)? loaded =
            await store.LoadAsync(key, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded.Value.Content.ShouldBe("ciphertext");
        loaded.Value.EncryptedDataKey.ShouldBe("wrapped-key");
    }

    [Fact]
    public async Task Save_PreservesAKeyWithMetadataSeparatorTextInIt()
    {
        // Both metadata fields are base64-encoded, so a key that happens to contain the separator
        // must not be able to split the file in the wrong place.
        var store = new NacosConfigSnapshotStore(_root);
        var key = new NacosConfigKey("cipher-AES-app", "DEFAULT_GROUP", "public");
        string secret = "line1\n---nacos-meta---\nline2";

        await store.SaveAsync(
            key,
            "ciphertext",
            "text",
            encryptedDataKey: secret,
            ct: TestContext.Current.CancellationToken);

        (string Content, string? ContentType, string? EncryptedDataKey)? loaded =
            await store.LoadAsync(key, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded.Value.EncryptedDataKey.ShouldBe(secret);
    }

    [Fact]
    public async Task Load_ReadsASnapshotWrittenBeforeTheKeyWasStored()
    {
        // Files written by an older version have no version marker: the first line is the content
        // type itself. They must stay readable rather than be discarded on upgrade.
        var store = new NacosConfigSnapshotStore(_root);
        var key = new NacosConfigKey("app", "DEFAULT_GROUP", "public");

        await store.SaveAsync(key, "placeholder", "text", ct: TestContext.Current.CancellationToken);

        string configDirectory = Path.Combine(_root, "config");
        string file = Directory.GetFiles(configDirectory).ShouldHaveSingleItem();
        await File.WriteAllTextAsync(
            file,
            "json\n---nacos-meta---\nlegacy",
            TestContext.Current.CancellationToken);

        (string Content, string? ContentType, string? EncryptedDataKey)? loaded =
            await store.LoadAsync(key, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded.Value.Content.ShouldBe("legacy");
        loaded.Value.ContentType.ShouldBe("json");
        loaded.Value.EncryptedDataKey.ShouldBeNull();
    }

    [Fact]
    public async Task Load_IgnoresMalformedMetadata()
    {
        var store = new NacosConfigSnapshotStore(_root);
        var key = new NacosConfigKey("app", "DEFAULT_GROUP", "public");

        await store.SaveAsync(key, "placeholder", "text", ct: TestContext.Current.CancellationToken);

        string configDirectory = Path.Combine(_root, "config");
        string file = Directory.GetFiles(configDirectory).ShouldHaveSingleItem();
        await File.WriteAllTextAsync(
            file,
            "v2\nnot-base64!\n\n---nacos-meta---\ncontent",
            TestContext.Current.CancellationToken);

        (await store.LoadAsync(key, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Load_ReturnsNullWhenNoSnapshotExists()
    {
        var store = new NacosConfigSnapshotStore(_root);

        (string Content, string? ContentType, string? EncryptedDataKey)? loaded = await store.LoadAsync(
            new NacosConfigKey("missing", "DEFAULT_GROUP", "public"),
            TestContext.Current.CancellationToken);

        loaded.ShouldBeNull();
    }

    [Fact]
    public async Task Save_ReplacesAnExistingSnapshot()
    {
        var store = new NacosConfigSnapshotStore(_root);
        var key = new NacosConfigKey("app", "DEFAULT_GROUP", "public");

        await store.SaveAsync(key, "old", "text", ct: TestContext.Current.CancellationToken);
        await store.SaveAsync(key, "new", "text", ct: TestContext.Current.CancellationToken);

        (string Content, string? ContentType, string? EncryptedDataKey)? loaded =
            await store.LoadAsync(key, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded.Value.Content.ShouldBe("new");
    }

    [Fact]
    public async Task Save_PreservesContentVerbatim()
    {
        // The payload is arbitrary caller text that may itself look like this store's own format.
        // Storing it byte for byte is the only safe behaviour.
        var store = new NacosConfigSnapshotStore(_root);
        var key = new NacosConfigKey("tricky", "DEFAULT_GROUP", "public");
        string content = "line1\n---nacos-meta---\nline2\n\ttabbed \r\n";

        await store.SaveAsync(key, content, "text", ct: TestContext.Current.CancellationToken);

        (string Content, string? ContentType, string? EncryptedDataKey)? loaded =
            await store.LoadAsync(key, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded.Value.Content.ShouldBe(content);
    }

    [Fact]
    public async Task Save_HandlesEmptyContent()
    {
        var store = new NacosConfigSnapshotStore(_root);
        var key = new NacosConfigKey("empty", "DEFAULT_GROUP", "public");

        await store.SaveAsync(
            key, string.Empty, "text", ct: TestContext.Current.CancellationToken);

        (string Content, string? ContentType, string? EncryptedDataKey)? loaded =
            await store.LoadAsync(key, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded.Value.Content.ShouldBeEmpty();
    }

    [Fact]
    public async Task Save_KeepsConfigurationsIndependent()
    {
        var store = new NacosConfigSnapshotStore(_root);

        var first = new NacosConfigKey("app", "GROUP_A", "public");
        var second = new NacosConfigKey("app", "GROUP_B", "public");
        var third = new NacosConfigKey("app", "GROUP_A", "other-namespace");

        await store.SaveAsync(first, "one", "text", ct: TestContext.Current.CancellationToken);
        await store.SaveAsync(second, "two", "text", ct: TestContext.Current.CancellationToken);
        await store.SaveAsync(third, "three", "text", ct: TestContext.Current.CancellationToken);

        (await store.LoadAsync(first, TestContext.Current.CancellationToken))!.Value.Content.ShouldBe("one");
        (await store.LoadAsync(second, TestContext.Current.CancellationToken))!.Value.Content.ShouldBe("two");
        (await store.LoadAsync(third, TestContext.Current.CancellationToken))!.Value.Content.ShouldBe("three");
    }

    [Fact]
    public async Task Save_CreatesNoTemporaryFileBehind()
    {
        // A leftover temporary file would accumulate one entry per write for the life of the
        // application.
        var store = new NacosConfigSnapshotStore(_root);
        var key = new NacosConfigKey("app", "DEFAULT_GROUP", "public");

        await store.SaveAsync(key, "content", "text", ct: TestContext.Current.CancellationToken);

        string configDirectory = Path.Combine(_root, "config");
        Directory.GetFiles(configDirectory).ShouldHaveSingleItem();
        Directory.GetFiles(configDirectory, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public async Task Remove_DeletesTheSnapshot()
    {
        var store = new NacosConfigSnapshotStore(_root);
        var key = new NacosConfigKey("app", "DEFAULT_GROUP", "public");

        await store.SaveAsync(key, "content", "text", ct: TestContext.Current.CancellationToken);
        store.Remove(key);

        (await store.LoadAsync(key, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public void Remove_IsSafeWhenNothingWasStored()
    {
        var store = new NacosConfigSnapshotStore(_root);

        Should.NotThrow(() => store.Remove(new NacosConfigKey("absent", "DEFAULT_GROUP", "public")));
    }

    [Fact]
    public async Task Load_IgnoresAFileThatIsNotInTheExpectedFormat()
    {
        // A truncated or foreign file must be treated as absent so the caller falls back to the
        // server rather than applying content that cannot be trusted.
        var store = new NacosConfigSnapshotStore(_root);
        var key = new NacosConfigKey("app", "DEFAULT_GROUP", "public");

        await store.SaveAsync(key, "content", "text", ct: TestContext.Current.CancellationToken);

        string configDirectory = Path.Combine(_root, "config");
        string file = Directory.GetFiles(configDirectory).ShouldHaveSingleItem();
        await File.WriteAllTextAsync(file, "no separator here", TestContext.Current.CancellationToken);

        (await store.LoadAsync(key, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Save_StoresConfigurationWithContentTypeAbsent()
    {
        var store = new NacosConfigSnapshotStore(_root);
        var key = new NacosConfigKey("app", "DEFAULT_GROUP", "public");

        await store.SaveAsync(key, "content", null, ct: TestContext.Current.CancellationToken);

        (string Content, string? ContentType, string? EncryptedDataKey)? loaded =
            await store.LoadAsync(key, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded.Value.Content.ShouldBe("content");
        loaded.Value.ContentType.ShouldBeNull();
    }

    [Fact]
    public async Task Save_UsesAPathDerivedFromACoordinatesHash()
    {
        // Data ids are arbitrary caller input, so the file name must not be derived from them
        // directly: a data id containing a path separator would otherwise escape the directory.
        var store = new NacosConfigSnapshotStore(_root);
        var key = new NacosConfigKey("../../escape", "DEFAULT_GROUP", "public");

        await store.SaveAsync(key, "content", "text", ct: TestContext.Current.CancellationToken);

        string configDirectory = Path.Combine(_root, "config");
        Directory.GetFiles(configDirectory).ShouldHaveSingleItem();

        // Nothing may have been created outside the snapshot directory.
        File.Exists(Path.Combine(_root, "escape")).ShouldBeFalse();
        File.Exists(Path.GetFullPath(Path.Combine(configDirectory, "..", "..", "escape"))).ShouldBeFalse();
    }
}
