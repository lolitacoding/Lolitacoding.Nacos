using Grpc.Core;
using Lolitacoding.Nacos.Protocol.Generated;

namespace Lolitacoding.Nacos.Protocol;

/// <summary>
/// Hand-written gRPC stubs for the two services declared in the official Nacos
/// <c>nacos_grpc_service.proto</c>: the unary <c>Request</c> service and the
/// bidirectional <c>BiRequestStream</c> service.
/// </summary>
/// <remarks>
/// <para>
/// <c>Grpc.Tools</c> cannot generate usable stubs for this contract: the unary RPC is
/// named <c>request</c>, so the generated convenience overload declares a parameter with
/// the same name and produces uncompilable code (<c>CS0149</c>). The message types are
/// still generated from the official <c>.proto</c>; only these thin client wrappers are
/// authored by hand.
/// </para>
/// <para>
/// Both services carry an identical <see cref="Payload"/> envelope, so a single set of
/// marshallers is reused. gRPC method names and the service name must match the server
/// exactly: <c>{service}/{method}</c> becomes <c>Request/request</c> and
/// <c>BiRequestStream/requestBiStream</c>.
/// </para>
/// </remarks>
internal static class NacosGrpcStubs
{
    /// <summary>The full gRPC service name of the unary request service.</summary>
    public const string RequestServiceName = "Request";

    /// <summary>The full gRPC service name of the bidirectional stream service.</summary>
    public const string BiRequestStreamServiceName = "BiRequestStream";

    /// <summary>The unary <c>Request/request</c> method descriptor.</summary>
    public static readonly Method<Payload, Payload> RequestMethod = new(
        MethodType.Unary,
        RequestServiceName,
        "request",
        ProtobufMarshaller<Payload>.Instance,
        ProtobufMarshaller<Payload>.Instance);

    /// <summary>The duplex-streaming <c>BiRequestStream/requestBiStream</c> method descriptor.</summary>
    public static readonly Method<Payload, Payload> BiRequestStreamMethod = new(
        MethodType.DuplexStreaming,
        BiRequestStreamServiceName,
        "requestBiStream",
        ProtobufMarshaller<Payload>.Instance,
        ProtobufMarshaller<Payload>.Instance);

    /// <summary>
    /// Issues a unary Nacos request over the supplied call invoker.
    /// </summary>
    /// <param name="callInvoker">The gRPC call invoker bound to a specific server.</param>
    /// <param name="payload">The request envelope.</param>
    /// <param name="options">The call options (deadline, headers, cancellation).</param>
    /// <returns>The asynchronous unary call carrying the response envelope.</returns>
    public static AsyncUnaryCall<Payload> SendRequestAsync(
        CallInvoker callInvoker,
        Payload payload,
        CallOptions options)
        => callInvoker.AsyncUnaryCall(RequestMethod, host: null, options, payload);

    /// <summary>
    /// Opens the bidirectional stream used for server-initiated pushes such as
    /// configuration change notifications and service instance change notifications.
    /// </summary>
    /// <param name="callInvoker">The gRPC call invoker bound to a specific server.</param>
    /// <param name="options">The call options (deadline, headers, cancellation).</param>
    /// <returns>The duplex streaming call whose request stream accepts client pushes.</returns>
    public static AsyncDuplexStreamingCall<Payload, Payload> OpenBiRequestStream(
        CallInvoker callInvoker,
        CallOptions options)
        => callInvoker.AsyncDuplexStreamingCall(BiRequestStreamMethod, host: null, options);
}
