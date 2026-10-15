using System.Buffers;
using Google.Protobuf;
using Grpc.Core;

namespace Lolitacoding.Nacos.Protocol;

/// <summary>
/// Provides allocation-conscious protobuf marshallers for the generated Nacos
/// transport messages.
/// </summary>
/// <typeparam name="T">The protobuf message type to marshall.</typeparam>
/// <remarks>
/// The generated gRPC stubs cannot be used for this protocol because the upstream RPC is
/// named <c>request</c>, which collides with the generator's own <c>request</c> parameter.
/// These marshallers therefore back hand-written stubs that avoid reflection, so the
/// assembly stays trim- and Native AOT-safe.
/// </remarks>
internal static class ProtobufMarshaller<T>
    where T : class, IMessage<T>, new()
{
    /// <summary>
    /// The shared parser for <typeparamref name="T"/>.
    /// </summary>
    private static readonly MessageParser<T> Parser = new(static () => new T());

    /// <summary>
    /// Gets the marshaller used for every <typeparamref name="T"/> gRPC call.
    /// </summary>
    public static Marshaller<T> Instance { get; } = Marshallers.Create(Serialize, Deserialize);

    private static void Serialize(T value, SerializationContext context)
    {
        context.SetPayloadLength(value.CalculateSize());
        value.WriteTo(context.GetBufferWriter());
        context.Complete();
    }

    private static T Deserialize(DeserializationContext context)
        => Parser.ParseFrom(context.PayloadAsReadOnlySequence());
}
