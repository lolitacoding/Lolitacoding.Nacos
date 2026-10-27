namespace Lolitacoding.Aspire.Hosting.Nacos;

/// <summary>
/// The well-known Nacos server ports and the offsets between them.
/// </summary>
/// <remarks>
/// A Nacos client derives the gRPC port from the main port it was given, so the two must stay a
/// fixed distance apart for a published container to be reachable.
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
