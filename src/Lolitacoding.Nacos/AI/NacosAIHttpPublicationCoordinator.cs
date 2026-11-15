using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.AI;

/// <summary>
/// One module's HTTP endpoint publications, as the coordinator sees them.
/// </summary>
/// <remarks>
/// The MCP and agent registries publish over the same HTTP client identity, and the server keeps
/// one liveness record for that identity rather than one per module. So the modules cannot each run
/// their own heartbeat: one heartbeat refreshes both, and a client the server has forgotten has to
/// be reported to both at once, before either replays, or the first replay would recreate the client
/// and hide the loss from the other.
/// </remarks>
internal interface INacosAIHttpPublicationParticipant
{
    /// <summary>Gets the module name, used when a failure is reported.</summary>
    string ModuleName { get; }

    /// <summary>Gets a value indicating whether this module owns any HTTP publication.</summary>
    bool HasHttpPublication { get; }

    /// <summary>
    /// Gets a value indicating whether this module owns a publication the server currently knows.
    /// </summary>
    bool HasRegisteredHttpPublication { get; }

    /// <summary>
    /// Refreshes the shared HTTP client through this module's heartbeat path.
    /// </summary>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The liveness intervals the server reported, or <see langword="null"/> for none.</returns>
    Task<NacosClientLivenessInfo?> HeartbeatAsync(CancellationToken ct);

    /// <summary>
    /// Marks every HTTP publication this module owns for a replay.
    /// </summary>
    void MarkHttpPublicationsDirty();

    /// <summary>
    /// Replays the HTTP publications this module marked for a replay.
    /// </summary>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>A task that completes once every marked publication was attempted.</returns>
    Task RedoDirtyHttpPublicationsAsync(CancellationToken ct);
}

/// <summary>
/// Runs one heartbeat and replay cycle for every AI module that publishes endpoints over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// An HTTP endpoint publication is kept alive by a heartbeat and expired by a timeout, unlike a gRPC
/// publication which lives with its connection. The server tracks the heartbeat per HTTP client
/// rather than per endpoint, so one timer serves every module: when the interval elapses, the
/// coordinator replays whatever is marked dirty, heartbeats through the first module that owns a
/// live publication, and adopts the interval the server reports.
/// </para>
/// <para>
/// The cycle does nothing while no module owns an HTTP publication, which is the usual case for a
/// gRPC or <see cref="NacosAITransportMode.Auto"/> client that has not fallen back. The timer is
/// then not merely idle but absent, so a client that never publishes over HTTP pays nothing.
/// </para>
/// </remarks>
internal sealed class NacosAIHttpPublicationCoordinator : IDisposable
{
    /// <summary>The interval used until the server reports one.</summary>
    private static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly OrderedDictionary<INacosAIHttpPublicationParticipant, bool> _activity = new();
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    private ITimer? _timer;
    private TimeSpan _heartbeatInterval = DefaultHeartbeatInterval;
    private bool _maintaining;
    private bool _closed;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosAIHttpPublicationCoordinator"/> class.
    /// </summary>
    /// <param name="timeProvider">The clock the heartbeat interval is measured with.</param>
    /// <param name="logger">The logger.</param>
    public NacosAIHttpPublicationCoordinator(TimeProvider timeProvider, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Adds a module whose publications this coordinator keeps alive.
    /// </summary>
    /// <param name="participant">The module.</param>
    public void Register(INacosAIHttpPublicationParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);

        lock (_gate)
        {
            _activity[participant] = false;
        }
    }

