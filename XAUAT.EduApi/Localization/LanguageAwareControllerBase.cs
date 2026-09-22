using Microsoft.AspNetCore.Mvc;
using EduApi.Data.Models;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Localization;

public abstract class LanguageAwareControllerBase(
    ILanguageResolver languageResolver,
    IApiMessageLocalizer messageLocalizer) : ControllerBase
{
    protected string Language => languageResolver.Resolve(HttpContext);

    protected string Message(string key)
    {
        return messageLocalizer.Get(Language, key);
    }

    /// <param name="key">本地化文案的键</param>
    /// <param name="retryAfterSeconds">
    /// 显式指定 Retry-After（秒）。给了就用它覆盖按学生限流状态推算出的值——
    /// 账号封禁的等待时间来自上游给的解封时刻，与本地限流窗口无关，
    /// 沿用推算值会告诉一个被封 24 小时的账号"60 秒后再试"。
    /// </param>
    protected ObjectResult RateLimited(string key, int? retryAfterSeconds = null)
    {
        var services = HttpContext.RequestServices;
        var rateLimitState = services is null ? null : services.GetService<IStudentRateLimitState>();
        var retryAfter = retryAfterSeconds
            ?? (rateLimitState is null
                ? 60
                : HttpContext.GetRetryAfterSeconds(rateLimitState) ?? 60);

        Response.Headers.RetryAfter = retryAfter.ToString();

        return StatusCode(StatusCodes.Status429TooManyRequests, new RateLimitErrorResponse
        {
            error = "rate_limited",
            message = Message(key),
            retryAfterSeconds = retryAfter
        });
    }
}
