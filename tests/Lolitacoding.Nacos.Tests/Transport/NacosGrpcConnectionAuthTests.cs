using Lolitacoding.Nacos.Auth;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Generated;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Tests.Auth;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.Tests.Transport;

/// <summary>
/// Verifies how the transport attaches the access token and recovers from its rejection.
/// </summary>
public sealed class NacosGrpcConnectionAuthTests
{
    private static readonly NacosServerAddress Server = NacosServerAddress.Parse("127.0.0.1:8848");

    [Fact]
    public async Task RequestAsync_AttachesTheTokenToTheRequestHeaders()
    {
        // Nacos reads the token from the gRPC metadata under the key "accessToken"; there is no
        // Authorization bearer header on the gRPC path.
        Payload? captured = null;
        var provider = new FakeAuthProvider("token-1");

        await using NacosGrpcConnection connection = CreateConnection(
            request =>
            {
                if (PayloadCodec.ReadTypeName(request) == "ServerCheckRequest")
                {
                    return FakeServerCheck.Responding()(request);
                }

                captured = request;
                return PayloadCodec.Encode(new ConfigQueryResponse { Content = "ok" });
            },
            provider);

        await connection.ConnectAsync(TestContext.Current.CancellationToken);
        await connection.RequestAsync<ConfigQueryResponse>(
            new ConfigQueryRequest { DataId = "a", Group = "g" },
            TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured.Metadata.Headers["accessToken"].ShouldBe("token-1");
    }

    [Fact]
    public async Task RequestAsync_SendsNoTokenHeaderWhenAuthenticationIsAnonymous()
    {
        Payload? captured = null;

        await using NacosGrpcConnection connection = CreateConnection(
            request =>
            {
                if (PayloadCodec.ReadTypeName(request) == "ServerCheckRequest")
                {
                    return FakeServerCheck.Responding()(request);
                }

                captured = request;
                return PayloadCodec.Encode(new ConfigQueryResponse { Content = "ok" });
            },
            authProvider: null);

        await connection.ConnectAsync(TestContext.Current.CancellationToken);
        await connection.RequestAsync<ConfigQueryResponse>(
            new ConfigQueryRequest { DataId = "a", Group = "g" },
            TestContext.Current.CancellationToken);

        captured!.Metadata.Headers.ShouldNotContainKey("accessToken");
    }

    [Fact]
    public async Task RequestAsync_InvalidatesTheTokenAndRetriesOnceOnAccessDenied()
    {
        // A rejected token is usually just an expired one, so a single refresh-and-retry hides
        // the expiry from the caller without masking a genuine authorization failure.
        int calls = 0;
        var provider = new FakeAuthProvider("stale-token", "fresh-token");

        await using NacosGrpcConnection connection = CreateConnection(
            request =>
            {
                if (PayloadCodec.ReadTypeName(request) == "ServerCheckRequest")
                {
                    return FakeServerCheck.Responding()(request);
                }

                calls++;

                // First attempt is refused, the retry succeeds, mimicking an expired token.
                return calls == 1
                    ? PayloadCodec.Encode(new ErrorResponse
                    {
                        ResultCode = NacosResponse.FailureCode,
                        ErrorCode = 403,
                        Message = "token expired!",
                    })
                    : PayloadCodec.Encode(new ConfigQueryResponse { Content = "ok" });
            },
            provider);

        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        ConfigQueryResponse response = await connection.RequestAsync<ConfigQueryResponse>(
            new ConfigQueryRequest { DataId = "a", Group = "g" },
            TestContext.Current.CancellationToken);

        response.Content.ShouldBe("ok");
        calls.ShouldBe(2);
        provider.InvalidateCount.ShouldBe(1);
    }

    [Fact]
    public async Task RequestAsync_RetriesOnlyOnce()
    {
        // Retrying indefinitely would turn a permanent authorization failure into an infinite
        // loop against the server.
        var provider = new FakeAuthProvider("stale-token", "still-stale");

        await using NacosGrpcConnection connection = CreateConnection(
            request =>
            {
                if (PayloadCodec.ReadTypeName(request) == "ServerCheckRequest")
                {
                    return FakeServerCheck.Responding()(request);
                }

                return PayloadCodec.Encode(new ErrorResponse
                {
                    ResultCode = NacosResponse.FailureCode,
                    ErrorCode = 403,
                    Message = "token expired!",
                });
            },
            provider);

        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        NacosServerException ex = await Should.ThrowAsync<NacosServerException>(
            () => connection.RequestAsync<ConfigQueryResponse>(
                new ConfigQueryRequest { DataId = "a", Group = "g" },
                TestContext.Current.CancellationToken));

        ex.ErrorCode.ShouldBe(403);
        provider.InvalidateCount.ShouldBe(1);
    }

    [Fact]
    public async Task RequestAsync_RetriesOnTheOpenApiAccessDeniedCodeToo()
    {
        // The OpenAPI envelope reports the same condition as 10001 rather than 403, so both
        // codes must trigger the refresh.
        int calls = 0;
        var provider = new FakeAuthProvider("stale-token", "fresh-token");

        await using NacosGrpcConnection connection = CreateConnection(
            request =>
            {
                if (PayloadCodec.ReadTypeName(request) == "ServerCheckRequest")
                {
                    return FakeServerCheck.Responding()(request);
                }

                calls++;
                return calls == 1
                    ? PayloadCodec.Encode(new ErrorResponse
                    {
                        ResultCode = NacosResponse.FailureCode,
                        ErrorCode = 10001,
                        Message = "access denied",
                    })
                    : PayloadCodec.Encode(new ConfigQueryResponse { Content = "ok" });
            },
            provider);

        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        ConfigQueryResponse response = await connection.RequestAsync<ConfigQueryResponse>(
            new ConfigQueryRequest { DataId = "a", Group = "g" },
            TestContext.Current.CancellationToken);

        response.Content.ShouldBe("ok");
        calls.ShouldBe(2);
    }

    [Fact]
    public async Task RequestAsync_DoesNotRefreshOnAnUnrelatedError()
    {
        // Only a credential rejection justifies another login; a missing resource must surface
        // immediately.
        var provider = new FakeAuthProvider("token-1");

        await using NacosGrpcConnection connection = CreateConnection(
            request =>
            {
                if (PayloadCodec.ReadTypeName(request) == "ServerCheckRequest")
                {
                    return FakeServerCheck.Responding()(request);
                }

                return PayloadCodec.Encode(new ErrorResponse
                {
                    ResultCode = NacosResponse.FailureCode,
                    ErrorCode = 20004,
                    Message = "resource not found",
                });
            },
            provider);

        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<NacosServerException>(
            () => connection.RequestAsync<ConfigQueryResponse>(
                new ConfigQueryRequest { DataId = "a", Group = "g" },
                TestContext.Current.CancellationToken));

        provider.InvalidateCount.ShouldBe(0);
    }

    private static NacosGrpcConnection CreateConnection(
        Func<Payload, Payload> responder,
        INacosAuthProvider? authProvider)
    {
        var stream = new FakeNacosDuplexStream();
        var factory = new FakeNacosChannelFactory(
            address => new FakeNacosChannel(address, responder, stream));

        var options = new NacosOptions
        {
            AppName = "test-app",
            ConnectionTimeout = TimeSpan.FromSeconds(5),
        };

        return new NacosGrpcConnection(
            options,
            [Server],
            factory,
            TimeProvider.System,
            NullLogger.Instance,
            new Random(1234),
            authProvider);
    }
}
