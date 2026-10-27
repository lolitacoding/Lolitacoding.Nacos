using System.Net;
using Lolitacoding.Nacos.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Lolitacoding.Nacos.Tests.Internal;

/// <summary>
/// Verifies how the address server URL is built and how its answers are applied.
/// </summary>
/// <remarks>
/// The provider is the only part of the client that talks to something other than a Nacos server,
/// so these tests pin the URL shape the official Java client uses, the retry and refresh rules,
/// and the way an unusable answer is ignored rather than half-applied.
/// </remarks>
public sealed class NacosEndpointServerListProviderTests
{
    [Fact]
    public void BuildAddressServerUri_UsesTheDefaultShape()
    {
        var options = new NacosOptions { Endpoint = "address.example.com" };

        Uri uri = NacosEndpointServerListProvider.BuildAddressServerUri(options);

        uri.AbsoluteUri.ShouldBe("http://address.example.com:8080/nacos/serverlist");
    }

    [Fact]
    public void BuildAddressServerUri_AddsTheNamespaceOnlyWhenOneWasChosen()
    {
        var options = new NacosOptions { Endpoint = "address.example.com" };

        NacosEndpointServerListProvider.BuildAddressServerUri(options).Query.ShouldBeEmpty();

        options.Namespace = "tenant a";

        NacosEndpointServerListProvider.BuildAddressServerUri(options).Query
            .ShouldBe("?namespace=tenant%20a");
    }

    [Fact]
    public void BuildAddressServerUri_AppendsTheConfiguredQueryParameters()
    {
        var options = new NacosOptions
        {
            Endpoint = "address.example.com",
            EndpointContextPath = "custom",
            EndpointPort = 9000,
            EndpointServerListName = "nodes",
            EndpointQueryParams = "cluster=alpha&zone=z1",
        };

        Uri uri = NacosEndpointServerListProvider.BuildAddressServerUri(options);

        uri.AbsoluteUri.ShouldBe("http://address.example.com:9000/custom/nodes?cluster=alpha&zone=z1");
    }

    [Fact]
    public void BuildAddressServerUri_SeparatesTheNamespaceFromTheQueryParameters()
    {
        var options = new NacosOptions
        {
            Endpoint = "address.example.com",
            Namespace = "tenant",
            EndpointQueryParams = "cluster=alpha",
        };

        NacosEndpointServerListProvider.BuildAddressServerUri(options).Query
            .ShouldBe("?namespace=tenant&cluster=alpha");
    }

    [Fact]
    public async Task Seed_PublishesTheFirstList()
    {
        var clock = new FakeTimeProvider();
        var handler = new StubHandler(_ => Body("10.0.0.1\n10.0.0.2:8849"));
        var list = new NacosServerList([], isDynamic: true);
        using var client = new HttpClient(handler);
        await using var provider = CreateProvider(clock, list, client);

        await provider.SeedAsync(TestContext.Current.CancellationToken);

        handler.Requests.Count.ShouldBe(1);
        list.Count.ShouldBe(2);

        // A bare host defaults to the main port, exactly as the Java client does.
        list[0].Port.ShouldBe(8848);
        list[1].Port.ShouldBe(8849);
    }

