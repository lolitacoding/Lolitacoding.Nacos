using Lolitacoding.Nacos.Config;

namespace Lolitacoding.Nacos.Tests.Config;

/// <summary>
/// Verifies the client-side configuration filter chain and the built-in encryption filter.
/// </summary>
/// <remarks>
/// The behaviour under test is the one the official Java client implements as
/// <c>ConfigEncryptionFilter</c> plus <c>EncryptionHandler</c>: a data id opts into encryption by
/// starting with <c>cipher-</c>, the algorithm is the segment after that prefix, and an unknown
/// algorithm leaves the value alone rather than failing the operation.
/// </remarks>
public sealed class NacosConfigFilterTests
{
    private const string Group = "DEFAULT_GROUP";

    [Fact]
    public async Task Publish_EncryptsACipherDataIdAndWrapsTheDataKey()
    {
        var provider = new FakeEncryptionProvider();
        var filter = new NacosConfigEncryptionFilter([provider]);
        var context = new NacosConfigFilterContext(
            new NacosConfigKey("cipher-AES-application.json", Group, "public"),
            NacosConfigFilterStage.Publish,
            content: "plain",
            contentType: "text");

        await filter.InvokeAsync(context, TestContext.Current.CancellationToken);

        context.Content.ShouldBe("cipher(plain)");
        context.EncryptedDataKey.ShouldBe("wrapped(secret)");
        provider.Calls.ShouldContain("generate");
        provider.Calls.ShouldContain("encrypt:secret");
        provider.Calls.ShouldContain("wrap:secret");
    }

    [Fact]
    public async Task Read_DecryptsACipherDataId()
    {
        var provider = new FakeEncryptionProvider();
        var filter = new NacosConfigEncryptionFilter([provider]);
        var context = new NacosConfigFilterContext(
            new NacosConfigKey("cipher-AES-application.json", Group, "public"),
            NacosConfigFilterStage.Read,
            content: "cipher(plain)",
            contentType: "text",
            encryptedDataKey: "wrapped(secret)",
            md5: "stored-hash");

        await filter.InvokeAsync(context, TestContext.Current.CancellationToken);

        context.Content.ShouldBe("plain");

        // The stored key stays visible: the Java filter replaces it with the plaintext key
        // internally, which would leak it through NacosConfigResult.EncryptedDataKey here.
        context.EncryptedDataKey.ShouldBe("wrapped(secret)");
        context.Md5.ShouldBe("stored-hash");
    }

