namespace Lolitacoding.Nacos.Transport;

/// <summary>
/// Describes the lifecycle state of a Nacos gRPC connection.
/// </summary>
internal enum NacosTransportState
{
    /// <summary>No connection has been established yet.</summary>
    Disconnected,

    /// <summary>A connection attempt or handshake is in progress.</summary>
    Connecting,

    /// <summary>The connection is established and the handshake completed.</summary>
    Connected,

    /// <summary>
    /// The connection is established but has failed a health check and is about to be replaced.
    /// </summary>
    Unhealthy,

    /// <summary>The connection has been closed deliberately and will not reconnect.</summary>
    Shutdown,
}

/// <summary>
/// Carries the previous and current connection state when it changes.
/// </summary>
internal sealed class NacosTransportStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NacosTransportStateChangedEventArgs"/> class.
    /// </summary>
    /// <param name="previous">The state being left.</param>
    /// <param name="current">The state being entered.</param>
    /// <param name="serverAddress">The server the change concerns, when known.</param>
    public NacosTransportStateChangedEventArgs(
        NacosTransportState previous,
        NacosTransportState current,
        string? serverAddress = null)
    {
        Previous = previous;
        Current = current;
        ServerAddress = serverAddress;
    }

    /// <summary>Gets the state being left.</summary>
    public NacosTransportState Previous { get; }

    /// <summary>Gets the state being entered.</summary>
    public NacosTransportState Current { get; }

    /// <summary>Gets the server the change concerns, when known.</summary>
    public string? ServerAddress { get; }
}
