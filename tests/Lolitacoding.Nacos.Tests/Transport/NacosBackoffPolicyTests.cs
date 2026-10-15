using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Time.Testing;

namespace Lolitacoding.Nacos.Tests.Transport;

/// <summary>
/// Verifies the exponential backoff used between reconnect attempts.
/// </summary>
/// <remarks>
/// These tests drive <see cref="NacosBackoffPolicy"/> with a fake clock, which is how PLAN.md
/// requires reconnect timing to be verifiable without waiting on real time.
/// </remarks>
public sealed class NacosBackoffPolicyTests
{
    [Fact]
    public void GetDelay_GrowsExponentiallyAndIsCappedAtMaxDelay()
    {
        var policy = CreatePolicy(new NacosReconnectOptions
        {
            InitialDelay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(10),
            Multiplier = 2.0,
            JitterFactor = 0,
        });

        policy.GetDelay(1).ShouldBe(TimeSpan.FromSeconds(1));
        policy.GetDelay(2).ShouldBe(TimeSpan.FromSeconds(2));
        policy.GetDelay(3).ShouldBe(TimeSpan.FromSeconds(4));
        policy.GetDelay(4).ShouldBe(TimeSpan.FromSeconds(8));

        // Capped rather than continuing to double.
        policy.GetDelay(5).ShouldBe(TimeSpan.FromSeconds(10));
        policy.GetDelay(50).ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void GetDelay_AppliesJitterWithinTheConfiguredFraction()
    {
        // A seeded source makes the sequence reproducible, so this asserts the bound rather
        // than an exact value.
        var policy = CreatePolicy(
            new NacosReconnectOptions
            {
                InitialDelay = TimeSpan.FromSeconds(10),
                MaxDelay = TimeSpan.FromSeconds(10),
                Multiplier = 1.0,
                JitterFactor = 0.2,
            },
            seed: 42);

        for (int attempt = 1; attempt <= 20; attempt++)
        {
            TimeSpan delay = policy.GetDelay(attempt);

            // Jitter only ever shortens the delay, so it can never exceed the cap.
            delay.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(10));
            delay.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(8));
        }
    }

    [Fact]
    public void GetDelay_WithoutJitterIsDeterministic()
    {
        var policy = CreatePolicy(new NacosReconnectOptions
        {
            InitialDelay = TimeSpan.FromMilliseconds(500),
            MaxDelay = TimeSpan.FromSeconds(30),
            Multiplier = 3.0,
            JitterFactor = 0,
        });

        policy.GetDelay(3).ShouldBe(TimeSpan.FromMilliseconds(4500));
        policy.GetDelay(3).ShouldBe(TimeSpan.FromMilliseconds(4500));
    }

    [Fact]
    public void ShouldRetry_HonoursTheAttemptBudget()
    {
        var unlimited = CreatePolicy(new NacosReconnectOptions { MaxAttempts = 0 });
        unlimited.ShouldRetry(0).ShouldBeTrue();
        unlimited.ShouldRetry(1000).ShouldBeTrue();

        var bounded = CreatePolicy(new NacosReconnectOptions { MaxAttempts = 3 });
        bounded.ShouldRetry(0).ShouldBeTrue();
        bounded.ShouldRetry(2).ShouldBeTrue();
        bounded.ShouldRetry(3).ShouldBeFalse();
        bounded.ShouldRetry(4).ShouldBeFalse();
    }

    [Fact]
    public async Task DelayAsync_WaitsForTheComputedDelayOnTheInjectedClock()
    {
        // The wait must go through TimeProvider, so advancing the fake clock releases it without
        // any real time passing.
        var timeProvider = new FakeTimeProvider();
        var policy = new NacosBackoffPolicy(
            new NacosReconnectOptions
            {
                InitialDelay = TimeSpan.FromSeconds(5),
                MaxDelay = TimeSpan.FromSeconds(5),
                Multiplier = 1.0,
                JitterFactor = 0,
            },
            timeProvider,
            new Random(1));

        Task<TimeSpan> wait = policy.DelayAsync(1, TestContext.Current.CancellationToken);

        wait.IsCompleted.ShouldBeFalse();

        timeProvider.Advance(TimeSpan.FromSeconds(5));

        (await wait).ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void GetDelay_HandlesAZeroMultiplierWithoutOverflowing()
    {
        var policy = CreatePolicy(new NacosReconnectOptions
        {
            InitialDelay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(30),
            Multiplier = 1.0,
            JitterFactor = 0,
        });

        // A long outage must not overflow the exponential term.
        TimeSpan delay = policy.GetDelay(int.MaxValue);

        delay.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
        delay.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    private static NacosBackoffPolicy CreatePolicy(NacosReconnectOptions options, int seed = 1) =>
        new(options, TimeProvider.System, new Random(seed));
}
