using System.Diagnostics.Metrics;

namespace Lolitacoding.Nacos.Diagnostics;

/// <summary>
/// The metric instruments the client records observations through.
/// </summary>
/// <remarks>
/// All instruments are created once from <see cref="NacosDiagnostics.Meter"/>. Instrument names
/// follow the OpenTelemetry semantic-convention shape (<c>nacos.client.&lt;measure&gt;</c>) so they
/// can be enabled with a single <c>AddMeter(NacosDiagnostics.MeterName)</c> call.
/// </remarks>
internal static class NacosMetrics
{
    /// <summary>
    /// The number of requests sent to Nacos, tagged with the request type and outcome.
    /// </summary>
    internal static readonly Counter<long> Requests = NacosDiagnostics.Meter.CreateCounter<long>(
        "nacos.client.requests",
        unit: "{request}",
        description: "Number of requests sent to the Nacos server, by request type and outcome.");

    /// <summary>
    /// The duration of requests sent to Nacos, tagged with the request type.
    /// </summary>
    internal static readonly Histogram<double> RequestDuration =
        NacosDiagnostics.Meter.CreateHistogram<double>(
            "nacos.client.request.duration",
            unit: "ms",
            description: "Duration of requests sent to the Nacos server, by request type.");

    /// <summary>
    /// The number of successful reconnections to Nacos.
    /// </summary>
    internal static readonly Counter<long> Reconnects = NacosDiagnostics.Meter.CreateCounter<long>(
        "nacos.client.reconnects",
        unit: "{reconnect}",
        description: "Number of times the client re-established its connection to Nacos.");

    /// <summary>
    /// The number of server-initiated pushes received, tagged with the payload type.
    /// </summary>
    internal static readonly Counter<long> Pushes = NacosDiagnostics.Meter.CreateCounter<long>(
        "nacos.client.pushes",
        unit: "{push}",
        description: "Number of server-initiated pushes received, by payload type.");

    /// <summary>
    /// The number of transport state transitions, tagged with the state that was entered.
    /// </summary>
    internal static readonly Counter<long> StateChanges = NacosDiagnostics.Meter.CreateCounter<long>(
        "nacos.client.connection.state.changes",
        unit: "{change}",
        description: "Number of Nacos transport state transitions, by the state entered.");
}
