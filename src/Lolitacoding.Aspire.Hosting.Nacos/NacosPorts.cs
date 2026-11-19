namespace Lolitacoding.Aspire.Hosting.Nacos;

/// <summary>
/// The ports a Nacos server listens on inside its container and the offsets between them.
/// </summary>
/// <remarks>
/// These are container-side ports, not host ports. Aspire publishes each one on a free host port
/// unless the caller pins it, so a client reads the published gRPC port instead of assuming it is
/// the published main port plus an offset. The offsets describe the container's own layout and are
/// what the image listens on.
/// </remarks>
internal static class NacosPorts
{
    /// <summary>The main HTTP port.</summary>
    internal const int Http = 8848;

    /// <summary>
    /// The port of the web console, which Nacos 3.x serves as a separate application.
    /// </summary>
    /// <remarks>
    /// In Nacos 2.x the console was part of the server and reachable under the main port's context
    /// path. From 3.0 it is its own Spring Boot application, configured by
    /// <c>nacos.console.port</c> and listening on <c>8080</c> by default.
    /// </remarks>
    internal const int Console = 8080;

    /// <summary>The offset from the main port to the client gRPC port.</summary>
    internal const int ClientGrpcOffset = 1000;

    /// <summary>The offset from the main port to the server gRPC port.</summary>
    internal const int ServerGrpcOffset = 1001;
}
