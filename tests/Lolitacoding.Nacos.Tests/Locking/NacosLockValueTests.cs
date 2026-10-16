using Lolitacoding.Nacos.Locking;

namespace Lolitacoding.Nacos.Tests.Locking;

/// <summary>
/// Verifies the lock value objects: the wire values the server matches on, the key's validation,
/// and how a result reports the queue position it carries.
/// </summary>
public sealed class NacosLockValueTests
{
    [Fact]
    public void LockTypes_UseTheValuesTheServerMatchesOn()
    {
        // These strings are the server's LockConstants. A typo would make every lock request fail
        // with a message about an unknown lock type, so they are pinned here.
        NacosLockTypes.Mutex.ShouldBe("NACOS_LOCK");
        NacosLockTypes.Reentrant.ShouldBe("REENTRANT");
        NacosLockTypes.NonReentrant.ShouldBe("NON_REENTRANT");
    }

    [Fact]
    public void Key_DefaultsToTheMutexType()
    {
        var key = new NacosLockKey("order-42");

        key.Name.ShouldBe("order-42");
        key.LockType.ShouldBe(NacosLockTypes.Mutex);
    }

    [Fact]
    public void Key_RejectsAnEmptyNameOrType()
    {
        Should.Throw<ArgumentException>(() => new NacosLockKey(" "));
        Should.Throw<ArgumentException>(() => new NacosLockKey("order-42", " "));
    }

    [Fact]
    public void Key_IsAValueRatherThanAnIdentity()
    {
        // Two keys with the same name and type address the same lock, so a caller can build one per
        // call without having to keep them.
        new NacosLockKey("order-42").ShouldBe(new NacosLockKey("order-42"));
        new NacosLockKey("order-42").ShouldNotBe(new NacosLockKey("order-43"));
        new NacosLockKey("order-42").ShouldNotBe(new NacosLockKey("order-42", NacosLockTypes.Reentrant));
    }

    [Fact]
    public void Key_DescribesItselfWithTheTypeFirst()
    {
        // The type is part of the identity, so leaving it out of a diagnostic would make two
        // different locks look like one.
        new NacosLockKey("order-42", NacosLockTypes.Reentrant).ToString()
            .ShouldBe("REENTRANT/order-42");
    }

    [Fact]
    public void Result_ReportsWaitingFromTheQueuePosition()
    {
        // The server models "waiting" as a non-negative queue position and sends no separate flag,
        // so the position is the only thing that can be read.
        new NacosLockResult(false, 0, 2, null).Waiting.ShouldBeTrue();
        new NacosLockResult(false, 0, -1, "Lock is held by another owner").Waiting.ShouldBeFalse();
        new NacosLockResult(true, 1, -1, null).Waiting.ShouldBeFalse();
    }

    [Fact]
    public void Result_DescribesItself()
    {
        new NacosLockResult(true, 2, -1, null).ToString().ShouldBe("Locked (count 2)");
        new NacosLockResult(false, 0, 3, null).ToString().ShouldBe("Waiting at position 3");
        new NacosLockResult(false, 0, -1, "nope").ToString().ShouldBe("Not locked: nope");
        new NacosLockResult(false, 0, -1, null).ToString().ShouldBe("Not locked: no reason reported");
    }
}
