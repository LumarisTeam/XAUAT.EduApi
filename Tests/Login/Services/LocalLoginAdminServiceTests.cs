using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using XAUAT.EduApi.Login;
using XAUAT.EduApi.Tests.Login.TestSupport;

namespace XAUAT.EduApi.Tests.Login.Services;

/// <summary>
/// <see cref="LocalLoginAdminService"/>：进进程内实现之后，管理端读到的数据必须与
/// 走 HTTP 转发 LoginApi 时逐字一致。
/// <para>
/// 这里最要紧的两条是"静默失败"型的——<c>school</c> 忘了转小写会让按学校过滤恒返回空；
/// 降级时把"读不到"说成"没有"。两者都不会抛异常，只会让管理端看到一份看起来正常的错数据。
/// </para>
/// </summary>
public class LocalLoginAdminServiceTests
{
    private const string School = "xauat";
    private const string Username = "alice";
    private const string Account = "xauat:alice";

    private readonly FakeLoginRedisStore _store = new();
    private readonly BanService _banService;
    private readonly LocalLoginAdminService _service;

    /// <summary>
    /// 基于**真实**的 <see cref="LoginRedisStore"/>（未注册多路复用器 ⇒ 降级）搭起来的实例。
    /// <para>
    /// 降级路径刻意不用 <see cref="FakeLoginRedisStore.Available"/>：那个开关只影响
    /// <c>IsAvailable</c>（供 <c>SsoAuthService</c> 的分支用），并不门控各个读写方法；
    /// 而真实实现是**每个方法**都先判 <c>_database is null</c> 再返回默认值。
    /// 要断言"Redis 挂了，管理端读到的是空而不是 500"，就得用真实那层。
    /// </para>
    /// </summary>
    private readonly LocalLoginAdminService _degradedService;

    public LocalLoginAdminServiceTests()
    {
        _banService = new BanService(_store, NullLogger<BanService>.Instance);
        _service = new LocalLoginAdminService(
            _banService, new StatisticsService(_store, NullLogger<StatisticsService>.Instance));

        var offlineStore = new LoginRedisStore([], NullLogger<LoginRedisStore>.Instance);
        _degradedService = new LocalLoginAdminService(
            new BanService(offlineStore, NullLogger<BanService>.Instance),
            new StatisticsService(offlineStore, NullLogger<StatisticsService>.Instance));
    }

    /// <remarks>
    /// 用 <c>ListLeftPushAsync</c> 而不是 <c>SeedList</c>：后者是赋值语义，连调两次只剩最后一条。
    /// LPUSH 才是生产里的写入方式，也天然给出"最新在前"的顺序。
    /// </remarks>
    private Task SeedBanLogAsync(string type, string account, string reason = "连续触发登录限流")
    {
        var entry = type == "auto_ban"
            ? new BanLogEntry
            {
                Type = type,
                CreatedAt = 1_700_000_000,
                Data = new BanInfo
                {
                    Account = account, Reason = reason, BannedAt = 1_700_000_000, UnbanAt = 1_700_086_400
                }
            }
            : new BanLogEntry
            {
                Type = type,
                CreatedAt = 1_700_100_000,
                Account = account,
                Reason = reason,
                Previous = new BanInfo { Account = account, Reason = reason, UnbanAt = 1_700_086_400 }
            };

        return _store.ListLeftPushAsync(
            LoginCacheKeys.BanLogs, JsonSerializer.Serialize(entry, RedisJson.Options));
    }

    // ================================================================ 活跃用户数

