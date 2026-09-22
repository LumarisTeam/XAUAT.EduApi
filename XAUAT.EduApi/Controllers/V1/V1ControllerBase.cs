using EduApi.Data.Models;
using Microsoft.AspNetCore.Mvc;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Controllers.V1;

public abstract class V1ControllerBase(
    ILanguageResolver languageResolver,
    IApiMessageLocalizer messageLocalizer)
    : LanguageAwareControllerBase(languageResolver, messageLocalizer)
{
    protected static ApiResponse<T> SuccessResponse<T>(T data, string message = "ok")
        => new() { Data = data, Code = ApiCodes.Success, Message = message };

    protected static ApiResponse<List<T>> SuccessListResponse<T>(List<T> data, string message = "ok")
        => new() { Data = data, Code = ApiCodes.Success, Message = message, Total = data.Count };

    protected static ApiResponse<object?> ErrorResponse(int code, string message)
        => new() { Data = null, Code = code, Message = message };

    /// <param name="key">本地化文案的键</param>
    /// <param name="retryAfterSeconds">
    /// 显式指定 Retry-After（秒）。给了就用它覆盖按学生限流状态推算出的值——
    /// 账号封禁的等待时间来自上游给的解封时刻，与本地限流窗口无关。
    /// </param>
    protected new ObjectResult RateLimited(string key, int? retryAfterSeconds = null)
    {
        var services = HttpContext.RequestServices;
        var rateLimitState = services?.GetService<IStudentRateLimitState>();
        var retryAfter = retryAfterSeconds
            ?? (rateLimitState is null
                ? 60
                : HttpContext.GetRetryAfterSeconds(rateLimitState) ?? 60);

        Response.Headers.RetryAfter = retryAfter.ToString();

        return StatusCode(StatusCodes.Status429TooManyRequests,
            ErrorResponse(ApiCodes.RateLimited, Message(key)));
    }
}
