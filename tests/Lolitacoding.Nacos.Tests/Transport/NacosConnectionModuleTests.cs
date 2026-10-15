using Lolitacoding.Nacos.Transport;

namespace Lolitacoding.Nacos.Tests.Transport;

/// <summary>
/// Verifies the module label values the connection advertises.
/// </summary>
/// <remarks>
/// These strings are read by the server, not by this client, so they are asserted as literals
/// rather than by comparison with the same constants the implementation uses.
/// </remarks>
public sealed class NacosConnectionModuleTests
{
    [Fact]
    public void ToLabel_MatchesTheValuesNacosRoutesOn()
    {
        NacosConnectionModule.Config.ToLabel().ShouldBe("config");
        NacosConnectionModule.Naming.ToLabel().ShouldBe("naming");
        NacosConnectionModule.Lock.ToLabel().ShouldBe("lock");
        NacosConnectionModule.Ai.ToLabel().ShouldBe("ai");
    }

    [Fact]
    public void ToLabel_RejectsAnUnknownModule()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
        {
            _ = ((NacosConnectionModule)42).ToLabel();
        });
    }
}
