namespace Lolitacoding.Nacos.IntegrationTests;

/// <summary>
/// Shared constants describing the Nacos containers the integration tests run against.
/// </summary>
/// <remarks>
/// Three images are exercised, because the modules did not all arrive at once: the lock service the
/// client speaks to belongs to the 3.3 line, the lock module a 3.2 server ships is an earlier
/// contract that can neither renew a lease nor queue a waiter, and the AI registry is younger than
/// the oldest server the rest of the suite runs against. Which image a test runs against is
/// therefore part of what it is testing.
/// </remarks>
internal static class NacosIntegrationImages
{
    /// <summary>
    /// The Nacos 3.x image the suite runs against: <c>NACOS_TEST_IMAGE</c> when it is set, and the
    /// pinned release below otherwise.
    /// </summary>
    /// <remarks>
    /// The environment variable is what a CI run uses to move the suite — including the container it
    /// warms the image cache with — to another server version without editing the tests, which is why
    /// this is a field rather than a constant.
    /// </remarks>
    public static readonly string V3Image =
        Environment.GetEnvironmentVariable("NACOS_TEST_IMAGE") is { Length: > 0 } image
            ? image
            : PinnedV3Image;

    /// <summary>
    /// The image the lock tests run against: <c>NACOS_LOCK_TEST_IMAGE</c> when it is set, and the
    /// pinned release below otherwise.
    /// </summary>
    /// <remarks>
    /// It is a separate image because the lock service the client implements is the 3.3 one and no
    /// 3.2 release has it: a 3.2 lock module knows only <c>ACQUIRE</c> and <c>RELEASE</c>, so it can
    /// neither renew a lease nor queue a waiter, and it registers <c>NACOS_LOCK</c> as its only lock
    /// type.
    /// </remarks>
    public static readonly string LockImage =
        Environment.GetEnvironmentVariable("NACOS_LOCK_TEST_IMAGE") is { Length: > 0 } image
            ? image
            : PinnedLockImage;

    /// <summary>
    /// The image the AI registry tests run against: <c>NACOS_AI_TEST_IMAGE</c> when it is set, and
    /// the pinned release below otherwise.
    /// </summary>
    /// <remarks>
    /// It is a separate image because the AI registry is younger than the server the rest of the
    /// suite is pinned to: the client contract for MCP servers was added in 3.0.3
    /// (<c>McpService</c> marks its operations <c>@Since("3.0.3")</c>), and a 3.0.2 server answers a
    /// query and a release with <c>RequestHandler Not Found</c>. The pin is the oldest release the
    /// suite is checked against that serves the registry; the newer releases are checked through
    /// this same variable.
    /// </remarks>
    public static readonly string AiImage =
        Environment.GetEnvironmentVariable("NACOS_AI_TEST_IMAGE") is { Length: > 0 } image
            ? image
            : PinnedAiImage;

    /// <summary>
    /// The pinned release the rest of the suite is checked against.
    /// </summary>
    /// <remarks>
    /// The oldest supported 3.x release, so that what the suite proves is the oldest contract the
    /// client claims to speak. A run picks another server through <see cref="V3Image"/> without a
    /// code change.
    /// </remarks>
    private const string PinnedV3Image = "nacos/nacos-server:v3.0.2";

    /// <summary>
    /// The pinned release the AI registry tests are checked against.
    /// </summary>
    /// <remarks>
    /// The oldest release the AI registry has been verified against. Move this down to 3.0.3 once a
    /// run has confirmed that release serves the whole contract, or up when a newer feature — the
    /// agent registry in particular — needs a newer server.
    /// </remarks>
    private const string PinnedAiImage = "nacos/nacos-server:v3.2.4";

    /// <summary>
    /// The pinned release the lock tests are checked against.
    /// </summary>
    /// <remarks>
    /// The lock service belongs to the 3.3 line, of which a release candidate is what is published so
    /// far. Move this to the final release once there is one.
    /// </remarks>
    private const string PinnedLockImage = "nacos/nacos-server:v3.3.0-RC";

    /// <summary>The main HTTP port inside the container.</summary>
    public const int HttpPort = 8848;

    /// <summary>The gRPC port inside the container, which is always the HTTP port plus this offset.</summary>
    public const int GrpcPort = 9848;

    /// <summary>The gRPC port offset from the main HTTP port.</summary>
    public const int GrpcPortOffset = 1000;

    /// <summary>The user name of the administrator the test containers are set up with.</summary>
    public const string DefaultUserName = "nacos";

    /// <summary>The password the test containers create the administrator with.</summary>
    public const string DefaultPassword = "nacos";

    /// <summary>
    /// The endpoint that creates the first administrator of a server.
    /// </summary>
    /// <remarks>
    /// A Nacos 3.x server ships no accounts at all, so an auth-enabled one rejects every login until
    /// this has been called once; the container harness calls it for that reason. The endpoint refuses
    /// once a global administrator exists, which is the answer a restarted container gives.
    /// </remarks>
    public const string InitialAdminPath = "/nacos/v3/auth/user/admin";

    /// <summary>
    /// The signing key used by every test container.
    /// </summary>
    /// <remarks>
    /// The development key the repository's compose file and CI also start a server with. Nacos
    /// refuses to start without one from 3.2 on, even when the authentication plugin is disabled, and
    /// the server decodes it from Base64 and requires at least 32 bytes out of it.
    /// </remarks>
    public const string AuthToken =
        "SecretKey012345678901234567890123456789012345678901234567890123456789";

    /// <summary>The server identity key the container image requires.</summary>
    public const string AuthIdentityKey = "serverIdentity";

    /// <summary>The server identity value the container image requires.</summary>
    public const string AuthIdentityValue = "security";
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
