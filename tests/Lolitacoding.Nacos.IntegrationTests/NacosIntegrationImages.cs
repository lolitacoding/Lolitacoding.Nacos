namespace Lolitacoding.Nacos.IntegrationTests;

/// <summary>
/// Shared constants describing the Nacos containers the integration tests run against.
/// </summary>
/// <remarks>
/// The client targets Nacos 3.x only, so a single image is exercised. The v3 login endpoint and
/// the v3 OpenAPI paths it exposes are exactly the parts most likely to regress.
/// </remarks>
internal static class NacosIntegrationImages
{
    /// <summary>The Nacos 3.x image, which exposes the v3 OpenAPI and the v3 login endpoint.</summary>
    public const string V3Image = "nacos/nacos-server:v3.0.2";

    /// <summary>The main HTTP port inside the container.</summary>
    public const int HttpPort = 8848;

    /// <summary>The gRPC port inside the container, which is always the HTTP port plus this offset.</summary>
    public const int GrpcPort = 9848;

    /// <summary>The gRPC port offset from the main HTTP port.</summary>
    public const int GrpcPortOffset = 1000;

    /// <summary>The default user name of an auth-enabled Nacos server.</summary>
    public const string DefaultUserName = "nacos";

    /// <summary>The default password of an auth-enabled Nacos server.</summary>
    public const string DefaultPassword = "nacos";

    /// <summary>
    /// The signing key used by the auth-enabled test container.
    /// </summary>
    /// <remarks>
    /// Nacos refuses to start with authentication enabled unless this is supplied, and it must be
    /// at least 32 characters of Base64-encoded material.
    /// </remarks>
    public const string AuthToken =
        "SecretKey012345678901234567890123456789012345678901234567890123456789";
}

/// <summary>
/// Names the trait applied to every test that requires a running Docker daemon.
/// </summary>
/// <remarks>
/// CI filters these tests out of the unit-test job with <c>--filter "Category!=Integration"</c>
/// and runs them in a dedicated job that provides Docker, so the trait is the single switch that
/// separates the two jobs.
/// </remarks>
public static class NacosTraits
{
    /// <summary>The trait name identifying integration tests.</summary>
    public const string Category = "Category";

    /// <summary>The trait value identifying integration tests.</summary>
    public const string Integration = "Integration";
}
