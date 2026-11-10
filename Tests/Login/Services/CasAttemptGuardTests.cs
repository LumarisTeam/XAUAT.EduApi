using XAUAT.EduApi.Login;
using XAUAT.EduApi.Tests.Login.TestSupport;

namespace XAUAT.EduApi.Tests.Login.Services;

public class CasAttemptGuardTests
{
    [Fact]
    public async Task SharedStore_LimitsSameAccountAcrossInstancesAndNormalizesHost()
    {
        var store = new FakeLoginRedisStore();
        var first = new CasAttemptGuard(store);
        var second = new CasAttemptGuard(store);
        for (var i = 0; i < CasAttemptGuard.MaxAttempts; i++)
            Assert.Null(await first.AcquireAsync(CasAttemptGuard.XauatHost, "Alice"));

        Assert.InRange((await second.AcquireAsync("AUTHSERVER.XAUAT.EDU.CN", "alice"))!.Value, 1, CasAttemptGuard.WindowSeconds);
        Assert.Null(await second.AcquireAsync("other.edu.cn", "alice"));
        Assert.Null(await second.AcquireAsync(CasAttemptGuard.XauatHost, "bob"));
    }

    [Fact]
    public async Task RedisUnavailable_StillLimitsLocally()
    {
        var guard = new CasAttemptGuard(new FakeLoginRedisStore { Available = false });
        for (var i = 0; i < CasAttemptGuard.MaxAttempts; i++)
            Assert.Null(await guard.AcquireAsync(CasAttemptGuard.XauatHost, "alice"));
        Assert.NotNull(await guard.AcquireAsync(CasAttemptGuard.XauatHost, "alice"));
    }
}
