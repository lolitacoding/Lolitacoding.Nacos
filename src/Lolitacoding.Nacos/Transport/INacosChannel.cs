using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol.Generated;

namespace Lolitacoding.Nacos.Transport;

/// <summary>
/// A connection to a single Nacos server, expressed in terms of the two RPC shapes Nacos uses.
/// </summary>
/// <remarks>
/// <para>
/// This abstraction exists so the transport logic can be exercised without a network. It exposes
/// exactly what Nacos needs — one unary call and one bidirectional stream — and deliberately
/// exposes no gRPC types beyond the protobuf envelope, so a substitute is trivial to write.
/// </para>
/// <para>
/// The real implementation is <see cref="GrpcNacosChannel"/>.
/// </para>
/// </remarks>
internal interface INacosChannel : IDisposable
{
    /// <summary>
    /// Invokes the unary <c>Request/request</c> RPC.
    /// </summary>
    /// <param name="request">The request envelope.</param>
    /// <param name="ct">A token that cancels the call.</param>
    /// <returns>The response envelope.</returns>
    /// <exception cref="NacosConnectionException">The RPC failed at the transport level.</exception>
    Task<Payload> UnaryAsync(Payload request, CancellationToken ct);

    /// <summary>
    /// Opens the bidirectional <c>BiRequestStream/requestBiStream</c> stream.
    /// </summary>
    /// <param name="ct">A token that cancels the call.</param>
    /// <returns>The stream, which the caller owns and must dispose.</returns>
    /// <exception cref="NacosConnectionException">The stream could not be opened.</exception>
    INacosDuplexStream OpenStream(CancellationToken ct);
}

/// <summary>
/// The client half of the Nacos notification stream.
/// </summary>
internal interface INacosDuplexStream : IDisposable
{
    /// <summary>
    /// Writes one frame to the stream.
    /// </summary>
    /// <param name="payload">The envelope to send.</param>
    /// <param name="ct">A token that cancels the write.</param>
    /// <returns>A task that completes when the frame is buffered.</returns>
    Task WriteAsync(Payload payload, CancellationToken ct);

    /// <summary>
    /// Reads the next frame from the stream.
    /// </summary>
    /// <param name="ct">A token that cancels the read.</param>
    /// <returns>
    /// The next envelope, or <see langword="null"/> when the server closed the stream cleanly.
    /// </returns>
    /// <exception cref="NacosConnectionException">The stream faulted.</exception>
    Task<Payload?> ReadAsync(CancellationToken ct);
}

/// <summary>
/// Creates a <see cref="INacosChannel"/> for a specific server.
/// </summary>
/// <remarks>
/// Separating channel creation from channel use lets the transport be driven by an in-memory
/// substitute in unit tests while production uses a real gRPC channel.
/// </remarks>
internal interface INacosChannelFactory
{
    /// <summary>
    /// Creates a channel bound to one Nacos server.
    /// </summary>
    /// <param name="address">The server endpoint, including the derived gRPC port.</param>
    /// <returns>The channel, which the caller owns and must dispose.</returns>
    INacosChannel Create(NacosServerAddress address);
}
