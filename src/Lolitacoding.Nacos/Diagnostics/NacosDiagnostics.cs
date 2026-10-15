using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Lolitacoding.Nacos.Diagnostics;

/// <summary>
/// Exposes the <see cref="System.Diagnostics.ActivitySource"/> and <see cref="Meter"/> the client
/// publishes telemetry through, so an application can subscribe to traces and metrics.
/// </summary>
/// <remarks>
/// <para>
/// Both instruments are process-wide singletons that the library never disposes, matching the
/// convention used by the .NET runtime and by OpenTelemetry itself: a diagnostics listener may
/// attach at any time and must keep working for the life of the process.
/// </para>
/// <para>
/// Nothing is recorded until a listener subscribes. Register traces with
/// <c>AddSource(NacosDiagnostics.ActivitySourceName)</c> and metrics with
/// <c>AddMeter(NacosDiagnostics.MeterName)</c> using the OpenTelemetry SDK, or subscribe directly
/// through <see cref="ActivitySource.AddActivityListener"/> and the <see cref="Meter"/> API.
/// </para>
/// </remarks>
public static class NacosDiagnostics
{
    /// <summary>The name of the <see cref="System.Diagnostics.ActivitySource"/>.</summary>
    public const string ActivitySourceName = "Lolitacoding.Nacos";

    /// <summary>The name of the <see cref="Meter"/>.</summary>
    public const string MeterName = "Lolitacoding.Nacos";

    /// <summary>
    /// The span every Nacos request is emitted under, with <c>ActivityKind.Client</c>.
    /// </summary>
    internal const string RequestActivityName = "Nacos.Client.Request";

    /// <summary>
    /// The span a connection attempt (including failover between servers) is emitted under.
    /// </summary>
    internal const string ConnectActivityName = "Nacos.Client.Connect";

    /// <summary>The tag carrying the wire type name of the request, for example <c>ConfigQueryRequest</c>.</summary>
    internal const string RequestTypeTag = "nacos.request.type";

    /// <summary>The tag carrying the wire type name of a pushed request.</summary>
    internal const string PushTypeTag = "nacos.push.type";

    /// <summary>The tag carrying the outcome of a request; <c>success</c> or <c>error</c>.</summary>
    internal const string OutcomeTag = "nacos.outcome";

    /// <summary>The tag carrying the address of the server a span or state change concerns.</summary>
    internal const string ServerAddressTag = "nacos.server.address";

    /// <summary>The tag carrying the transport state that was entered.</summary>
    internal const string ConnectionStateTag = "nacos.connection.state";

    /// <summary>The tag recording whether a connection attempt followed a lost connection.</summary>
    internal const string ReconnectTag = "nacos.connection.reconnect";

    /// <summary>The outcome value recorded when a request completed successfully.</summary>
    internal const string OutcomeSuccess = "success";

    /// <summary>The outcome value recorded when a request failed.</summary>
    internal const string OutcomeError = "error";

    /// <summary>Gets the activity source that carries Nacos client spans.</summary>
    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName, GetVersion());

    /// <summary>Gets the meter that carries Nacos client instruments.</summary>
    public static Meter Meter { get; } = new(MeterName, GetVersion());

    private static string GetVersion() =>
        typeof(NacosDiagnostics).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
