using Lolitacoding.Nacos.Extensions.Configuration;
using Lolitacoding.Nacos.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Tests.Configuration;

/// <summary>
/// Verifies the buffering and replay behaviour of the deferred logger factory.
/// </summary>
public sealed class NacosDeferredLoggerFactoryTests
{
    [Fact]
    public void IsEnabled_BeforeBind_IsTrueForInformationAndAbove()
    {
        var deferred = new NacosDeferredLoggerFactory();
        ILogger logger = deferred.CreateLogger("test");

        logger.IsEnabled(LogLevel.Trace).ShouldBeFalse();
        logger.IsEnabled(LogLevel.Debug).ShouldBeFalse();
        logger.IsEnabled(LogLevel.Information).ShouldBeTrue();
        logger.IsEnabled(LogLevel.Warning).ShouldBeTrue();
        logger.IsEnabled(LogLevel.Error).ShouldBeTrue();
        logger.IsEnabled(LogLevel.None).ShouldBeFalse();
    }

    [Fact]
    public void Log_BeforeBind_KeepsOnlyInformationAndAbove()
    {
        var deferred = new NacosDeferredLoggerFactory();
        ILogger logger = deferred.CreateLogger("test");

        logger.LogDebug("debug before the bind");
        logger.LogInformation("information before the bind");
        logger.LogWarning("warning before the bind");

        var inner = new RecordingLoggerFactory();
        deferred.Bind(inner);

        inner.Entries.Select(entry => entry.Level)
            .ShouldBe([LogLevel.Information, LogLevel.Warning]);
        inner.Entries.Select(entry => entry.Message).ShouldBe(
            ["information before the bind", "warning before the bind"]);
    }

    [Fact]
    public void Log_AfterBind_ForwardsEverything()
    {
        var deferred = new NacosDeferredLoggerFactory();
        var inner = new RecordingLoggerFactory();
        deferred.Bind(inner);

        ILogger logger = deferred.CreateLogger("test");
        logger.LogDebug("debug after the bind");

        inner.Entries.ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Debug);
    }

    [Fact]
    public void Bind_ReplaysTheStructuredStateAndTheEventId()
    {
        var deferred = new NacosDeferredLoggerFactory();
        ILogger logger = deferred.CreateLogger("test");

        logger.LogWarning(new EventId(4242, "Test"), "The value is {Value}.", 42);

        var inner = new RecordingLoggerFactory();
        deferred.Bind(inner);

        RecordedLogEntry entry = inner.Entries.ShouldHaveSingleItem();
        entry.EventId.Id.ShouldBe(4242);
        entry.EventId.Name.ShouldBe("Test");
        entry.Category.ShouldBe("test");
        entry.Message.ShouldBe("The value is 42.");
        entry.State.ShouldContain(new KeyValuePair<string, object?>("Value", 42));
    }

    [Fact]
    public void Bind_ReplaysThroughTheInnerFactoryFilter()
    {
        // The host may be configured to drop information from the library. Replaying through the
        // inner factory, rather than writing straight to a sink, is what makes that filter apply.
        var deferred = new NacosDeferredLoggerFactory();
        ILogger logger = deferred.CreateLogger("test");

        logger.LogInformation("filtered out");
        logger.LogWarning("kept");

        var inner = new RecordingLoggerFactory { MinimumLevel = LogLevel.Warning };
        deferred.Bind(inner);

        inner.Entries.ShouldHaveSingleItem().Message.ShouldBe("kept");
    }

    [Fact]
    public void Buffer_KeepsTheNewestEntriesWhenItOverflows()
    {
        var deferred = new NacosDeferredLoggerFactory();
        ILogger logger = deferred.CreateLogger("test");

        for (int i = 0; i < NacosDeferredLoggerFactory.MaxBufferedEntries + 10; i++)
        {
            logger.LogInformation("message {Index}", i);
        }

        var inner = new RecordingLoggerFactory();
        deferred.Bind(inner);

        inner.Entries.Count.ShouldBe(NacosDeferredLoggerFactory.MaxBufferedEntries);
        inner.Entries[0].Message.ShouldBe("message 10");
        inner.Entries[^1].Message.ShouldBe(
            $"message {NacosDeferredLoggerFactory.MaxBufferedEntries + 9}");
    }

    [Fact]
    public void Bind_IsIgnoredAfterTheFirstBind()
    {
        var deferred = new NacosDeferredLoggerFactory();
        ILogger logger = deferred.CreateLogger("test");
        logger.LogInformation("buffered");

        var first = new RecordingLoggerFactory();
        deferred.Bind(first);

        var second = new RecordingLoggerFactory();
        deferred.Bind(second);

        first.Entries.ShouldHaveSingleItem();
        second.Entries.ShouldBeEmpty();

        logger.LogInformation("after both binds");
        first.Entries.Count.ShouldBe(2);
        second.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void BeginScope_BeforeBind_DoesNotThrow()
    {
        var deferred = new NacosDeferredLoggerFactory();
        ILogger logger = deferred.CreateLogger("test");

        Should.NotThrow(() =>
        {
            using IDisposable? scope = logger.BeginScope("scope");
        });
    }

    [Fact]
    public void Dispose_DoesNotDisposeTheInnerFactory()
    {
        var deferred = new NacosDeferredLoggerFactory();
        var inner = new RecordingLoggerFactory();
        deferred.Bind(inner);

        deferred.Dispose();

        // The container owns the inner factory; disposing it from the source would silence the
        // whole process.
        inner.Disposed.ShouldBeFalse();
    }

    [Fact]
    public void Bind_AfterDispose_DoesNotReplay()
    {
        var deferred = new NacosDeferredLoggerFactory();
        ILogger logger = deferred.CreateLogger("test");
        logger.LogWarning("buffered");

        deferred.Dispose();

        var inner = new RecordingLoggerFactory();
        deferred.Bind(inner);

        inner.Entries.ShouldBeEmpty();
    }
}
