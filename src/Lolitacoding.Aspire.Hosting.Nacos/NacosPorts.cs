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

    /// <summary>The offset from the main port to the client gRPC port.</summary>
    internal const int ClientGrpcOffset = 1000;

    /// <summary>The offset from the main port to the server gRPC port.</summary>
    internal const int ServerGrpcOffset = 1001;
}