    [Fact]
    public async Task GetUserCount_ShouldCountLiveSsoCookieKeys()
    {
        _store.SeedString("sso-cookies-alice-h1", "CASTGC=1", LoginCacheTtl.SsoCookies);
        _store.SeedString("sso-cookies-bob-h2", "CASTGC=2", LoginCacheTtl.SsoCookies);
        // 干扰项：不是 sso-cookies- 前缀，不该被算进去
        _store.SeedString("login-cookies-carol-h3", "__pstsid__=x", LoginCacheTtl.EduCookies);

        var count = await _service.GetUserCountAsync();

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task GetUserCount_ShouldReturnZero_WhenRedisUnavailable()
    {
        // 降级语义：Redis 不可用时这里是 0，而不是抛异常或回 503。
        // 这是 Flask/LoginApi 的既有行为，刻意保留。
        Assert.Equal(0, await _degradedService.GetUserCountAsync());
    }

    // ================================================================ 封禁日志

    [Fact]
    public async Task GetBanLogs_ShouldMapEntriesToPage()
    {
        await SeedBanLogAsync("auto_ban", Account);
        await SeedBanLogAsync("manual_unban", Account, reason: "手动解封");

        var page = await _service.GetBanLogsAsync(null, null);

        Assert.Equal(2, page.Total);
        Assert.Equal(2, page.Items.Count);
        // LPUSH 语义：最新在前
        Assert.Equal("manual_unban", page.Items[0].Type);
    }

    [Fact]
    public async Task GetBanLogs_ShouldNormalizeAutoBanFromDataField()
    {
        await SeedBanLogAsync("auto_ban", Account, reason: "连续触发登录限流");

        var page = await _service.GetBanLogsAsync(null, null);

        var item = Assert.Single(page.Items);
        Assert.Equal("auto_ban", item.Type);
        Assert.Equal("自动封禁", item.TypeLabel);
        Assert.Equal(Account, item.Account);
        Assert.Equal("连续触发登录限流", item.Reason);
        Assert.Equal(1_700_086_400, item.UnbanAt);
    }

    [Fact]
    public async Task GetBanLogs_ShouldNormalizeManualUnbanFromPreviousField()
    {
        // 手动解封条目的 account / unban_at 来自解封前的 previous 快照，
        // 而不是它自己的顶层字段——照抄 Flask 页面的取值顺序。
        await SeedBanLogAsync("manual_unban", Account, reason: "手动解封");

        var page = await _service.GetBanLogsAsync(null, null);

        var item = Assert.Single(page.Items);
        Assert.Equal("manual_unban", item.Type);
        Assert.Equal("手动解封", item.TypeLabel);
        Assert.Equal(Account, item.Account);
        Assert.Equal(1_700_086_400, item.UnbanAt);
    }

    [Fact]
    public async Task GetBanLogs_ShouldLowercaseSchool_SoFilterStillMatches()
    {
        // 封禁键是 security:ban:{school}:{username}，而 ListBanLogsAsync 的前缀匹配
        // 大小写敏感。改造前这一步在 LoginApi 的端点里做（AuthEndpoints 的
        // school.ToLowerInvariant()），LoginOpsController 的 GetBanLogs 并不转小写——
        // 漏掉它不会报错，只会让 ?school=XAUAT 静默返回零条。
        await SeedBanLogAsync("auto_ban", Account);

        var page = await _service.GetBanLogsAsync("XAUAT", null);

        Assert.Equal(1, page.Total);
        Assert.Equal(Account, page.Items[0].Account);
    }

    [Fact]
    public async Task GetBanLogs_ShouldMatchUsernameInBothKeyShapes()
    {
        // 历史条目是三段式（含密码哈希），灰度期间两种形状并存，只认其一会漏判。
        await SeedBanLogAsync("auto_ban", "xauat:alice");
        await SeedBanLogAsync("auto_ban", "xauat:alice:deadbeef");
        await SeedBanLogAsync("auto_ban", "xauat:bob");

        var page = await _service.GetBanLogsAsync(null, Username);

        Assert.Equal(2, page.Total);
    }

    [Fact]
    public async Task GetBanLogs_ShouldReturnEmptyPage_WhenRedisUnavailable()
    {
        Assert.Equal(0, (await _degradedService.GetBanLogsAsync(null, null)).Total);
    }

    // ================================================================ 封禁状态

    [Fact]
    public async Task GetBanStatus_ShouldReportBanned_WhenBanKeyExists()
    {
        _store.SeedString(
            LoginCacheKeys.Ban(LoginCacheKeys.AccountKey(School, Username)),
            JsonSerializer.Serialize(
                new BanInfo { Account = Account, Reason = "连续触发登录限流", BannedAt = 1, UnbanAt = 2 },
                RedisJson.Options),
            LoginCacheTtl.Ban);

        var status = await _service.GetBanStatusAsync(School, Username);

        Assert.True(status.Banned);
        var item = Assert.Single(status.Items);
        Assert.Equal(Account, item.Account);
        Assert.Equal(LoginCacheKeys.Ban(LoginCacheKeys.AccountKey(School, Username)), item.Key);
    }

    [Fact]
    public async Task GetBanStatus_ShouldReportNotBanned_WhenNothingSeeded()
    {
        var status = await _service.GetBanStatusAsync(School, Username);

        Assert.False(status.Banned);
        Assert.Empty(status.Items);
    }

    // ================================================================ 解封

    [Fact]
    public async Task Unban_ShouldReportSuccess_WhenBanRemoved()
    {
        _store.SeedString(
            LoginCacheKeys.Ban(LoginCacheKeys.AccountKey(School, Username)),
            JsonSerializer.Serialize(
                new BanInfo { Account = Account, Reason = "连续触发登录限流", BannedAt = 1, UnbanAt = 2 },
                RedisJson.Options),
            LoginCacheTtl.Ban);

        var result = await _service.UnbanAsync(School, Username);

        Assert.True(result.Success);
        Assert.Equal("用户已解封", result.Message);
    }

    [Fact]
    public async Task Unban_ShouldReportFailure_WhenNothingToUnban()
    {
        // 「当前未被封禁」是业务结果而不是错误，文案逐字沿用 LoginApi——
        // LoginOpsController 据此回 400 而不是 500。
        var result = await _service.UnbanAsync(School, Username);

        Assert.False(result.Success);
        Assert.Equal("用户当前未被封禁或解封失败", result.Message);
    }

    [Fact]
    public async Task Unban_ShouldLeaveABanLogTrail()
    {
        _store.SeedString(
            LoginCacheKeys.Ban(LoginCacheKeys.AccountKey(School, Username)),
            JsonSerializer.Serialize(
                new BanInfo { Account = Account, Reason = "连续触发登录限流", BannedAt = 1, UnbanAt = 2 },
                RedisJson.Options),
            LoginCacheTtl.Ban);

        await _service.UnbanAsync(School, Username);
        var page = await _service.GetBanLogsAsync(null, Username);

        var item = Assert.Single(page.Items);
        Assert.Equal("manual_unban", item.Type);
    }
}
