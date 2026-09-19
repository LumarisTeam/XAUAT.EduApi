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

    protected ObjectResult RateLimited(string key)
    {
        var services = HttpContext.RequestServices;
        var rateLimitState = services is null ? null : services.GetService<IStudentRateLimitState>();
        var retryAfterSeconds = rateLimitState is null
            ? 60
            : HttpContext.GetRetryAfterSeconds(rateLimitState) ?? 60;

        Response.Headers.RetryAfter = retryAfterSeconds.ToString();

        return StatusCode(StatusCodes.Status429TooManyRequests, new RateLimitErrorResponse
        {
            error = "rate_limited",
            message = Message(key),
            retryAfterSeconds = retryAfterSeconds
        });
    }
}
