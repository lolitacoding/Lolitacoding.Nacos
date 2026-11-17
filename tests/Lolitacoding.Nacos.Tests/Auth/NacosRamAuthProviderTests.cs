using Lolitacoding.Nacos.Auth;
using Lolitacoding.Nacos.Protocol.Messages;
using Microsoft.Extensions.Time.Testing;

namespace Lolitacoding.Nacos.Tests.Auth;

/// <summary>
/// Verifies the Alibaba Cloud RAM request signing the client performs.
/// </summary>
/// <remarks>
/// <para>
/// The signatures are pinned to fixed vectors rather than recomputed from the implementation,
/// because the value a compatible server checks is the exact base64 string the official client
/// produces. The clock is frozen at 2024-01-01T00:00:00Z so the timestamp, the v4 date and the
/// derived key are all stable.
/// </para>
/// <para>
/// The header names differ by module and are part of the contract: configuration and AI requests
/// use <c>Spas-AccessKey</c>/<c>Timestamp</c>/<c>Spas-Signature</c>, naming uses
/// <c>ak</c>/<c>data</c>/<c>signature</c>, and a lock request carries only <c>ak</c>.
/// </para>
/// </remarks>
public sealed class NacosRamAuthProviderTests
{
    private const string AccessKey = "test-ak";
    private const string SecretKey = "test-sk";
    private const string Region = "cn-hangzhou";
    private const string Timestamp = "1704067200000";

    private static readonly DateTimeOffset FrozenNow =
        new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ConfigRequest_IsSignedOverTheNamespaceAndGroup()
    {
        NacosRamAuthProvider provider = CreateProvider();
        var request = new ConfigQueryRequest
        {
            DataId = "a",
            Group = "g",
            Tenant = "ns",
        };

        provider.ApplyRequestMetadata(request);

        request.GetHeader("Spas-AccessKey").ShouldBe(AccessKey);
        request.GetHeader("Timestamp").ShouldBe(Timestamp);
        request.GetHeader("Spas-Signature").ShouldBe("qGHXLQDNNROOjL76InJK2k4upOo=");
        request.GetHeader("signatureVersion").ShouldBeNull();
    }

    [Fact]
    public void ConfigRequest_SignsOnlyTheGroupWhenThereIsNoNamespace()
    {
        NacosRamAuthProvider provider = CreateProvider();
        var request = new ConfigQueryRequest { DataId = "a", Group = "g" };

        provider.ApplyRequestMetadata(request);

        request.GetHeader("Spas-Signature").ShouldBe("tuPzN4acqNG5+c73hu5v4Qeai0k=");
    }

    [Fact]
    public void ConfigRequest_SignsTheTimestampAloneWhenThereIsNoResource()
    {
        // The official client signs the bare timestamp when neither tenant nor group is present;
        // signing an empty resource with a separator would produce a different value.
        NacosRamAuthProvider provider = CreateProvider();
        var request = new ConfigQueryRequest { DataId = "a" };

        provider.ApplyRequestMetadata(request);

        request.GetHeader("Spas-Signature").ShouldBe("SU2prVwSaHW4E+LW7V/Mqj1+7IY=");
    }

    [Fact]
    public void NamingRequest_IsSignedOverTheGroupedServiceName()
    {
        NacosRamAuthProvider provider = CreateProvider();
        var request = new ServiceQueryRequest { ServiceName = "svc", GroupName = "g" };

        provider.ApplyRequestMetadata(request);

        request.GetHeader("ak").ShouldBe(AccessKey);
        request.GetHeader("data").ShouldBe(Timestamp + "@@g@@svc");
        request.GetHeader("signature").ShouldBe("CmGornb/O+Ss8wWopQKCZwm4ogo=");
    }

    [Fact]
    public void NamingRequest_DoesNotQualifyAnAlreadyGroupedServiceName()
    {
        // A service name that already carries "@@" is the qualified form; qualifying it again
        // would address a service that does not exist.
        NacosRamAuthProvider provider = CreateProvider();
        var request = new ServiceQueryRequest { ServiceName = "g@@svc", GroupName = "other" };

        provider.ApplyRequestMetadata(request);

        request.GetHeader("data").ShouldBe(Timestamp + "@@g@@svc");
    }

    [Fact]
    public void NamingRequest_SignsTheTimestampAloneWhenThereIsNoService()
    {
        NacosRamAuthProvider provider = CreateProvider();
        var request = new ServiceQueryRequest();

        provider.ApplyRequestMetadata(request);

        request.GetHeader("data").ShouldBe(Timestamp);
        request.GetHeader("signature").ShouldBe("SU2prVwSaHW4E+LW7V/Mqj1+7IY=");
    }