    [Fact]
    public async Task Seed_RetriesUntilTheEndpointAnswers()
    {
        var clock = new FakeTimeProvider();
        var handler = new StubHandler(
            call => call < 2 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Body("10.0.0.1"));
        var list = new NacosServerList([], isDynamic: true);
        using var client = new HttpClient(handler);
        await using var provider = CreateProvider(clock, list, client);

        Task seed = provider.SeedAsync(TestContext.Current.CancellationToken);
        await DriveUntilAsync(clock, seed, TestContext.Current.CancellationToken);

        handler.Requests.Count.ShouldBe(3);
        list.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Seed_AfterFiveFailures_LeavesTheListEmpty()
    {
        var clock = new FakeTimeProvider();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var list = new NacosServerList([], isDynamic: true);
        using var client = new HttpClient(handler);
        await using var provider = CreateProvider(clock, list, client);

        Task seed = provider.SeedAsync(TestContext.Current.CancellationToken);
        await DriveUntilAsync(clock, seed, TestContext.Current.CancellationToken);

        handler.Requests.Count.ShouldBe(5);
        list.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Seed_KeepsAnExplicitHttpsScheme()
    {
        var clock = new FakeTimeProvider();
        var handler = new StubHandler(_ => Body("https://nacos.example.com:8443"));
        var list = new NacosServerList([], isDynamic: true);
        using var client = new HttpClient(handler);
        await using var provider = CreateProvider(clock, list, client);

        await provider.SeedAsync(TestContext.Current.CancellationToken);

        list[0].UseTls.ShouldBeTrue();
        list[0].GrpcBaseAddress.Port.ShouldBe(9443);
    }

    [Fact]
    public async Task Refresh_AppliesAChangeOnlyOncePerThirtySeconds()
    {
        var clock = new FakeTimeProvider();
        string body = "10.0.0.1";
        var handler = new StubHandler(_ => Body(body));
        var list = new NacosServerList([], isDynamic: true);
        using var client = new HttpClient(handler);
        await using var provider = CreateProvider(clock, list, client);

        await provider.SeedAsync(TestContext.Current.CancellationToken);
        list[0].Host.ShouldBe("10.0.0.1");

        body = "10.0.0.2";
        clock.Advance(TimeSpan.FromSeconds(10));

        await provider.RefreshIfDueAsync(TestContext.Current.CancellationToken);

        // The gate suppresses the fetch itself, so the endpoint is not even asked.
        handler.Requests.Count.ShouldBe(1);
        list[0].Host.ShouldBe("10.0.0.1");

        clock.Advance(TimeSpan.FromSeconds(21));

        await provider.RefreshIfDueAsync(TestContext.Current.CancellationToken);

        handler.Requests.Count.ShouldBe(2);
        list[0].Host.ShouldBe("10.0.0.2");
    }

    [Fact]
    public async Task Refresh_KeepsThePreviousListWhenTheAnswerIsUnusable()
    {
        var clock = new FakeTimeProvider();
        string body = "10.0.0.1";
        var handler = new StubHandler(_ => Body(body));
        var list = new NacosServerList([], isDynamic: true);
        using var client = new HttpClient(handler);
        await using var provider = CreateProvider(clock, list, client);

        await provider.SeedAsync(TestContext.Current.CancellationToken);

        body = "http://";
        clock.Advance(TimeSpan.FromSeconds(31));

        await provider.RefreshIfDueAsync(TestContext.Current.CancellationToken);

        // Half a cluster is worse than the list already in use, so a bad line rejects the answer.
        list.Count.ShouldBe(1);
        list[0].Host.ShouldBe("10.0.0.1");
    }

    [Fact]
    public async Task Refresh_KeepsThePreviousListWhenTheEndpointPublishesNothing()
    {
        var clock = new FakeTimeProvider();
        string body = "10.0.0.1";
        var handler = new StubHandler(_ => Body(body));
        var list = new NacosServerList([], isDynamic: true);
        using var client = new HttpClient(handler);
        await using var provider = CreateProvider(clock, list, client);

        await provider.SeedAsync(TestContext.Current.CancellationToken);

        body = string.Empty;
        clock.Advance(TimeSpan.FromSeconds(31));

        await provider.RefreshIfDueAsync(TestContext.Current.CancellationToken);

        list.Count.ShouldBe(1);
        list[0].Host.ShouldBe("10.0.0.1");
    }

    [Fact]
    public async Task Start_SeedsTheListWithoutBlockingTheCaller()
    {
        var clock = new FakeTimeProvider();
        var handler = new StubHandler(_ => Body("10.0.0.1"));
        var list = new NacosServerList([], isDynamic: true);
        using var client = new HttpClient(handler);
        await using var provider = CreateProvider(clock, list, client);

        provider.Start();

        await WaitUntilAsync(clock, () => list.Count == 1, TestContext.Current.CancellationToken);

        list[0].Host.ShouldBe("10.0.0.1");
    }

    private static NacosEndpointServerListProvider CreateProvider(
        FakeTimeProvider clock,
        NacosServerList list,
        HttpClient client) =>
        new(
            new NacosOptions
            {
                Endpoint = "address-server",
                EndpointRefreshInterval = TimeSpan.FromSeconds(5),
            },
            list,
            client,
            clock,
            NullLogger.Instance);

    private static HttpResponseMessage Body(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text) };

    /// <summary>
    /// Advances the fake clock until a task completes.
    /// </summary>
    /// <param name="clock">The clock the provider waits on.</param>
    /// <param name="task">The task to drive.</param>
    /// <param name="ct">A token that cancels the wait.</param>
    /// <returns>A task that completes once the driven task did.</returns>
    private static async Task DriveUntilAsync(FakeTimeProvider clock, Task task, CancellationToken ct)
    {
        for (int i = 0; i < 200 && !task.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(5, ct);
        }

        await task;
    }

    /// <summary>
    /// Advances the fake clock until a condition holds.
    /// </summary>
    /// <param name="clock">The clock the provider waits on.</param>
    /// <param name="condition">The condition to wait for.</param>
    /// <param name="ct">A token that cancels the wait.</param>
    /// <returns>A task that completes once the condition holds.</returns>
    private static async Task WaitUntilAsync(
        FakeTimeProvider clock,
        Func<bool> condition,
        CancellationToken ct)
    {
        for (int i = 0; i < 200; i++)
        {
            if (condition())
            {
                return;
            }

            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(5, ct);
        }

        throw new TimeoutException("The expected condition was not met within the timeout.");
    }

    /// <summary>
    /// An HTTP handler whose answer a test scripts per call.
    /// </summary>
    private sealed class StubHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _calls;

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);

            return Task.FromResult(respond(_calls++));
        }
    }
}
