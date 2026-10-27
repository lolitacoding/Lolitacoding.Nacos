using Lolitacoding.Nacos.Internal;

namespace Lolitacoding.Nacos.Tests.Internal;

/// <summary>
/// Verifies the shared server list that the connections and HTTP clients read through.
/// </summary>
/// <remarks>
/// The list is the one piece of state an address server mutates while the rest of the client is
/// running, so its contract is what makes a refresh safe: readers see a consistent snapshot, and a
/// replacement only notifies when the set actually changed.
/// </remarks>
public sealed class NacosServerListTests
{
    private static readonly NacosServerAddress First = NacosServerAddress.Parse("127.0.0.1:8848");
    private static readonly NacosServerAddress Second = NacosServerAddress.Parse("127.0.0.1:8849");
    private static readonly NacosServerAddress Other = NacosServerAddress.Parse("127.0.0.1:8850");

    [Fact]
    public void Replace_PublishesTheNewList()
    {
        var list = new NacosServerList([First]);

        list.Replace([Second]).ShouldBeTrue();

        list.Count.ShouldBe(1);
        list[0].Original.ShouldBe("127.0.0.1:8849");
    }

    [Fact]
    public void Replace_WithTheSameSetInADifferentOrder_ReportsNoChange()
    {
        // An endpoint that merely re-sorts the same cluster has not changed it, and reconnecting
        // for that would drop a healthy connection for nothing.
        var list = new NacosServerList([First, Second]);
        int changes = 0;
        list.Changed += (_, _) => changes++;

        list.Replace([Second, First]).ShouldBeFalse();

        changes.ShouldBe(0);
        list.Count.ShouldBe(2);
    }

    [Fact]
    public void Replace_WithADifferentSet_RaisesChanged()
    {
        var list = new NacosServerList([First]);
        int changes = 0;
        list.Changed += (_, _) => changes++;

        list.Replace([First, Other]).ShouldBeTrue();

        changes.ShouldBe(1);
        list.Count.ShouldBe(2);
    }

    [Fact]
    public void Contains_MatchesTheHttpEndpointRatherThanTheInstance()
    {
        // A refresh parses the endpoint's answer into new objects, so identity comparison would
        // always miss and every refresh would look like a withdrawal.
        var list = new NacosServerList([First]);

        list.Contains(NacosServerAddress.Parse("http://127.0.0.1:8848/nacos")).ShouldBeTrue();
        list.Contains(Second).ShouldBeFalse();
    }

    [Fact]
    public async Task WaitForItemsAsync_ReturnsImmediatelyWhenPopulated()
    {
        var list = new NacosServerList([First]);

        await list.WaitForItemsAsync(
            TimeSpan.FromSeconds(1), TimeProvider.System, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WaitForItemsAsync_CompletesWhenTheListIsFilled()
    {
        var list = new NacosServerList(
            [],
            isDynamic: true,
            source: "http://address-server:8080/nacos/serverlist");

        Task wait = list.WaitForItemsAsync(
            TimeSpan.FromSeconds(5), TimeProvider.System, TestContext.Current.CancellationToken);

        wait.IsCompleted.ShouldBeFalse();

        list.Replace([First]);

        await wait;
    }

    [Fact]
    public async Task WaitForItemsAsync_FailsWhenNothingIsPublished()
    {
        var list = new NacosServerList(
            [],
            isDynamic: true,
            source: "http://address-server:8080/nacos/serverlist");

        NacosConnectionException exception = await Should.ThrowAsync<NacosConnectionException>(
            () => list.WaitForItemsAsync(
                TimeSpan.FromMilliseconds(50),
                TimeProvider.System,
                TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("address server");
        exception.Message.ShouldContain("serverlist");
    }

    [Fact]
    public async Task WaitForItemsAsync_WithoutASource_SaysNoAddressIsConfigured()
    {
        var list = new NacosServerList([], isDynamic: true);

        NacosConnectionException exception = await Should.ThrowAsync<NacosConnectionException>(
            () => list.WaitForItemsAsync(
                TimeSpan.FromMilliseconds(50),
                TimeProvider.System,
                TestContext.Current.CancellationToken));

        exception.Message.ShouldBe("No Nacos server address is configured.");
    }
}
