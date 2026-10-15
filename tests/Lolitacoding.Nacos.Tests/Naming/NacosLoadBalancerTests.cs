using System.Reflection;
using Lolitacoding.Nacos.Naming;

namespace Lolitacoding.Nacos.Tests.Naming;

/// <summary>Verifies selection boundaries without statistical distribution assertions.</summary>
public sealed class NacosLoadBalancerTests
{
    [Fact]
    public void Select_EmptyList_ReturnsNullForEveryStrategy()
    {
        var random = new FixedRandom(0);
        INacosLoadBalancer[] strategies =
        [
            new NacosRoundRobinLoadBalancer(),
            new NacosWeightedRandomLoadBalancer(random),
            new NacosClusterAwareLoadBalancer("local"),
        ];

        foreach (INacosLoadBalancer strategy in strategies)
        {
            strategy.Select([]).ShouldBeNull();
        }

        random.Calls.ShouldBe(0);
    }

    [Fact]
    public void Select_NullList_ThrowsForEveryStrategy()
    {
        INacosLoadBalancer[] strategies =
        [
            new NacosRoundRobinLoadBalancer(),
            new NacosWeightedRandomLoadBalancer(),
            new NacosClusterAwareLoadBalancer("local"),
        ];

        foreach (INacosLoadBalancer strategy in strategies)
        {
            Should.Throw<ArgumentNullException>(() => strategy.Select(null!));
        }
    }

    [Fact]
    public void RoundRobin_RotatesAndUsesTheCurrentList()
    {
        var strategy = new NacosRoundRobinLoadBalancer();
        NacosInstance[] instances = [Instance(1), Instance(2), Instance(3)];

        for (int turn = 0; turn < 7; turn++)
        {
            strategy.Select(instances).ShouldBeSameAs(instances[turn % instances.Length]);
        }

        strategy.Select([]).ShouldBeNull();
        NacosInstance[] changed = [Instance(4), Instance(5)];
        strategy.Select(changed).ShouldBeSameAs(changed[1]);
        strategy.Select(changed).ShouldBeSameAs(changed[0]);
    }

