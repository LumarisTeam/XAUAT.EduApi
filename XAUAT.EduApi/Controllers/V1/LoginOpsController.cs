using EduApi.Data.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Controllers.V1;

/// <summary>
/// 登录服务的运维/统计端点：活跃用户数、封禁日志、封禁状态、解封。
/// <para>
/// 数据来自本进程直接读的 Redis——这些数据原先在独立的 XAUAT.LoginApi 里，
/// 由 <c>HttpLoginAdminService</c> 转发；登录逻辑搬进 EduApi 之后，
/// 那个"管理端够不到内网登录服务"的理由随之消失，代理也就撤了。
/// 键名与值编码仍是与 Flask 共用的那套（见 <c>Login.LoginCacheKeys</c>）。
/// </para>
/// <para>
/// <b>全部端点都要管理员 Token</b>（<see cref="IMapAdminTokenService"/>，环境变量
/// <c>MAP_ADMIN_TOKEN</c>）。封禁日志里含学号等个人信息，不能像地图查询那样开放；
/// 注意该服务在 Token 为空时 **恒返回 false**，即没配 Token 时这些端点一律 401。
/// </para>
/// <para>
/// 不使用 <c>EduCrawlerRateLimitFilter</c>：那是学号维度的教务系统封禁/冷却机制，
/// 管理端点没有学生身份，挂上去无意义。只保留 <c>EduCrawler</c> 并发上限
/// （<see cref="CalendarController"/> 是同一个取舍的先例）。
/// </para>
/// </summary>
[ApiController]
[Route("v1/login-ops")]
[Produces("application/json")]
[EnableRateLimiting("EduCrawler")]
public class LoginOpsController(
    ILoginAdminService loginAdminService,
    ILogger<LoginOpsController> logger,
    IMapAdminTokenService adminTokenService,
    ILanguageResolver languageResolver,
    IApiMessageLocalizer messageLocalizer)
    : V1ControllerBase(languageResolver, messageLocalizer)
{
    /// <summary>
    /// 活跃用户数。口径是存活的 <c>sso-cookies-*</c> 键数量，<b>不按人去重</b>——
    /// 同一学生换一次密码就会多算一个。这是 Flask 的既有定义。
    /// </summary>
    [HttpGet("user-count")]
    [ProducesResponseType(typeof(ApiResponse<LoginUserCount>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse<LoginUserCount>>> GetUserCount()
    {
        var unauthorized = EnsureAuthorized();
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        try
        {
            var count = await loginAdminService.GetUserCountAsync();
            return Ok(SuccessResponse(new LoginUserCount { Count = count }));
        }
        catch (Exception ex)
        {
            return ServerError(ex, "获取活跃用户数");
        }
    }

    /// <summary>
    /// 封禁日志（最新在前）。固定只返回最近 200 条，因此不分页。
    /// </summary>
    /// <param name="school">按学校过滤（前缀匹配，服务层会转小写）。留空表示不过滤。</param>
    /// <param name="username">按用户名过滤。历史条目是 <c>学校:用户名:密码哈希</c> 三段式，
    /// 服务层对两种形状都做了兼容。</param>
    [HttpGet("ban-logs")]
    [ProducesResponseType(typeof(ApiResponse<BanLogPage>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse<BanLogPage>>> GetBanLogs(
        [FromQuery] string? school = null,
        [FromQuery] string? username = null)
    {
        var unauthorized = EnsureAuthorized();
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        try
        {
            var page = await loginAdminService.GetBanLogsAsync(school, username);
            return Ok(SuccessResponse(page));
        }
        catch (Exception ex)
        {
            return ServerError(ex, "获取封禁日志");
        }
    }

    /// <summary>
    /// 查询某账号当前是否处于封禁中，以及封禁记录的明细。
    /// <para>
    /// 这是迁移时**唯一新增**的端点：它接管的是 LoginApi 那条没搬过来的
    /// <c>GET /security/ban_status</c>。封禁日志回答"发生过什么"，本端点回答"现在是否仍然生效"。
    /// </para>
    /// <para>
    /// 学校缺省 <c>xauat</c> 并一律转小写（封禁键就是这么拼的）；用户名必填。
    /// </para>
    /// </summary>
    [HttpGet("ban-status")]
    [ProducesResponseType(typeof(ApiResponse<BanStatusResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse<BanStatusResult>>> GetBanStatus(
        [FromQuery] string? school = null,
        [FromQuery] string? username = null)
    {
        var unauthorized = EnsureAuthorized();
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            return BadRequest(ErrorResponse(ApiCodes.ParamError, Message(ApiMessageKey.LoginOpsUsernameRequired)));
        }

        try
        {
            var schoolKey = string.IsNullOrWhiteSpace(school) ? "xauat" : school.ToLowerInvariant();
            var result = await loginAdminService.GetBanStatusAsync(schoolKey, username);
            return Ok(SuccessResponse(result));
        }
        catch (Exception ex)
        {
            return ServerError(ex, "查询封禁状态");
        }
    }

    /// <summary>
    /// 手动解封。
    /// <para>
    /// 学校/用户名由管理端从日志条目的 <c>account</c> 字段还原
    /// （<c>{school}:{username}</c> 或 <c>{school}:{username}:{hash}</c>，两种都取前两段）。
    /// </para>
    /// <para>
    /// 「用户当前未被封禁」回 <b>400</b> 并带上文案，而不是 500——那是业务结果。
    /// </para>
    /// </summary>
    [HttpPost("unban")]
    [ProducesResponseType(typeof(ApiResponse<UnbanResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse<UnbanResult>>> Unban([FromBody] UnbanRequest request)
    {
        var unauthorized = EnsureAuthorized();
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return BadRequest(ErrorResponse(ApiCodes.ParamError, Message(ApiMessageKey.LoginOpsUsernameRequired)));
        }

        // 与封禁键的拼法一致：学校缺省 xauat，且一律转小写
        var school = string.IsNullOrWhiteSpace(request.School) ? "xauat" : request.School.ToLowerInvariant();

        try
        {
            var result = await loginAdminService.UnbanAsync(school, request.Username);

            if (!result.Success)
            {
                // 「用户当前未被封禁或解封失败」原样透出，比本地化文案更有信息量
                return BadRequest(ErrorResponse(ApiCodes.ParamError, result.Message));
            }

            // 写操作留痕：解封会直接改变谁能登录，值得一条可检索的日志
            logger.LogWarning("管理员解封账号 {School}:{Username}", school, request.Username);

            return Ok(SuccessResponse(result));
        }
        catch (Exception ex)
        {
            return ServerError(ex, "解封账号");
        }
    }

    /// <summary>
    /// 令牌校验。返回 null 表示放行。
    /// <para>
    /// 返回类型刻意是 <see cref="ObjectResult"/>——它同时是
    /// <c>Unauthorized(...)</c> 与 <c>StatusCode(...)</c> 的返回类型，且能隐式转换到
    /// <c>ActionResult&lt;ApiResponse&lt;T&gt;&gt;</c>（该转换只接受 <c>ActionResult</c>，
    /// 声明成 <c>IActionResult</c> 反而转不过去）。
    /// </para>
    /// </summary>
    private ObjectResult? EnsureAuthorized()
    {
        if (adminTokenService.IsAuthorized(Request))
        {
            return null;
        }

        return Unauthorized(ErrorResponse(
            StatusCodes.Status401Unauthorized, Message(ApiMessageKey.LoginOpsTokenRequired)));
    }

    /// <summary>
    /// 把服务层异常翻译成响应。
    /// <para>
    /// 只剩一个分支：数据源是本地 Redis，没有"上游不可达"或"没配地址"这两种情况了
    /// （<c>LoginRedisStore</c> 在 Redis 挂掉时降级为空操作，不抛异常），
    /// 因此原先的 503 两分支连同那两个异常类型一并删除。
    /// </para>
    /// </summary>
    private ObjectResult ServerError(Exception exception, string operation)
    {
        logger.LogError(exception, "{Operation}失败", operation);
        return StatusCode(StatusCodes.Status500InternalServerError,
            ErrorResponse(ApiCodes.InternalError, Message(ApiMessageKey.InternalServerError)));
    }
}

/// <summary><c>POST v1/login-ops/unban</c> 的请求体。</summary>
public sealed record UnbanRequest
{
    public string? School { get; init; }

    public string Username { get; init; } = "";
}
