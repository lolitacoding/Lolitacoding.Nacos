namespace Lolitacoding.Nacos.Transport;

/// <summary>
/// Computes the delay before each reconnect attempt using exponential backoff with jitter.
/// </summary>
/// <remarks>
/// <para>
/// The delay starts at <see cref="NacosReconnectOptions.InitialDelay"/> and is multiplied by
/// <see cref="NacosReconnectOptions.Multiplier"/> after each failed attempt, capped at
/// <see cref="NacosReconnectOptions.MaxDelay"/>. A random jitter of up to
/// <see cref="NacosReconnectOptions.JitterFactor"/> of the computed delay is then subtracted,
/// which spreads reconnect attempts across a fleet after a shared server restarts instead of
/// having every client retry in lockstep.
/// </para>
/// <para>
/// Delays are computed as pure functions of the attempt number, and waiting goes through
/// <see cref="TimeProvider"/>, so tests can drive an entire reconnect sequence with a fake clock
/// rather than real time.
/// </para>
/// </remarks>
internal sealed class NacosBackoffPolicy
{
    private readonly NacosReconnectOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Random _random;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosBackoffPolicy"/> class.
    /// </summary>
    /// <param name="options">The reconnect tuning values.</param>
    /// <param name="timeProvider">The clock and timer source used for delays.</param>
    /// <param name="random">
    /// The random source for jitter. Supplying a seeded instance makes the sequence
    /// reproducible in tests; a shared instance must not be used because
    /// <see cref="Random"/> is not thread-safe.
    /// </param>
    public NacosBackoffPolicy(
        NacosReconnectOptions options,
        TimeProvider timeProvider,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(random);

        _options = options;
        _timeProvider = timeProvider;
        _random = random;
    }

    /// <summary>
    /// Gets a value indicating whether another attempt is permitted after
    /// <paramref name="attemptsMade"/> failures.
    /// </summary>
    /// <param name="attemptsMade">The number of attempts already made.</param>
    /// <returns>
    /// <see langword="true"/> when the attempt budget is unlimited or not yet exhausted.
    /// </returns>
    public bool ShouldRetry(int attemptsMade)
        => _options.MaxAttempts <= 0 || attemptsMade < _options.MaxAttempts;

    /// <summary>
    /// Computes the delay to wait before the next attempt.
    /// </summary>
    /// <param name="attemptsMade">
    /// The number of attempts already made; the first retry is attempt <c>1</c>.
    /// </param>
    /// <returns>The jittered delay, never negative.</returns>
    public TimeSpan GetDelay(int attemptsMade)
    {
        int exponent = Math.Max(0, attemptsMade - 1);

        // Cap the exponent before the multiplication to avoid overflowing on a long outage.
        double multiplier = Math.Pow(_options.Multiplier, Math.Min(exponent, 32));
        double rawTicks = _options.InitialDelay.Ticks * multiplier;

        if (double.IsNaN(rawTicks) || rawTicks <= 0)
        {
            return TimeSpan.Zero;
        }

        double cappedTicks = Math.Min(rawTicks, _options.MaxDelay.Ticks);
        double jitter = _options.JitterFactor;

        if (jitter <= 0)
        {
            return TimeSpan.FromTicks((long)cappedTicks);
        }

        // Jitter is taken from the delay itself, so a delay never extends beyond MaxDelay.
        double jitterFraction = _random.NextDouble() * jitter;
        double jitteredTicks = cappedTicks * (1.0 - jitterFraction);

        return TimeSpan.FromTicks((long)Math.Max(0, jitteredTicks));
    }

    /// <summary>
    /// Waits for the computed backoff delay before the next attempt.
    /// </summary>
    /// <param name="attemptsMade">The number of attempts already made.</param>
    /// <param name="ct">A token that cancels the wait.</param>
    /// <returns>The delay that was awaited.</returns>
    public async Task<TimeSpan> DelayAsync(int attemptsMade, CancellationToken ct)
    {
        TimeSpan delay = GetDelay(attemptsMade);

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);
        }

        return delay;
    }
}