    /// <summary>
    /// Reports that a module's publications changed, and reschedules the cycle.
    /// </summary>
    /// <param name="participant">The module that changed.</param>
    /// <param name="liveness">The intervals the server reported, or <see langword="null"/> for none.</param>
    /// <param name="hasHttpPublication">Whether the module now owns an HTTP publication.</param>
    public void StateChanged(
        INacosAIHttpPublicationParticipant participant,
        NacosClientLivenessInfo? liveness,
        bool hasHttpPublication)
    {
        ArgumentNullException.ThrowIfNull(participant);

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _activity[participant] = hasHttpPublication;

            if (liveness is { HeartbeatIntervalMillis: > 0 })
            {
                _heartbeatInterval = TimeSpan.FromMilliseconds(liveness.HeartbeatIntervalMillis);
            }

            // A cycle that is already running reschedules itself when it finishes, so scheduling
            // from inside one would stack a second timer on the same interval.
            if (_maintaining)
            {
                return;
            }

            ScheduleIfRequired();
        }
    }

    /// <summary>
    /// Runs one replay and heartbeat cycle now.
    /// </summary>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>A task that completes once the cycle finished.</returns>
    /// <remarks>
    /// Exposed so the cycle can be driven directly, which is what the tests do: waiting on a timer
    /// would make them depend on wall-clock time for no extra coverage.
    /// </remarks>
    public async Task MaintainAsync(CancellationToken ct = default)
    {
        INacosAIHttpPublicationParticipant[] snapshot;

        lock (_gate)
        {
            if (_closed || _maintaining || !HasHttpPublication())
            {
                return;
            }

            _maintaining = true;
            snapshot = [.. _activity.Keys];
        }

        try
        {
            await RedoAllAsync(snapshot, ct).ConfigureAwait(false);

            INacosAIHttpPublicationParticipant? owner = null;

            foreach (INacosAIHttpPublicationParticipant participant in snapshot)
            {
                if (participant.HasRegisteredHttpPublication)
                {
                    owner = participant;
                    break;
                }
            }

            if (owner is null)
            {
                return;
            }

            try
            {
                NacosClientLivenessInfo? liveness =
                    await owner.HeartbeatAsync(ct).ConfigureAwait(false);

                StateChanged(owner, liveness, owner.HasHttpPublication);
            }
            catch (NacosException ex) when (ex.ErrorCode == NacosAIEndpointHttp.HttpClientNotFoundErrorCode)
            {
                // The server forgot the client, so every module's publication is gone rather than
                // one endpoint. Marking them all before any replay is what keeps the first replay
                // from recreating the client and making the other module look healthy.
                NacosAILog.AiPublicationClientLost(_logger, ex.Message);

                MarkAllDirty(snapshot);
                await RedoAllAsync(snapshot, ct).ConfigureAwait(false);
            }
            catch (NacosException ex)
            {
                NacosAILog.AiPublicationHeartbeatFailed(_logger, owner.ModuleName, ex.Message);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client is shutting down; the next cycle is the next process's problem.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The AI HTTP publication cycle failed.");
        }
        finally
        {
            lock (_gate)
            {
                _maintaining = false;
                ScheduleIfRequired();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _timer?.Dispose();
            _timer = null;
            _activity.Clear();
        }
    }

    /// <summary>
    /// Starts the timer when a module owns an HTTP publication, and stops it otherwise.
    /// </summary>
    /// <remarks>
    /// The caller holds <see cref="_gate"/>.
    /// </remarks>
    private void ScheduleIfRequired()
    {
        if (_closed || !HasHttpPublication())
        {
            CancelTimer();
            return;
        }

        _timer ??= _timeProvider.CreateTimer(
            static state => ((NacosAIHttpPublicationCoordinator)state!).OnTimer(),
            this,
            _heartbeatInterval,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Runs the cycle when the timer elapses.
    /// </summary>
    private void OnTimer()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;

            if (_closed || !HasHttpPublication())
            {
                return;
            }
        }

        // The cycle reports its own failures; nothing awaits the timer callback.
        _ = MaintainAsync();
    }

    /// <summary>
    /// Stops the timer.
    /// </summary>
    /// <remarks>
    /// The caller holds <see cref="_gate"/>.
    /// </remarks>
    private void CancelTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// Reports whether any module owns an HTTP publication.
    /// </summary>
    /// <returns><see langword="true"/> when at least one does.</returns>
    /// <remarks>
    /// The caller holds <see cref="_gate"/>.
    /// </remarks>
    private bool HasHttpPublication()
    {
        foreach (bool active in _activity.Values)
        {
            if (active)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Marks every module's HTTP publications for a replay.
    /// </summary>
    /// <param name="snapshot">The modules registered when the cycle started.</param>
    private static void MarkAllDirty(INacosAIHttpPublicationParticipant[] snapshot)
    {
        foreach (INacosAIHttpPublicationParticipant participant in snapshot)
        {
            participant.MarkHttpPublicationsDirty();
        }
    }

    /// <summary>
    /// Replays every module's marked publications.
    /// </summary>
    /// <param name="snapshot">The modules registered when the cycle started.</param>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>A task that completes once every module was attempted.</returns>
    private static async Task RedoAllAsync(
        INacosAIHttpPublicationParticipant[] snapshot,
        CancellationToken ct)
    {
        foreach (INacosAIHttpPublicationParticipant participant in snapshot)
        {
            await participant.RedoDirtyHttpPublicationsAsync(ct).ConfigureAwait(false);
        }
    }
}
