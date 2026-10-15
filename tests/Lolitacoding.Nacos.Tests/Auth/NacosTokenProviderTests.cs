using Lolitacoding.Nacos.Auth;
using Lolitacoding.Nacos.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Lolitacoding.Nacos.Tests.Auth;

/// <summary>
/// A scripted <see cref="INacosLoginClient"/> that records calls and returns canned responses.
/// </summary>
internal sealed class FakeLoginClient : INacosLoginClient
{
    private readonly Func<int, NacosServerAddress, NacosLoginResponse?> _responder;

    /// <summary>
    /// Initializes a new instance of the <see cref="FakeLoginClient"/> class.
    /// </summary>
    /// <param name="responder">
    /// Produces the response for an attempt; the first argument is the zero-based call index.
    /// Throw from it to simulate an unreachable server.
    /// </param>
    public FakeLoginClient(Func<int, NacosServerAddress, NacosLoginResponse?> responder) =>
        _responder = responder;

    /// <summary>Gets the servers that were asked for a token, in order.</summary>
    public List<NacosServerAddress> RequestedServers { get; } = [];

    /// <summary>Gets the user names that were presented, in order.</summary>
    public List<string> RequestedUserNames { get; } = [];

    /// <summary>Gets the passwords that were presented, in order.</summary>
    public List<string> RequestedPasswords { get; } = [];

    public ValueTask<NacosLoginResponse?> LoginAsync(
        NacosServerAddress server,
        string userName,
        string password,
        CancellationToken ct = default)
    {
        int index = RequestedServers.Count;
        RequestedServers.Add(server);
        RequestedUserNames.Add(userName);
        RequestedPasswords.Add(password);

        return ValueTask.FromResult(_responder(index, server));
    }
}

/// <summary>
/// Verifies the login, caching and refresh behaviour of <see cref="NacosTokenProvider"/>.
/// </summary>
/// <remarks>
/// Every timing assertion is driven by a fake clock, which is what makes the refresh window
/// verifiable without waiting for a real token to expire.
/// </remarks>
public sealed class NacosTokenProviderTests
{
    private static readonly NacosServerAddress Server = NacosServerAddress.Parse("127.0.0.1:8848");
    private static readonly NacosServerAddress SecondServer = NacosServerAddress.Parse("127.0.0.1:8849");

    [Fact]
    public async Task GetAccessToken_LogsInOnceAndCachesTheToken()
    {
        var client = new FakeLoginClient((_, _) => Token("token-1", ttlSeconds: 18000));
        NacosTokenProvider provider = CreateProvider(client);

        string? first = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);
        string? second = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        first.ShouldBe("token-1");
        second.ShouldBe("token-1");

