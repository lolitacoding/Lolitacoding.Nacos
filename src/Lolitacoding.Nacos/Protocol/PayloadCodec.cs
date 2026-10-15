using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Lolitacoding.Nacos.Protocol.Generated;
using Lolitacoding.Nacos.Protocol.Messages;

namespace Lolitacoding.Nacos.Protocol;

/// <summary>
/// Converts between Nacos payload objects and the protobuf <see cref="Payload"/> envelope that
/// travels over gRPC.
/// </summary>
/// <remarks>
/// <para>
/// The Nacos transport wraps a JSON document in protobuf rather than describing the messages in
/// protobuf. Concretely: <c>metadata.type</c> holds the simple payload class name,
/// <c>metadata.headers</c> holds the request headers, and <c>body</c> is a
/// <see cref="Any"/> whose <c>value</c> is the UTF-8 JSON of the message. The <c>type_url</c>
/// field is deliberately left empty — the official clients never set it, and neither the server
/// nor the client reads it.
/// </para>
/// <para>
/// Every method here allocates only what the wire format requires and takes no dependency on
/// reflection, so it is safe under trimming and Native AOT.
/// </para>
/// </remarks>
internal static class PayloadCodec
{
    /// <summary>
    /// Builds the protobuf envelope for a request.
    /// </summary>
    /// <param name="request">The request to encode.</param>
    /// <param name="clientIp">The local IP the server associates with the request.</param>
    /// <returns>The populated envelope.</returns>
    public static Payload Encode(NacosRequest request, string? clientIp)
    {
        string typeName = PayloadTypeRegistry.GetTypeName(request);
        JsonTypeInfo typeInfo = ResolveRequired(typeName);

        Metadata metadata = new()
        {
            Type = typeName,
        };

        if (!string.IsNullOrEmpty(clientIp))
        {
            metadata.ClientIp = clientIp;
        }

        // Headers live in the metadata, not the body. Nacos' convert path empties the bean's
        // headers before serializing so the body copy stays empty; this mirrors that exactly.
        foreach (KeyValuePair<string, string> header in request.Headers)
        {
            metadata.Headers[header.Key] = header.Value;
        }

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, typeInfo);

        return new Payload
        {
            Metadata = metadata,
            Body = new Any { Value = ByteString.CopyFrom(json) },
        };
    }

    /// <summary>
    /// Builds the protobuf envelope for a response the client sends back to the server.
    /// </summary>
    /// <param name="response">The response to encode.</param>
    /// <returns>The populated envelope.</returns>
    public static Payload Encode(NacosResponse response)
    {
        string typeName = PayloadTypeRegistry.GetTypeName(response);
        JsonTypeInfo typeInfo = ResolveRequired(typeName);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(response, typeInfo);

        return new Payload
        {
            Metadata = new Metadata { Type = typeName },
            Body = new Any { Value = ByteString.CopyFrom(json) },
        };
    }

    /// <summary>
    /// Reads the payload type name from an envelope.
    /// </summary>
    /// <param name="payload">The received envelope.</param>
    /// <returns>The type name, or an empty string when the envelope carries no metadata.</returns>
    public static string ReadTypeName(Payload payload) => payload.Metadata?.Type ?? string.Empty;

    /// <summary>
    /// Decodes a received envelope into a strongly typed request.
    /// </summary>
    /// <typeparam name="TRequest">The expected request type.</typeparam>
    /// <param name="payload">The received envelope.</param>
    /// <returns>The decoded request.</returns>
    /// <exception cref="NacosException">The body carries an unknown or malformed payload.</exception>
    public static TRequest DecodeRequest<TRequest>(Payload payload)
        where TRequest : NacosRequest
    {
        TRequest? result = Decode<TRequest>(payload);

        if (result is null)
        {
            throw new NacosException(
                string.Concat(
                    "The response body could not be read as ", typeof(TRequest).Name,
                    " (server reported type '", ReadTypeName(payload), "')."));
        }

        // A received request carries its headers in the gRPC metadata, so copy them onto the
        // bean exactly as the official clients do; downstream code then reads them uniformly
        // whether the message was encoded locally or received.
        if (payload.Metadata is not null)
        {
            foreach (KeyValuePair<string, string> header in payload.Metadata.Headers)
            {
                result.SetHeader(header.Key, header.Value);
            }
        }

        return result;
    }

    /// <summary>
    /// Decodes a received envelope using the type named in its metadata.
    /// </summary>
    /// <param name="payload">The received envelope.</param>
    /// <returns>The decoded message, or <see langword="null"/> when the type is not registered.</returns>
    /// <exception cref="NacosException">The body is not valid JSON for the named type.</exception>
    public static object? DecodeAny(Payload payload)
    {
        string typeName = ReadTypeName(payload);

        if (typeName.Length == 0)
        {
            return null;
        }

        JsonTypeInfo? typeInfo = PayloadTypeRegistry.Resolve(typeName);
        if (typeInfo is null)
        {
            // Nacos raises "Unknown payload type" for the same condition. Returning null lets
            // the stream handler log and ignore a payload from a newer server instead of
            // tearing down a healthy connection.
            return null;
        }

        byte[] json = payload.Body?.Value.ToByteArray() ?? [];

        try
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException ex)
        {
            throw new NacosException(
                string.Concat("Failed to deserialize payload of type '", typeName, "'."),
                ex);
        }
    }

    /// <summary>
    /// Decodes a received envelope into <typeparamref name="T"/>, or returns
    /// <see langword="null"/> when the payload is not of that type.
    /// </summary>
    /// <typeparam name="T">The expected message type.</typeparam>
    /// <param name="payload">The received envelope.</param>
    /// <returns>The decoded message, or <see langword="null"/>.</returns>
    public static T? Decode<T>(Payload payload)
        where T : class
    {
        object? decoded = DecodeAny(payload);
        return decoded as T;
    }

    /// <summary>
    /// Reads the UTF-8 JSON body of an envelope without interpreting it.
    /// </summary>
    /// <param name="payload">The received envelope.</param>
    /// <returns>The raw JSON bytes.</returns>
    public static byte[] ReadBodyBytes(Payload payload) => payload.Body?.Value.ToByteArray() ?? [];

    private static JsonTypeInfo ResolveRequired(string typeName)
        => PayloadTypeRegistry.Resolve(typeName)
           ?? throw new NacosException(
               string.Concat(
                   "The payload type '", typeName,
                   "' is not registered in the local payload registry."));
}
