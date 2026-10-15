using Lolitacoding.Nacos.Extensions.Configuration;
using Microsoft.Extensions.Configuration;

namespace Lolitacoding.Nacos.Tests.Configuration;

/// <summary>
/// Verifies the failover behaviour of the Nacos configuration provider.
/// </summary>
/// <remarks>
/// These exercise the paths that do not need a live server. Port 1 is used because nothing can
/// legitimately be listening on it, which makes the connection failure immediate and
/// deterministic rather than a timeout.
/// </remarks>
public sealed class NacosConfigurationProviderTests
{
    /// <summary>A server address that is guaranteed to refuse a connection.</summary>
    private const string Unreachable = "http://127.0.0.1:1";

    [Fact]
    public void Load_Optional_DoesNotThrowWhenTheServerIsUnreachable()
    {
        var options = new NacosConfigurationSourceOptions
        {
            ServerAddresses = [Unreachable],
            DataIds = ["app.json"],
            Optional = true,
        };

        var provider = new NacosConfigurationProvider(options);

        try
        {
            // The whole point of Optional is that a control-plane outage does not stop the process
            // from starting.
            Should.NotThrow(provider.Load);
            provider.TryGet("app.json", out string? _).ShouldBeFalse();
        }
        finally
        {
            ((IDisposable)provider).Dispose();
        }
    }

    [Fact]
    public void Load_WithoutOptional_ThrowsWhenTheServerIsUnreachable()
    {
        var options = new NacosConfigurationSourceOptions
        {
            ServerAddresses = [Unreachable],
            DataIds = ["app.json"],
        };

        var provider = new NacosConfigurationProvider(options);

        try
        {
            Should.Throw<NacosConnectionException>(provider.Load);
        }
        finally
        {
            ((IDisposable)provider).Dispose();
        }
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var options = new NacosConfigurationSourceOptions
        {
            ServerAddresses = [Unreachable],
            DataIds = ["app.json"],
            Optional = true,
        };

        var provider = new NacosConfigurationProvider(options);
        provider.Load();

        var disposable = (IDisposable)provider;

        Should.NotThrow(disposable.Dispose);
        Should.NotThrow(disposable.Dispose);
    }

    [Fact]
    public void Load_AfterDispose_IsANoOp()
    {
        var options = new NacosConfigurationSourceOptions
        {
            ServerAddresses = [Unreachable],
            DataIds = ["app.json"],
            Optional = true,
        };

        var provider = new NacosConfigurationProvider(options);
        ((IDisposable)provider).Dispose();

        // A provider can be disposed before it is ever asked to load if the host is torn down
        // early; that must not throw.
        Should.NotThrow(provider.Load);
    }
}