    [Theory]
    [InlineData((long)int.MaxValue - 1)]
    [InlineData((long)int.MaxValue)]
    [InlineData((long)int.MaxValue + 1)]
    [InlineData(long.MaxValue - 1)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void RoundRobin_CounterBoundary_AlwaysProducesTheExpectedIndex(long initialCounter)
    {
        var strategy = new NacosRoundRobinLoadBalancer();
        NacosInstance[] instances = [Instance(1), Instance(2), Instance(3)];

        // Reach the regression in constant time without exposing a production test-only API.
        FieldInfo counter = typeof(NacosRoundRobinLoadBalancer)
            .GetField("_counter", BindingFlags.Instance | BindingFlags.NonPublic).ShouldNotBeNull();
        counter.SetValue(strategy, initialCounter);

        for (int offset = 0; offset < 6; offset++)
        {
            long turn = unchecked(initialCounter + offset);
            int expected = (int)((turn & long.MaxValue) % instances.Length);
            strategy.Select(instances).ShouldBeSameAs(instances[expected]);
        }
    }

    [Fact]
    public void RoundRobin_ConcurrentCalls_AllocateEveryTurnOnce()
    {
        var strategy = new NacosRoundRobinLoadBalancer();
        NacosInstance[] instances = [Instance(1), Instance(2), Instance(3)];
        int[] counts = new int[instances.Length];

        Parallel.For(0, 300, new ParallelOptions
        {
            CancellationToken = TestContext.Current.CancellationToken,
        }, _ =>
        {
            NacosInstance selected = strategy.Select(instances).ShouldNotBeNull();
            Interlocked.Increment(ref counts[selected.Port - 1]);
        });

        counts.ShouldBe([100, 100, 100]);
    }

    [Fact]
    public void ClusterAware_PrefersExactClusterAndKeepsIndependentFallbackRotation()
    {
        var strategy = new NacosClusterAwareLoadBalancer("local");
        NacosInstance first = Instance(1, cluster: "local");
        NacosInstance second = Instance(2, cluster: "local");
        NacosInstance remote = Instance(3, cluster: "remote");
        NacosInstance differentCase = Instance(4, cluster: "LOCAL");
        NacosInstance[] all = [remote, first, differentCase, second];
        NacosInstance[] fallback = [remote, differentCase];

        strategy.Select(all).ShouldBeSameAs(first);
        strategy.Select(fallback).ShouldBeSameAs(remote);
        strategy.Select(all).ShouldBeSameAs(second);
        strategy.Select(fallback).ShouldBeSameAs(differentCase);
        strategy.Select(all).ShouldBeSameAs(first);
        strategy.Select([]).ShouldBeNull();
    }

    [Fact]
    public void WeightedRandom_SingleInstance_DoesNotDrawRandomness()
    {
        var random = new FixedRandom(0);
        var strategy = new NacosWeightedRandomLoadBalancer(random);
        NacosInstance instance = Instance(1, double.MaxValue);

        strategy.Select([instance]).ShouldBeSameAs(instance);
        random.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.24999999999999997, 0)]
    [InlineData(0.25, 1)]
    [InlineData(0.49999999999999994, 1)]
    [InlineData(0.5, 2)]
    [InlineData(0.9999999999999999, 2)]
    public void WeightedRandom_CumulativeBoundary_UsesHalfOpenIntervals(double sample, int expected)
    {
        var random = new FixedRandom(sample);
        var strategy = new NacosWeightedRandomLoadBalancer(random);
        NacosInstance[] instances = [Instance(1), Instance(2), Instance(3, 2)];

        strategy.Select(instances).ShouldBeSameAs(instances[expected]);
        random.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.49999999999999994, 0)]
    [InlineData(0.5, 1)]
    [InlineData(0.75, 2)]
    [InlineData(0.9999999999999999, 2)]
    public void WeightedRandom_FiniteWeightsWhoseSumOverflows_PreserveTheirRatios(double sample, int expected)
    {
        var strategy = new NacosWeightedRandomLoadBalancer(new FixedRandom(sample));
        NacosInstance[] instances =
        [
            Instance(1, double.MaxValue),
            Instance(2, double.MaxValue / 2),
            Instance(3, double.MaxValue / 2),
        ];

        strategy.Select(instances).ShouldBeSameAs(instances[expected]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.25, 1)]
    [InlineData(0.5, 2)]
    [InlineData(0.9999999999999999, 2)]
    public void WeightedRandom_SubnormalWeights_PreserveTheirRatios(double sample, int expected)
    {
        var strategy = new NacosWeightedRandomLoadBalancer(new FixedRandom(sample));
        NacosInstance[] instances =
        [
            Instance(1, double.Epsilon),
            Instance(2, double.Epsilon),
            Instance(3, 2 * double.Epsilon),
        ];

        strategy.Select(instances).ShouldBeSameAs(instances[expected]);
    }

    [Fact]
    public void WeightedRandom_UsesTheCurrentList()
    {
        var strategy = new NacosWeightedRandomLoadBalancer(new FixedRandom(0.5));
        NacosInstance[] first = [Instance(1, 3), Instance(2)];
        NacosInstance[] changed = [Instance(3), Instance(4, 3)];

        strategy.Select(first).ShouldBeSameAs(first[0]);
        strategy.Select(changed).ShouldBeSameAs(changed[1]);
    }

    [Fact]
    public void WeightedRandom_ConcurrentCalls_SerializeTheInjectedRandomSource()
    {
        var random = new AlternatingRandom();
        var strategy = new NacosWeightedRandomLoadBalancer(random);
        NacosInstance[] instances = [Instance(1), Instance(2)];
        int[] counts = new int[instances.Length];

        Parallel.For(0, 256, new ParallelOptions
        {
            CancellationToken = TestContext.Current.CancellationToken,
            MaxDegreeOfParallelism = 8,
        }, _ =>
        {
            NacosInstance selected = strategy.Select(instances).ShouldNotBeNull();
            Interlocked.Increment(ref counts[selected.Port - 1]);
        });

        // Assert exact scripted outcomes, not probabilities or thread scheduling order.
        counts.ShouldBe([128, 128]);
        random.Calls.ShouldBe(256);
        random.ConcurrentAccess.ShouldBeFalse();
    }

    private static NacosInstance Instance(int port, double weight = 1, string? cluster = null) =>
        new("127.0.0.1", port, weight, cluster);

    private sealed class FixedRandom(double sample) : Random
    {
        public int Calls { get; private set; }

        public override double NextDouble()
        {
            Calls++;
            return sample;
        }
    }

    private sealed class AlternatingRandom : Random
    {
        private int _active;
        private int _concurrentAccess;

        public int Calls { get; private set; }

        public bool ConcurrentAccess => Volatile.Read(ref _concurrentAccess) != 0;

        public override double NextDouble()
        {
            if (Interlocked.Increment(ref _active) != 1)
            {
                Interlocked.Exchange(ref _concurrentAccess, 1);
            }

            try
            {
                // Deliberately not thread-safe: synchronization is the balancer's responsibility.
                int turn = Calls;
                Thread.SpinWait(1000);
                Calls = turn + 1;
                return (turn & 1) == 0 ? 0.25 : 0.75;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
