using Grpc.Core;
using Grpc.Net.Client;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Generated;

namespace Lolitacoding.Nacos.Transport;

/// <summary>
/// A <see cref="INacosChannel"/> backed by a real <see cref="GrpcChannel"/>.
/// </summary>
/// <remarks>
/// Nacos multiplexes a unary RPC and a bidirectional stream over one HTTP/2 connection, so a
/// single <see cref="GrpcChannel"/> is shared by both and disposed with this object. The channel
/// is created with <c>http</c> for plaintext endpoints; TLS endpoints pass <c>https</c> through
/// unchanged so the platform's TLS stack performs the handshake, optionally refined by
/// <see cref="NacosTlsOptions"/>.
/// </remarks>
internal sealed class GrpcNacosChannel : INacosChannel
{
    private readonly GrpcChannel _channel;

    /// <summary>
    /// Initializes a new instance of the <see cref="GrpcNacosChannel"/> class.
    /// </summary>
    /// <param name="address">The server endpoint whose gRPC port was already derived.</param>
    /// <param name="requestTimeout">The timeout applied to a single unary call.</param>
    /// <param name="tls">
    /// The TLS settings, or <see langword="null"/> to use the platform defaults. Only consulted
    /// when <paramref name="address"/> uses TLS.
    /// </param>
    public GrpcNacosChannel(NacosServerAddress address, TimeSpan requestTimeout, NacosTlsOptions? tls = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        RequestTimeout = requestTimeout;

        // The gRPC port is derived by NacosServerAddress (always main + 1000, for TLS and
        // plaintext alike), and the Nacos gRPC endpoint lives at the root, so no context path is
        // appended here.
        var options = new GrpcChannelOptions
        {
            MaxReceiveMessageSize = 10 * 1024 * 1024,
            MaxSendMessageSize = 10 * 1024 * 1024,
        };

        // A custom handler is only needed when the caller configured TLS behaviour beyond the
        // platform defaults; otherwise the channel's default handler handshakes correctly.
        if (address.UseTls && NacosTlsHandler.CreateHandler(tls) is { } handler)
        {
            options.HttpHandler = handler;
        }

        _channel = GrpcChannel.ForAddress(address.ToGrpcAddress(), options);
    }

    /// <summary>Gets the timeout applied to a single unary call.</summary>
    public TimeSpan RequestTimeout { get; }

    /// <inheritdoc />
    public async Task<Payload> UnaryAsync(Payload request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            using AsyncUnaryCall<Payload> call = NacosGrpcStubs.SendRequestAsync(
                _channel.CreateCallInvoker(),
                request,
                new CallOptions(deadline: DateTime.UtcNow.Add(RequestTimeout), cancellationToken: ct));

            return await call.ResponseAsync.ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded)
        {
            throw new NacosTimeoutException(
                $"The Nacos server did not answer within {RequestTimeout}.", ex);
        }
        catch (RpcException ex)
        {
            throw new NacosConnectionException(
                $"The gRPC request failed with status '{ex.StatusCode}': {ex.Status.Detail}", ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new NacosTimeoutException(
                $"The Nacos server did not answer within {RequestTimeout}.");
        }
    }

    /// <inheritdoc />
    public INacosDuplexStream OpenStream(CancellationToken ct)
    {
        try
        {
            AsyncDuplexStreamingCall<Payload, Payload> call = NacosGrpcStubs.OpenBiRequestStream(
                _channel.CreateCallInvoker(),
                new CallOptions(cancellationToken: ct));

            return new GrpcNacosDuplexStream(call);
        }
        catch (RpcException ex)
        {
            throw new NacosConnectionException(
                $"The Nacos notification stream could not be opened (status '{ex.StatusCode}').", ex);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _channel.Dispose();

    /// <summary>
    /// Adapts an <see cref="AsyncDuplexStreamingCall{TRequest,TResponse}"/> to the transport
    /// abstraction.
    /// </summary>
    /// <remarks>
    /// gRPC forbids concurrent writes on a stream, and Nacos pushes responses from a read loop
    /// while callers write requests, so every write is serialized on a semaphore.
    /// </remarks>
    private sealed class GrpcNacosDuplexStream : INacosDuplexStream
    {
        private readonly AsyncDuplexStreamingCall<Payload, Payload> _call;
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private bool _completed;

        public GrpcNacosDuplexStream(AsyncDuplexStreamingCall<Payload, Payload> call) => _call = call;

        public async Task WriteAsync(Payload payload, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(payload);

            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_completed)
                {
                    throw new NacosConnectionException(
                        "The Nacos notification stream is already completed.");
                }

                await _call.RequestStream.WriteAsync(payload, ct).ConfigureAwait(false);
            }
            catch (RpcException ex)
            {
                throw new NacosConnectionException(
                    $"Writing to the Nacos notification stream failed (status '{ex.StatusCode}').", ex);
            }
            catch (InvalidOperationException ex)
            {
                // gRPC reports a stream that has already completed this way.
                throw new NacosConnectionException(
                    "Writing to the Nacos notification stream failed because the stream is closed.", ex);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        public async Task<Payload?> ReadAsync(CancellationToken ct)
        {
            try
            {
                if (await _call.ResponseStream.MoveNext(ct).ConfigureAwait(false))
                {
                    return _call.ResponseStream.Current;
                }

                // A clean end of stream means the server closed the connection.
                _completed = true;
                return null;
            }
            catch (RpcException ex)
            {
                _completed = true;
                throw new NacosConnectionException(
                    $"The Nacos notification stream ended unexpectedly (status '{ex.StatusCode}').", ex);
            }
            catch (InvalidOperationException ex)
            {
                _completed = true;
                throw new NacosConnectionException(
                    "The Nacos notification stream ended because the stream is closed.", ex);
            }
        }

        public void Dispose()
        {
            _completed = true;
            _writeGate.Dispose();
            _call.Dispose();
        }
    }
}

/// <summary>
/// Creates real gRPC channels.
/// </summary>
internal sealed class GrpcNacosChannelFactory : INacosChannelFactory
{
    private readonly TimeSpan _requestTimeout;
    private readonly NacosTlsOptions? _tls;

    /// <summary>
    /// Initializes a new instance of the <see cref="GrpcNacosChannelFactory"/> class.
    /// </summary>
    /// <param name="requestTimeout">The timeout applied to a single unary call.</param>
    /// <param name="tls">The TLS settings, or <see langword="null"/> for platform defaults.</param>
    public GrpcNacosChannelFactory(TimeSpan requestTimeout, NacosTlsOptions? tls = null)
    {
        _requestTimeout = requestTimeout;
        _tls = tls;
    }

    /// <inheritdoc />
    public INacosChannel Create(NacosServerAddress address) =>
        new GrpcNacosChannel(address, _requestTimeout, _tls);
}