    [Fact]
    public async Task Publish_LeavesADataIdWithoutTheCipherPrefixAlone()
    {
        var provider = new FakeEncryptionProvider();
        var filter = new NacosConfigEncryptionFilter([provider]);
        var context = new NacosConfigFilterContext(
            new NacosConfigKey("application.json", Group, "public"),
            NacosConfigFilterStage.Publish,
            content: "plain");

        await filter.InvokeAsync(context, TestContext.Current.CancellationToken);

        context.Content.ShouldBe("plain");
        context.EncryptedDataKey.ShouldBeNull();
        provider.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Publish_LeavesADataIdThatIsOnlyThePrefixAlone()
    {
        // "cipher-" on its own names no algorithm, so the Java handler treats it as ordinary
        // content. Encrypting it would produce a value the client could never decrypt again.
        var provider = new FakeEncryptionProvider();
        var filter = new NacosConfigEncryptionFilter([provider]);
        var context = new NacosConfigFilterContext(
            new NacosConfigKey("cipher-", Group, "public"),
            NacosConfigFilterStage.Publish,
            content: "plain");

        await filter.InvokeAsync(context, TestContext.Current.CancellationToken);

        context.Content.ShouldBe("plain");
        context.EncryptedDataKey.ShouldBeNull();
        provider.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Publish_LeavesAnUnknownAlgorithmAlone()
    {
        // The Java handler logs a warning and passes the value through; failing the publish would
        // turn a missing plugin into an outage.
        var filter = new NacosConfigEncryptionFilter([new FakeEncryptionProvider { AlgorithmName = "AES" }]);
        var context = new NacosConfigFilterContext(
            new NacosConfigKey("cipher-ROT13-application.json", Group, "public"),
            NacosConfigFilterStage.Publish,
            content: "plain");

        await filter.InvokeAsync(context, TestContext.Current.CancellationToken);

        context.Content.ShouldBe("plain");
        context.EncryptedDataKey.ShouldBeNull();
    }

    [Fact]
    public async Task Publish_MatchesTheAlgorithmCaseSensitively()
    {
        var filter = new NacosConfigEncryptionFilter([new FakeEncryptionProvider { AlgorithmName = "AES" }]);
        var context = new NacosConfigFilterContext(
            new NacosConfigKey("cipher-aes-application.json", Group, "public"),
            NacosConfigFilterStage.Publish,
            content: "plain");

        await filter.InvokeAsync(context, TestContext.Current.CancellationToken);

        context.Content.ShouldBe("plain");
    }

    [Theory]
    [InlineData("cipher-AES-application.json", "AES")]
    [InlineData("cipher-AES", "AES")]
    [InlineData("cipher-AES-", "AES")]
    [InlineData("cipher-AES-group-application.json", "AES")]
    public async Task Publish_TakesTheAlgorithmFromTheSegmentAfterThePrefix(
        string dataId, string algorithm)
    {
        var provider = new FakeEncryptionProvider { AlgorithmName = algorithm };
        var filter = new NacosConfigEncryptionFilter([provider]);
        var context = new NacosConfigFilterContext(
            new NacosConfigKey(dataId, Group, "public"),
            NacosConfigFilterStage.Publish,
            content: "plain");

        await filter.InvokeAsync(context, TestContext.Current.CancellationToken);

        context.Content.ShouldBe("cipher(plain)");
    }

    [Fact]
    public async Task Publish_UsesTheFirstProviderThatMatches()
    {
        var first = new FakeEncryptionProvider { AlgorithmName = "AES", Marker = "first" };
        var second = new FakeEncryptionProvider { AlgorithmName = "AES", Marker = "second" };
        var filter = new NacosConfigEncryptionFilter([first, second]);
        var context = new NacosConfigFilterContext(
            new NacosConfigKey("cipher-AES-application.json", Group, "public"),
            NacosConfigFilterStage.Publish,
            content: "plain");

        await filter.InvokeAsync(context, TestContext.Current.CancellationToken);

        first.Calls.ShouldNotBeEmpty();
        second.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Read_LeavesMissingContentAlone()
    {
        // There is nothing to decrypt for a configuration that does not exist, and a filter must
        // not be the place that invents content.
        var provider = new FakeEncryptionProvider();
        var filter = new NacosConfigEncryptionFilter([provider]);
        var context = new NacosConfigFilterContext(
            new NacosConfigKey("cipher-AES-application.json", Group, "public"),
            NacosConfigFilterStage.Read,
            content: null);

        await filter.InvokeAsync(context, TestContext.Current.CancellationToken);

        context.Content.ShouldBeNull();
        provider.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Invoke_ThrowsWhenTheCallerCancelled()
    {
        var filter = new NacosConfigEncryptionFilter([new FakeEncryptionProvider()]);
        var context = new NacosConfigFilterContext(
            new NacosConfigKey("application.json", Group, "public"),
            NacosConfigFilterStage.Publish,
            content: "plain");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => filter.InvokeAsync(context, cts.Token).AsTask());
    }

    [Fact]
    public void Constructor_RejectsNullProviders() =>
        Should.Throw<ArgumentNullException>(
            () => new NacosConfigEncryptionFilter(null!));

    [Fact]
    public void Constructor_RejectsANullProvider() =>
        Should.Throw<ArgumentException>(
            () => new NacosConfigEncryptionFilter([null!]));

    [Fact]
    public void Constructor_RejectsAProviderWithoutAnAlgorithmName() =>
        Should.Throw<ArgumentException>(
            () => new NacosConfigEncryptionFilter([new FakeEncryptionProvider { AlgorithmName = " " }]));

    [Fact]
    public void Filter_IdentifiesItselfAndRunsFirst()
    {
        var filter = new NacosConfigEncryptionFilter([new FakeEncryptionProvider()]);

        filter.Name.ShouldBe(NacosConfigEncryptionFilter.FilterName);
        filter.Order.ShouldBe(0);
    }

    [Fact]
    public void Options_RejectANullEncryptionProvider()
    {
        var options = new NacosConfigOptions();
        options.EncryptionProviders.Add(null!);

        Should.Throw<NacosConfigurationException>(options.Validate);
    }

    [Fact]
    public void Options_RejectAnEncryptionProviderWithoutAnAlgorithmName()
    {
        var options = new NacosConfigOptions();
        options.EncryptionProviders.Add(new FakeEncryptionProvider { AlgorithmName = string.Empty });

        Should.Throw<NacosConfigurationException>(options.Validate);
    }
}
