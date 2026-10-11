using System.Net;
using XAUAT.EduApi.Login.Unified;

namespace XAUAT.EduApi.Tests.Login.UnifiedAuth;

/// <summary>
/// 建连前的地址校验。<b>这是防 SSRF 的第二道闸，也是最容易被"优化掉"的一道</b>：
/// 白名单只认域名，合法域名被解析到内网（DNS rebinding / 内网 DNS 污染）时，
/// 只有这里能拦住。
/// </summary>
public class UnifiedConnectGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]         // 环回
    [InlineData("127.1.2.3")]         // 环回段
    [InlineData("10.0.0.5")]          // 私有
    [InlineData("172.16.0.1")]        // 私有
    [InlineData("172.31.255.254")]    // 私有上边界
    [InlineData("192.168.1.1")]       // 私有
    [InlineData("169.254.169.254")]   // 云元数据（SSRF 的头号目标）
    [InlineData("100.64.0.1")]        // CGNAT
    [InlineData("0.0.0.0")]           // 本网络
    [InlineData("224.0.0.1")]         // 多播
    [InlineData("240.0.0.1")]         // 保留
    [InlineData("198.18.0.1")]        // 基准测试段
    [InlineData("::1")]               // IPv6 环回
    [InlineData("fc00::1")]           // IPv6 唯一本地
    [InlineData("fe80::1")]           // IPv6 链路本地
    [InlineData("::ffff:127.0.0.1")]  // IPv4-mapped 环回
    [InlineData("::ffff:10.0.0.1")]   // IPv4-mapped 私有
    public void ShouldBlockInternalAndReservedAddresses(string address)
    {
        Assert.True(UnifiedConnectGuard.IsBlocked(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("202.117.16.1")]      // 公网段
    [InlineData("172.32.0.1")]        // 172.16/12 之外
    [InlineData("2001:4860:4860::8888")]
    public void ShouldAllowPublicAddresses(string address)
    {
        Assert.False(UnifiedConnectGuard.IsBlocked(IPAddress.Parse(address)));
    }

    /// <summary>
    /// 把闸门挂到真实的 <see cref="SocketsHttpHandler"/> 上跑一次，验证它确实在连接层生效——
    /// <see cref="IsBlocked"/> 单测证明不了"它真的被接进了管道"。
    /// </summary>
    [Fact]
    public async Task ConnectCallback_ShouldRefuseLoopbackTarget()
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = UnifiedConnectGuard.ConnectAsync
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => client.GetAsync("http://127.0.0.1:1/"));

        var blocked = exception as BlockedTargetException ?? exception.InnerException as BlockedTargetException;
        Assert.NotNull(blocked);
    }
}
