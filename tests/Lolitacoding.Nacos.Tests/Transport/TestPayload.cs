using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Generated;
using Lolitacoding.Nacos.Protocol.Messages;

namespace Lolitacoding.Nacos.Tests.Transport;

/// <summary>
/// Builds payload envelopes the way a Nacos server would.
/// </summary>
/// <remarks>
/// The production codec encodes requests for transmission, which stamps the client's headers
/// onto the envelope. A server pushing at a client does not do that, and it pushes
/// request-typed payloads such as <c>SetupAckRequest</c>, so tests need to construct an
/// envelope directly. Keeping this in the test project stops a server-only concern from
/// leaking into the shipped API.
/// </remarks>
internal static class TestPayload
{
    /// <summary>
    /// Encodes a request-typed message as a bare server push.
    /// </summary>
    /// <param name="request">The message to encode.</param>
    /// <returns>The envelope.</returns>
    public static Payload ForRequest(NacosRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string typeName = request.GetType().Name;
        JsonTypeInfo typeInfo = PayloadTypeRegistry.Resolve(typeName)
            ?? throw new InvalidOperationException($"Payload type '{typeName}' is not registered.");

        return new Payload
        {
            Metadata = new Metadata { Type = typeName },
            Body = new Any { Value = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(request, typeInfo)) },
        };
    }
}
