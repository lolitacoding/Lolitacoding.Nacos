using Google.Protobuf;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Generated;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;

namespace Lolitacoding.Nacos.Tests.Transport;

/// <summary>
/// An in-memory <see cref="INacosChannel"/> that records what the transport sent and replays
/// what a scripted server would answer. It replaces the network entirely so handshake and
/// reconnect behaviour can be asserted deterministically.
/// </summary>
internal sealed class FakeNacosChannel : INacosChannel
{
    private readonly Func<Payload, Payload> _unaryHandler;

    public FakeNacosChannel(
        NacosServerAddress address,
        Func<Payload, Payload> unaryHandler,
        FakeNacosDuplexStream stream)
    {
        Address = address;
        _unaryHandler = unaryHandler;
        Stream = stream;
    }

    /// <summary>Gets the address this channel was created for.</summary>
    public NacosServerAddress Address { get; }

    /// <summary>Gets the stream this channel handed out.</summary>
    public FakeNacosDuplexStream Stream { get; }

    /// <summary>Gets every unary request this channel received, in order.</summary>
    public List<Payload> UnaryRequests { get; } = [];

    /// <summary>Gets a value indicating whether this channel was disposed.</summary>
    public bool Disposed { get; private set; }

    public Task<Payload> UnaryAsync(Payload request, CancellationToken ct)
    {
        UnaryRequests.Add(request);
        return Task.FromResult(_unaryHandler(request));
    }

    public INacosDuplexStream OpenStream(CancellationToken ct) => Stream;

    public void Dispose() => Disposed = true;
}

/// <summary>
/// An in-memory <see cref="INacosDuplexStream"/> driven by a channel so tests can push frames
/// to the transport exactly as a server would.
/// </summary>
internal sealed class FakeNacosDuplexStream : INacosDuplexStream
{
    private readonly System.Threading.Channels.Channel<Payload?> _inbound =
        System.Threading.Channels.Channel.CreateUnbounded<Payload?>();

    /// <summary>
    /// Gets or sets a value indicating whether writing the connection setup is answered with a
    /// <c>SetupAckRequest</c>, mirroring a server that supports ability negotiation.
    /// </summary>
    public bool AutoAckSetup { get; set; } = true;

    /// <summary>
    /// Gets or sets the ability table reported in the automatic acknowledgement.
    /// </summary>
    /// <remarks>
    /// A non-null table is what makes the acknowledgement valid: the server only sends one when
    /// the client advertised abilities.
    /// </remarks>
    public Dictionary<string, bool>? SetupAckAbilityTable { get; set; } = [];

    /// <summary>Gets every frame the transport wrote to this stream, in order.</summary>
    public List<Payload> Written { get; } = [];

    /// <summary>Gets a value indicating whether this stream was disposed.</summary>
    public bool Disposed { get; private set; }

    /// <summary>Pushes a frame to the transport, as a server would.</summary>
    /// <param name="payload">The frame to push.</param>
    public void Push(Payload payload) => _inbound.Writer.TryWrite(payload);

    /// <summary>Ends the stream, as a server closing the connection would.</summary>
    public void EndStream() => _inbound.Writer.TryWrite(null);

    public Task WriteAsync(Payload payload, CancellationToken ct)
    {
        lock (Written)
        {
            Written.Add(payload);
        }

        if (AutoAckSetup && PayloadCodec.ReadTypeName(payload) == "ConnectionSetupRequest")
        {
            Push(TestPayload.ForRequest(new SetupAckRequest
            {
                AbilityTable = SetupAckAbilityTable,
            }));
        }

        return Task.CompletedTask;
    }

    public async Task<Payload?> ReadAsync(CancellationToken ct) =>
        await _inbound.Reader.ReadAsync(ct).ConfigureAwait(false);

    public void Dispose() => Disposed = true;
}

/// <summary>
/// An <see cref="INacosChannelFactory"/> that hands out pre-built fake channels and records how
/// many times a channel was requested.
/// </summary>
internal sealed class FakeNacosChannelFactory : INacosChannelFactory
{
    private readonly Func<NacosServerAddress, INacosChannel> _factory;

    public FakeNacosChannelFactory(Func<NacosServerAddress, INacosChannel> factory) => _factory = factory;

    /// <summary>Gets the addresses a channel was requested for, in order.</summary>
    public List<NacosServerAddress> RequestedAddresses { get; } = [];

    public INacosChannel Create(NacosServerAddress address)
    {
        RequestedAddresses.Add(address);
        return _factory(address);
    }
}

/// <summary>
/// A successful server check response, which is the first call of every handshake.
/// </summary>
internal static class FakeServerCheck
{
    /// <summary>Builds a channel handler that answers the server check successfully.</summary>
    /// <param name="connectionId">The connection identifier to report.</param>
    /// <returns>The handler.</returns>
    public static Func<Payload, Payload> Responding(string connectionId = "conn-1") => request =>
    {
        PayloadMetadataAssertions.AssertType(request, "ServerCheckRequest");

        var response = new ServerCheckResponse
        {
            ConnectionId = connectionId,
            SupportAbilityNegotiation = true,
        };

        return PayloadCodec.Encode(response);
    };
}

/// <summary>
/// Assertions shared by the transport tests.
/// </summary>
internal static class PayloadMetadataAssertions
{
    /// <summary>Asserts that an envelope carries the expected payload type name.</summary>
    /// <param name="payload">The envelope.</param>
    /// <param name="expected">The expected <c>metadata.type</c>.</param>
    public static void AssertType(Payload payload, string expected) =>
        payload.Metadata.Type.ShouldBe(expected);
}