    [Fact]
    public void LockRequest_CarriesOnlyTheAccessKey()
    {
        // The official client has no signed lock format: the server reads the key and ignores
        // anything else, so adding a signature would be inventing a contract.
        NacosRamAuthProvider provider = CreateProvider();
        var request = new LockOperationRequest();

        provider.ApplyRequestMetadata(request);

        request.GetHeader("ak").ShouldBe(AccessKey);
        request.GetHeader("signature").ShouldBeNull();
        request.GetHeader("data").ShouldBeNull();
        request.GetHeader("Spas-AccessKey").ShouldBeNull();
    }

    [Fact]
    public void InternalRequest_IsNotSigned()
    {
        // The handshake and health-check requests address no resource, and the server does not
        // authenticate them.
        NacosRamAuthProvider provider = CreateProvider();
        var request = new ServerCheckRequest();

        provider.ApplyRequestMetadata(request);

        request.Headers.ShouldBeEmpty();
    }

    [Fact]
    public void AiRequest_IsSignedOverItsOwnNamespaceAndTheDefaultGroup()
    {
        NacosRamAuthProvider provider = CreateProvider();
        var request = new QueryMcpServerRequest { NamespaceId = "public", McpName = "m" };

        provider.ApplyRequestMetadata(request);

        request.GetHeader("Spas-Signature").ShouldBe("OsKNMak340x8R/bXtC9ATFj4F4U=");
    }

    [Fact]
    public void AiRequest_FallsBackToTheClientsNamespace()
    {
        NacosRamAuthProvider provider = CreateProvider(defaultNamespace: "public");
        var request = new QueryMcpServerRequest { McpName = "m" };

        provider.ApplyRequestMetadata(request);

        request.GetHeader("Spas-Signature").ShouldBe("OsKNMak340x8R/bXtC9ATFj4F4U=");
    }

    [Fact]
    public void V4ConfigRequest_UsesTheDerivedKeyAndAnnouncesTheVersion()
    {
        NacosRamAuthProvider provider = CreateProvider(region: Region);
        var request = new ConfigQueryRequest { DataId = "a", Group = "g", Tenant = "ns" };

        provider.ApplyRequestMetadata(request);

        request.GetHeader("signatureVersion").ShouldBe("v4");
        request.GetHeader("Spas-Signature").ShouldBe("MLkBYzfRb3VN14NK5hRybDircSE=");
    }

    [Fact]
    public void V4NamingRequest_UsesTheDerivedKey()
    {
        NacosRamAuthProvider provider = CreateProvider(region: Region);
        var request = new ServiceQueryRequest { ServiceName = "svc", GroupName = "g" };

        provider.ApplyRequestMetadata(request);

        request.GetHeader("signatureVersion").ShouldBe("v4");
        request.GetHeader("signature").ShouldBe("Jlfz+k2WMrhYJVE45VSTqFjKdtM=");
    }

    [Fact]
    public void V4AiRequest_UsesTheDerivedKey()
    {
        NacosRamAuthProvider provider = CreateProvider(region: Region);
        var request = new QueryMcpServerRequest { NamespaceId = "public", McpName = "m" };

        provider.ApplyRequestMetadata(request);

        request.GetHeader("signatureVersion").ShouldBe("v4");
        request.GetHeader("Spas-Signature").ShouldBe("o7QBGrweq3bjD16OnOoodHdQToI=");
    }

    [Fact]
    public async Task GetAccessTokenAsync_ReturnsNoToken()
    {
        // RAM authenticates with headers, not with an access token, so the transport must not add
        // one on top of the signature.
        NacosRamAuthProvider provider = CreateProvider();

        string? token = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        token.ShouldBeNull();
    }

    [Fact]
    public void InvalidateAndDisposeAreSafeToCall()
    {
        // Both are called unconditionally by the transport and its disposal path. Signing has no
        // cached state, so a rejection needs nothing discarded.
        NacosRamAuthProvider provider = CreateProvider();

        Should.NotThrow(() =>
        {
            provider.Invalidate();
            provider.Dispose();
        });
    }

    [Fact]
    public void SigningTwice_ReplacesTheHeadersRatherThanDuplicatingThem()
    {
        // A request that is retried is signed again; header lookup is case-insensitive, so a
        // duplicate would surface as an ambiguous metadata entry.
        NacosRamAuthProvider provider = CreateProvider();
        var request = new ConfigQueryRequest { DataId = "a", Group = "g", Tenant = "ns" };

        provider.ApplyRequestMetadata(request);
        provider.ApplyRequestMetadata(request);

        request.Headers.Count.ShouldBe(3);
        request.GetHeader("Spas-Signature").ShouldBe("qGHXLQDNNROOjL76InJK2k4upOo=");
    }

    private static NacosRamAuthProvider CreateProvider(
        string? region = null,
        string defaultNamespace = "public") =>
        new(
            new NacosRamOptions
            {
                AccessKey = AccessKey,
                SecretKey = SecretKey,
                SignatureRegionId = region,
            },
            new FakeTimeProvider(FrozenNow),
            defaultNamespace);
}
