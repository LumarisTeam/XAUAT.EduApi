using XAUAT.EduApi.Login.Unified;

namespace XAUAT.EduApi.Tests.Login.UnifiedAuth;

/// <summary>
/// 域名白名单是通用统一认证接口的第一道 SSRF 闸门（第二道是
/// <see cref="UnifiedConnectGuard"/> 的地址校验）。这里是"默认只放教育网、
/// 追加只能加不能减"这条契约的守卫。
/// </summary>
public class HostAllowListTests
{
    [Theory]
    [InlineData("swjw.xauat.edu.cn")]
    [InlineData("authserver.xauat.edu.cn")]
    [InlineData("EDU.CN.example.edu.cn")]
    [InlineData("a.b.c.edu.cn")]
    public void ShouldAllowEducationalHostsByDefault(string host)
    {
        Assert.True(HostAllowList.Create(null).IsAllowed(host));
    }

    [Theory]
    [InlineData("edu.cn")]          // 裸后缀本身不是主机（*.edu.cn 要求至少一级子域）
    [InlineData("evil.com")]
    [InlineData("edu.cn.evil.com")] // 前缀里带了 edu.cn 不算数：匹配的是结尾
    [InlineData("xauat.edu.cn.attacker.net")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ShouldRejectEverythingElseByDefault(string? host)
    {
        Assert.False(HostAllowList.Create(null).IsAllowed(host));
    }

    [Fact]
    public void ShouldAppendConfiguredPatterns()
    {
        var allowList = HostAllowList.Create("*.example.com, jwxt.other.org ,");

        Assert.True(allowList.IsAllowed("a.example.com"));
        Assert.True(allowList.IsAllowed("jwxt.other.org"));
        Assert.True(allowList.IsAllowed("anything.edu.cn"), "追加不该挤掉默认项");
        Assert.False(allowList.IsAllowed("a.other.org"));
    }

    [Fact]
    public void ExactPattern_ShouldNotMatchSubdomains()
    {
        var allowList = HostAllowList.Create("jwxt.other.org");

        Assert.True(allowList.IsAllowed("jwxt.other.org"));
        Assert.False(allowList.IsAllowed("sub.jwxt.other.org"));
    }

    [Fact]
    public void ShouldIgnoreCaseAndTrailingDot()
    {
        var allowList = HostAllowList.Create(null);

        Assert.True(allowList.IsAllowed("SWJW.XAUAT.EDU.CN."));
    }

    [Fact]
    public void StarPattern_ShouldOpenEverything_ButBeReported()
    {
        var allowList = HostAllowList.Create("*");

        Assert.True(allowList.AllowAny);
        Assert.True(allowList.IsAllowed("anything.example.com"));
        Assert.True(allowList.IsAllowed("192.168.1.1"));
    }
}
