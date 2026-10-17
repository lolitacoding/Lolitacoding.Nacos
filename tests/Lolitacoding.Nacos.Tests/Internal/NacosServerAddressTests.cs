using Lolitacoding.Nacos.Internal;

namespace Lolitacoding.Nacos.Tests.Internal;

/// <summary>
/// Verifies the derivation of the gRPC endpoint from a configured Nacos server address.
/// </summary>
/// <remarks>
/// The gRPC endpoint is always the main port plus the fixed offset, for plaintext and TLS alike.
/// Nacos negotiates both protocols on the same SDK gRPC port, so an <c>https</c> address must not
/// drift to the cluster port one above it.
/// </remarks>
public sealed class NacosServerAddressTests
{
    [Fact]
    public void Parse_DerivesTheGrpcPortByAddingTheOffsetToTheMainPort()
    {
        NacosServerAddress address = NacosServerAddress.Parse("http://127.0.0.1:8848");

        address.UseTls.ShouldBeFalse();
        address.Port.ShouldBe(8848);
        address.GrpcBaseAddress.Scheme.ShouldBe("http");
        address.GrpcBaseAddress.Port.ShouldBe(9848);
    }

    [Fact]
    public void Parse_DerivesTlsGrpcOnTheSamePortAsPlaintext()
    {
        // Regression: the gRPC TLS endpoint is main + 1000 (9848), not main + 1001 (9849). The
        // latter is the cluster-to-cluster port, which an SDK client never dials.
        NacosServerAddress address = NacosServerAddress.Parse("https://nacos.example.com:8848");

        address.UseTls.ShouldBeTrue();
        address.GrpcBaseAddress.Scheme.ShouldBe("https");
        address.GrpcBaseAddress.Port.ShouldBe(9848);
        address.ToGrpcAddress().ShouldBe("https://nacos.example.com:9848");
    }

    [Fact]
    public void Parse_HonoursANonDefaultMainPort()
    {
        NacosServerAddress address = NacosServerAddress.Parse("http://10.0.0.5:8858");

        address.GrpcBaseAddress.Port.ShouldBe(9858);
    }

    [Fact]
    public void Parse_DefaultsTheSchemeAndPort()
    {
        NacosServerAddress address = NacosServerAddress.Parse("nacos.example.com");

        address.UseTls.ShouldBeFalse();
        address.Port.ShouldBe(NacosConstants.DefaultPort);
        address.GrpcBaseAddress.Port.ShouldBe(NacosConstants.DefaultPort + NacosConstants.GrpcPortOffset);
    }

    [Fact]
    public void Parse_KeepsTheContextPathOnTheHttpBaseOnly()
    {
        NacosServerAddress address = NacosServerAddress.Parse("https://nacos.example.com:8848/custom");

        address.ContextPath.ShouldBe("/custom");
        address.BuildHttpUri("/v3/auth/user/login").AbsoluteUri
            .ShouldBe("https://nacos.example.com:8848/custom/v3/auth/user/login");

        // The gRPC endpoint lives at the root of the derived port and carries no context path.
        address.GrpcBaseAddress.AbsoluteUri.ShouldBe("https://nacos.example.com:9848/");
    }

    [Fact]
    public void Parse_RejectsAnUnsupportedScheme()
    {
        Should.Throw<FormatException>(() => NacosServerAddress.Parse("ftp://nacos.example.com:8848"));
    }

    [Fact]
    public void BuildHttpUri_KeepsAnEmptyContextPathWithoutADoubleSlash()
    {
        // A server served at the root has no context path, which is a legal configuration: the
        // assembled URI must not gain a second slash from the base address' own trailing one.
        NacosServerAddress address = NacosServerAddress.Parse(
            "https://nacos.example.com:8848", defaultContextPath: string.Empty);

        address.ContextPath.ShouldBeEmpty();

        address.BuildHttpUri("/v3/client/ai/skills").AbsoluteUri
            .ShouldBe("https://nacos.example.com:8848/v3/client/ai/skills");
    }

    [Fact]
    public void ParseAll_AppliesTheGivenDefaultContextPath()
    {
        IReadOnlyList<NacosServerAddress> addresses =
            NacosServerAddress.ParseAll(["nacos.example.com:8848"], "/custom");

        addresses[0].BuildHttpUri("/v3/client/ai/skills").AbsoluteUri
            .ShouldBe("http://nacos.example.com:8848/custom/v3/client/ai/skills");
    }
}
