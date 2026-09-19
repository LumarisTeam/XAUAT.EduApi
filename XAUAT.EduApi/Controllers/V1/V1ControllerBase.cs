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

    protected new ObjectResult RateLimited(string key)
    {
        var services = HttpContext.RequestServices;
        var rateLimitState = services?.GetService<IStudentRateLimitState>();
        var retryAfterSeconds = rateLimitState is null
            ? 60
            : HttpContext.GetRetryAfterSeconds(rateLimitState) ?? 60;

        Response.Headers.RetryAfter = retryAfterSeconds.ToString();

        return StatusCode(StatusCodes.Status429TooManyRequests,
            ErrorResponse(ApiCodes.RateLimited, Message(key)));
    }
}
