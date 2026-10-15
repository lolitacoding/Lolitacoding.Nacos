namespace Lolitacoding.Aspire.Hosting.Nacos;

/// <summary>
/// The container image coordinates used by the Nacos hosting integration.
/// </summary>
/// <remarks>
/// Override any of these with the standard Aspire container methods, for example
/// <c>WithImageTag("v3.0.2")</c>, when a specific server version is required.
/// </remarks>
public static class NacosContainerImageTags
{
    /// <summary>The registry that hosts the Nacos server image.</summary>
    public const string Registry = "docker.io";

    /// <summary>The Nacos server image name, without a registry or tag.</summary>
    public const string Image = "nacos/nacos-server";

    /// <summary>The default image tag.</summary>
    public const string Tag = "latest";
}