        // The second call must be served from cache, not by a second login.
        client.RequestedServers.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetAccessToken_LogsInAgainAfterTheRefreshWindow()
    {
        var timeProvider = new FakeTimeProvider();
        var client = new FakeLoginClient((index, _) => Token($"token-{index + 1}", ttlSeconds: 18000));
        NacosTokenProvider provider = CreateProvider(client, timeProvider);

        await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        // The token is used for ttl - refreshWindow, where the window is in [ttl/15, ttl/10].
        // Advancing past the whole TTL therefore always forces a refresh.
        timeProvider.Advance(TimeSpan.FromSeconds(18000));

        string? refreshed = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        refreshed.ShouldBe("token-2");
        client.RequestedServers.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetAccessToken_RefreshesBeforeTheServerSideExpiry()
    {
        // The whole point of the refresh window is that the client renews while the token is
        // still valid, so it never presents an expired token. The window is drawn at random from
        // [ttl/15, ttl/10], so the earliest possible refresh point bounds what can be asserted:
        // before ttl - ttl/10 the cached token is always still in use.
        var timeProvider = new FakeTimeProvider();
        var client = new FakeLoginClient((index, _) => Token($"token-{index + 1}", ttlSeconds: 18000));
        NacosTokenProvider provider = CreateProvider(client, timeProvider);

        await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        // Well before the window opens, the token is served from cache without any login.
        timeProvider.Advance(TimeSpan.FromSeconds(18000 - 1800 - 1));
        string? stillValid = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);
        stillValid.ShouldBe("token-1");
        client.RequestedServers.Count.ShouldBe(1);

        // Once the entire window has passed, a refresh is guaranteed regardless of the draw.
        timeProvider.Advance(TimeSpan.FromSeconds(1801));
        string? refreshed = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);
        refreshed.ShouldBe("token-2");
    }

    [Fact]
    public async Task GetAccessToken_ThrowsWhenTheServerRejectsTheCredentials()
    {
        // A null response is how the login client reports HTTP 403, which is what the Nacos auth
        // plugin returns for a wrong password.
        var client = new FakeLoginClient((_, _) => null);
        NacosTokenProvider provider = CreateProvider(client);

        NacosAuthException ex = await Should.ThrowAsync<NacosAuthException>(
            () => provider.GetAccessTokenAsync(TestContext.Current.CancellationToken).AsTask());

        ex.Message.ShouldContain("rejected");
    }

    [Fact]
    public async Task GetAccessToken_DoesNotTryOtherServersAfterARejection()
    {
        // Every server shares the same user database, so a rejection is definitive rather than
        // something to retry elsewhere.
        var client = new FakeLoginClient((_, _) => null);
        NacosTokenProvider provider = CreateProvider(client, servers: [Server, SecondServer]);

        await Should.ThrowAsync<NacosAuthException>(
            () => provider.GetAccessTokenAsync(TestContext.Current.CancellationToken).AsTask());

        client.RequestedServers.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetAccessToken_FailsOverToTheNextServerWhenTheFirstIsUnreachable()
    {
        var client = new FakeLoginClient((index, _) => index == 0
            ? throw new NacosConnectionException("refused")
            : Token("token-2", ttlSeconds: 18000));

        NacosTokenProvider provider = CreateProvider(client, servers: [Server, SecondServer]);

        string? token = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        token.ShouldBe("token-2");
        client.RequestedServers.Count.ShouldBe(2);
        client.RequestedServers[0].Original.ShouldBe(Server.Original);
        client.RequestedServers[1].Original.ShouldBe(SecondServer.Original);
    }

    [Fact]
    public async Task GetAccessToken_ThrowsWhenNoServerCanBeReached()
    {
        var client = new FakeLoginClient((_, _) => throw new NacosConnectionException("refused"));
        NacosTokenProvider provider = CreateProvider(client, servers: [Server, SecondServer]);

        await Should.ThrowAsync<NacosAuthException>(
            () => provider.GetAccessTokenAsync(TestContext.Current.CancellationToken).AsTask());

        client.RequestedServers.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetAccessToken_ThrowsWhenTheTokenLifetimeIsMissing()
    {
        // Assuming a lifetime here would risk caching a token that is already expired, so the
        // response is treated as a protocol violation.
        var client = new FakeLoginClient((_, _) => new NacosLoginResponse
        {
            AccessToken = "token-1",
            TokenTtl = 0,
        });

        NacosTokenProvider provider = CreateProvider(client);

        NacosAuthException ex = await Should.ThrowAsync<NacosAuthException>(
            () => provider.GetAccessTokenAsync(TestContext.Current.CancellationToken).AsTask());

        ex.Message.ShouldContain("tokenTtl");
    }

    [Fact]
    public async Task GetAccessToken_ThrowsWhenTheResponseCarriesNoToken()
    {
        var client = new FakeLoginClient((_, _) => new NacosLoginResponse { TokenTtl = 18000 });
        NacosTokenProvider provider = CreateProvider(client);

        await Should.ThrowAsync<NacosAuthException>(
            () => provider.GetAccessTokenAsync(TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Invalidate_ForcesAFreshLoginOnceTheReLoginWindowHasPassed()
    {
        var timeProvider = new FakeTimeProvider();
        var client = new FakeLoginClient((index, _) => Token($"token-{index + 1}", ttlSeconds: 18000));
        NacosTokenProvider provider = CreateProvider(client, timeProvider);

        await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        // Past the re-login window, invalidating discards the cached expiry so the next call must
        // authenticate again even though the token would otherwise still be valid for hours.
        timeProvider.Advance(TimeSpan.FromSeconds(61));
        provider.Invalidate();

        string? afterInvalidate = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        afterInvalidate.ShouldBe("token-2");
        client.RequestedServers.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Invalidate_IsSuppressedWithinTheReLoginWindow()
    {
        // Without the 60 second guard, a server that rejects a token it just issued would put
        // the client into a tight login loop.
        var timeProvider = new FakeTimeProvider();
        var client = new FakeLoginClient((index, _) => Token($"token-{index + 1}", ttlSeconds: 18000));
        NacosTokenProvider provider = CreateProvider(client, timeProvider);

        await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);
        provider.Invalidate();

        // The retry path calls Invalidate then GetAccessToken; within the window the provider
        // declines to log in again and re-presents the existing token rather than going anonymous.
        string? throttled = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);
        throttled.ShouldBe("token-1");
        client.RequestedServers.Count.ShouldBe(1);

        // After the window elapses a fresh login is allowed again.
        timeProvider.Advance(TimeSpan.FromSeconds(61));
        provider.Invalidate();
        string? allowed = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);
        allowed.ShouldBe("token-2");
        client.RequestedServers.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetAccessToken_UsesTheConfiguredCredentials()
    {
        var client = new FakeLoginClient((_, _) => Token("token-1", ttlSeconds: 18000));
        NacosTokenProvider provider = CreateProvider(client);

        await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        client.RequestedUserNames.ShouldBe(["nacos"]);
        client.RequestedPasswords.ShouldBe(["secret"]);
    }

    [Fact]
    public async Task GetAccessToken_ConcurrentCallersShareOneLogin()
    {
        // Every request asks for a token, so an unguarded cache would let a burst of concurrent
        // requests each start its own login.
        var gate = new TaskCompletionSource();
        var client = new FakeLoginClient((_, _) => Token("token-1", ttlSeconds: 18000));
        var slowClient = new SlowLoginClient(gate.Task, client);
        NacosTokenProvider provider = CreateProvider(slowClient);

        Task<string?>[] callers = [.. Enumerable.Range(0, 8)
            .Select(_ => provider.GetAccessTokenAsync(TestContext.Current.CancellationToken).AsTask())];

        gate.SetResult();
        string?[] tokens = await Task.WhenAll(callers);

        tokens.ShouldAllBe(t => t == "token-1");
        client.RequestedServers.Count.ShouldBe(1);
    }

    [Fact]
    public void GenerateRefreshWindow_StaysWithinTheConfiguredFraction()
    {
        // The refresh window must land in [ttl/15, ttl/10]; a value outside that range would
        // either refresh wastefully early or risk presenting an expired token.
        for (int seed = 0; seed < 50; seed++)
        {
            var provider = CreateProvider(
                new FakeLoginClient((_, _) => Token("t", ttlSeconds: 18000)),
                random: new Random(seed));

            TimeSpan window = provider.GenerateRefreshWindowForTest(TimeSpan.FromSeconds(18000));

            window.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1200));
            window.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(1800));
        }
    }

    private static NacosLoginResponse Token(string token, long ttlSeconds) => new()
    {
        AccessToken = token,
        TokenTtl = ttlSeconds,
        UserName = "nacos",
    };

    private static NacosTokenProvider CreateProvider(
        INacosLoginClient client,
        TimeProvider? timeProvider = null,
        IReadOnlyList<NacosServerAddress>? servers = null,
        Random? random = null)
    {
        var options = new NacosOptions
        {
            UserName = "nacos",
            Password = "secret",
            ServerAddresses = [Server.Original],
        };

        return new NacosTokenProvider(
            options,
            servers ?? [Server],
            client,
            timeProvider ?? new FakeTimeProvider(),
            NullLogger.Instance,
            random ?? new Random(1234));
    }

    /// <summary>
    /// A login client that blocks until a gate opens, so concurrent callers are forced to overlap.
    /// </summary>
    private sealed class SlowLoginClient : INacosLoginClient
    {
        private readonly Task _gate;
        private readonly INacosLoginClient _inner;

        public SlowLoginClient(Task gate, INacosLoginClient inner)
        {
            _gate = gate;
            _inner = inner;
        }

        public async ValueTask<NacosLoginResponse?> LoginAsync(
            NacosServerAddress server,
            string userName,
            string password,
            CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            return await _inner.LoginAsync(server, userName, password, ct).ConfigureAwait(false);
        }
    }
}
