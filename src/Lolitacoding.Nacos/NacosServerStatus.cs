namespace Lolitacoding.Nacos;

/// <summary>
/// Reports whether a client currently has a usable connection to a Nacos server.
/// </summary>
/// <remarks>
/// This is the .NET counterpart of the <c>UP</c>/<c>DOWN</c> strings the official Java client's
/// <c>ConfigService.getServerStatus()</c> and <c>NamingService.getServerStatus()</c> return. The
/// answer is read from the transport's own state rather than by issuing a request, so it can be
/// polled from a health check without adding load.
/// </remarks>
public enum NacosServerStatus
{
    /// <summary>No server is connected, so a request would have to reconnect first.</summary>
    Down = 0,

    /// <summary>
    /// A server is connected and the handshake completed, so a request can be sent without
    /// reconnecting.
    /// </summary>
    Up = 1,
}
