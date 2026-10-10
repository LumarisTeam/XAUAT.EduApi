using XAUAT.EduApi.Interfaces;

namespace XAUAT.EduApi.Login;

/// <summary>
/// <see cref="ILoginAdminService"/> 的进程内实现：直接读本进程持有的 Redis。
/// <para>
/// 取代原先的 <c>HttpLoginAdminService</c>（它把管理端的运维查询转给 XAUAT.LoginApi）。
/// 这条代理存在的唯一理由是"lumaris_admin 部署在别的主机，够不到只在 <c>xauat-net</c>
/// 内暴露的登录服务"——登录逻辑搬进来之后，这层理由自然消失。
/// </para>
/// <para>
/// 失败语义随之简化：Redis 不可用时 <c>LoginRedisStore</c> 降级为空操作，
/// 于是用户数得 0、封禁日志得空列表，而<b>不是</b>原先那个 503。
/// 这符合降级路径的一贯目标——"看起来像没有数据"比"整个端点挂掉"更接近真实情况
/// （确实没有可读的封禁状态）。
/// </para>
/// </summary>
internal sealed class LocalLoginAdminService(BanService banService, StatisticsService statisticsService)
    : ILoginAdminService
{
    /// <summary>
    /// 封禁日志返回条数上限。沿用 Flask 页面读 <c>LRANGE 0 200</c>（201 条）的口径——
    /// 上游固定只给最近 200 条，因此端点不分页。
    /// </summary>
    private const long BanLogLimit = 200;

    public Task<int> GetUserCountAsync() => statisticsService.GetUserCountAsync();

    /// <remarks>
    /// <b>这里必须自己把 school 转小写</b>：封禁键是 <c>security:ban:{school}:{username}</c>，
    /// 而 <c>BanService.ListBanLogsAsync</c> 的前缀匹配是大小写敏感的
    /// （<c>item.Account.StartsWith($"{school}:")</c>）。改造前这一步在 LoginApi 的端点里
    /// （<c>school.ToLowerInvariant()</c>），<c>LoginOpsController</c> 的 <c>GetBanLogs</c>
    /// 并不转小写——漏掉它不会报错，只会让 <c>?school=XAUAT</c> 静默返回零条。
    /// </remarks>
    public async Task<BanLogPage> GetBanLogsAsync(string? school, string? username)
    {
        var logs = await banService.ListBanLogsAsync(school?.ToLowerInvariant(), username, BanLogLimit);
        return new BanLogPage { Total = logs.Count, Items = logs };
    }

    public async Task<BanStatusResult> GetBanStatusAsync(string school, string username)
    {
        var bans = await banService.FindActiveBansAsync(school, username);
        return new BanStatusResult { Banned = bans.Count > 0, Items = bans };
    }

    /// <remarks>
    /// 两条文案逐字沿用 LoginApi 的 <c>POST /security/ban_status</c>——它们是**业务结果**
    /// 而不是错误，控制器据此把"当前未被封禁"回成 400。
    /// </remarks>
    public async Task<UnbanResult> UnbanAsync(string school, string username)
    {
        var removed = await banService.UnbanByUsernameAsync(school, username);

        return removed
            ? new UnbanResult { Success = true, Message = "用户已解封" }
            : new UnbanResult { Success = false, Message = "用户当前未被封禁或解封失败" };
    }
}
