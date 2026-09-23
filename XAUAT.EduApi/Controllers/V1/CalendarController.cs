using System.Globalization;
using EduApi.Data.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Localization;

namespace XAUAT.EduApi.Controllers.V1;

/// <summary>
/// 日历订阅端点。生成 ICS（iCalendar）正文供日历客户端订阅。
/// <para>
/// <b>为什么单独一个控制器而不是塞进 <see cref="CourseController"/></b>：那条路由前缀
/// 上的控制器标了 <c>[Produces("application/json")]</c>，而这个端点返回的是
/// <c>text/calendar</c>，混在一起会让 OpenAPI 元数据自相矛盾。路由前缀共用没关系，
/// action 路由不冲突即可。
/// </para>
/// <para>
/// <b>跨服务契约</b>：这个端点原来是 302 到 <c>schedule.xauat.site/class</c>
/// （LoginApi/Flask 的实现）。参数名 <c>username</c>/<c>password</c>（以及历史别名
/// <c>passwd</c>）与 <c>Content-Disposition</c> 都是老客户端在用的形状，改动会让
/// 已订阅的日历静默失效。日历客户端拉取的是 URL 本体，不带 cookie，所以凭据只能走
/// 查询串——这与其它需要 <c>xauat</c> 头的端点不同。
/// </para>
/// </summary>
[ApiController]
[Route("v1/course")]
[Produces("text/calendar")]
[EnableRateLimiting("EduCrawler")]
public class CalendarController(
    ICalendarService calendarService,
    ILogger<CalendarController> logger,
    ILanguageResolver languageResolver,
    IApiMessageLocalizer messageLocalizer)
    : V1ControllerBase(languageResolver, messageLocalizer)
{
    /// <summary>
    /// 生成并返回 ICS 日历。
    /// </summary>
    /// <param name="username">学号。</param>
    /// <param name="password">教务密码。</param>
    /// <param name="passwd">密码的历史别名（老客户端用过），仅在 <paramref name="password"/> 为空时生效。</param>
    /// <param name="filter">传 <c>future</c> 时只返回结束时间在当下之后的事件。</param>
    /// <param name="type">历史参数（<c>webcal</c>/<c>https</c>），用于选拼接 scheme；现在直接返回正文，已忽略。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">ICS 正文</response>
    /// <response code="400">缺少用户名或密码</response>
    /// <response code="401">登录失败或上游认证失效</response>
    /// <response code="429">账号封禁或教务系统限流</response>
    /// <response code="500">其它错误</response>
    [HttpGet("Calendar")]
    [Produces("text/calendar")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetCalendar(
        string? username,
        string? password,
        string? passwd,
        string? filter,
        string? type,
        CancellationToken cancellationToken)
    {
        // Flask 起两个参数名就都被老客户端用过，password 为空时回退到 passwd
        var effectivePassword = string.IsNullOrEmpty(password) ? passwd : password;

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(effectivePassword))
        {
            return CalendarError(StatusCodes.Status400BadRequest, "缺少用户名或密码");
        }

        try
        {
            logger.LogInformation("开始生成日历订阅，学号: {Username}", username);

            var content = await calendarService.GenerateAsync(
                username, effectivePassword, filter, cancellationToken);

            // 手写响应头：Flask 当年就是 attachment + filename=calendar.ics，
            // 部分日历客户端据此判断这是可导入的文件。
            Response.Headers.ContentDisposition = "attachment; filename=calendar.ics";
            // URL 里明文带着密码，绝不能被任何中间层缓存
            Response.Headers.CacheControl = "no-store";
            return File(content, "text/calendar; charset=utf-8");
        }
        catch (Exceptions.StudentCooldownException)
        {
            return CalendarError(StatusCodes.Status429TooManyRequests,
                Message(ApiMessageKey.EduSystemRateLimited), RetryAfterSeconds());
        }
        catch (Exceptions.RateLimitException)
        {
            return CalendarError(StatusCodes.Status429TooManyRequests,
                Message(ApiMessageKey.EduSystemRateLimited), RetryAfterSeconds());
        }
        catch (Exceptions.AccountBannedException ex)
        {
            logger.LogWarning("学号 {Username} 已被登录服务封禁，理由 {Reason}，解封时刻 {UnbanAt}",
                username, ex.Reason, ex.UnbanAt);
            return CalendarError(StatusCodes.Status429TooManyRequests,
                Message(ApiMessageKey.AccountBanned), ex.RetryAfterSeconds ?? 60);
        }
        catch (Exceptions.LoginFailedException)
        {
            return CalendarError(StatusCodes.Status401Unauthorized,
                Message(ApiMessageKey.InvalidUsernameOrPassword));
        }
        catch (Exceptions.UnAuthenticationError)
        {
            return CalendarError(StatusCodes.Status401Unauthorized,
                Message(ApiMessageKey.AuthenticationFailed));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "生成日历订阅失败，学号: {Username}", username);
            return CalendarError(StatusCodes.Status500InternalServerError,
                Message(ApiMessageKey.EduSystemAccessFailed));
        }
    }

    /// <summary>
    /// 按本地限流状态推算 Retry-After（秒），没有状态时给 60 秒。
    /// 与 <see cref="V1ControllerBase.RateLimited"/> 的推算方式保持一致。
    /// </summary>
    private int RetryAfterSeconds()
    {
        var rateLimitState = HttpContext.RequestServices?.GetService<IStudentRateLimitState>();
        return rateLimitState is null ? 60 : HttpContext.GetRetryAfterSeconds(rateLimitState) ?? 60;
    }

    /// <summary>
    /// 日历接口历史的错误形状是 <c>{"error": ...}</c>（key 不是 <c>message</c>），
    /// 与其它 v1 端点的 <c>ApiResponse</c> 信封不同——这里保留老形状。
    /// </summary>
    private ObjectResult CalendarError(int statusCode, string message, int? retryAfterSeconds = null)
    {
        if (retryAfterSeconds is not null)
        {
            Response.Headers.RetryAfter = retryAfterSeconds.Value.ToString(CultureInfo.InvariantCulture);
        }

        return StatusCode(statusCode, new ErrorResponse { error = message });
    }
}
