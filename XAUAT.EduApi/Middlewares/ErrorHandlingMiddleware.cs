using System.Net;
using System.Text.Json;
using EduApi.Data.Models;
using XAUAT.EduApi.Controllers.V1;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Middlewares;

/// <summary>
/// 错误处理中间件
/// </summary>
public class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorHandlingMiddleware> _logger;
    private readonly ILanguageResolver _languageResolver;
    private readonly IApiMessageLocalizer _messageLocalizer;
    private readonly IStudentRateLimitState _rateLimitState;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="next">下一个中间件</param>
    /// <param name="logger">日志记录器</param>
    public ErrorHandlingMiddleware(
        RequestDelegate next,
        ILogger<ErrorHandlingMiddleware> logger,
        ILanguageResolver languageResolver,
        IApiMessageLocalizer messageLocalizer,
        IStudentRateLimitState rateLimitState)
    {
        _next = next;
        _logger = logger;
        _languageResolver = languageResolver;
        _messageLocalizer = messageLocalizer;
        _rateLimitState = rateLimitState;
    }

    /// <summary>
    /// 执行中间件
    /// </summary>
    /// <param name="context">HTTP上下文</param>
    /// <returns>任务</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    /// <summary>
    /// 处理异常
    /// </summary>
    /// <param name="context">HTTP上下文</param>
    /// <param name="exception">异常</param>
    /// <returns>任务</returns>
    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        if (exception is StudentCooldownException or RateLimitException)
        {
            await HandleRateLimitExceptionAsync(context, exception);
            return;
        }

        _logger.LogError(exception, "发生未处理的异常: {Path}", context.Request.Path);

        // 设置响应状态码
        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;

        // 设置响应头
        context.Response.ContentType = "application/json";

        // 构建错误响应
        object errorResponse;
        if (context.Request.Path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase))
        {
            errorResponse = new ApiResponse<object>
            {
                Data = null,
                Code = ApiCodes.InternalError,
                Message = "服务器内部错误，请联系管理员"
            };
        }
        else
        {
            errorResponse = new
            {
                context.Response.StatusCode,
                Message = "服务器内部错误，请联系管理员",
                Details = exception.Message,
                context.Request.Path,
                Timestamp = DateTime.UtcNow
            };
        }

        // 序列化错误响应
        var jsonResponse = JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });

        // 写入响应
        await context.Response.WriteAsync(jsonResponse);
    }

    private async Task HandleRateLimitExceptionAsync(HttpContext context, Exception exception)
    {
        _logger.LogWarning(exception, "请求触发限流: {Path}", context.Request.Path);

        var language = _languageResolver.Resolve(context);
        var retryAfterSeconds = exception switch
        {
            StudentCooldownException cooldownException => Math.Max(
                1,
                (int)Math.Ceiling((cooldownException.BlockedUntil - DateTimeOffset.UtcNow).TotalSeconds)),
            _ => context.GetRetryAfterSeconds(_rateLimitState) ?? 60
        };

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.ContentType = "application/json";
        context.Response.Headers.RetryAfter = retryAfterSeconds.ToString();

        object response;
        if (context.Request.Path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase))
        {
            response = new ApiResponse<object>
            {
                Data = null,
                Code = ApiCodes.RateLimited,
                Message = _messageLocalizer.Get(language, ApiMessageKey.EduSystemRateLimited)
            };
        }
        else
        {
            response = new RateLimitErrorResponse
            {
                error = "rate_limited",
                message = _messageLocalizer.Get(language, ApiMessageKey.EduSystemRateLimited),
                retryAfterSeconds = retryAfterSeconds
            };
        }

        var jsonResponse = JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });

        await context.Response.WriteAsync(jsonResponse);
    }
}

/// <summary>
/// 错误处理中间件扩展方法
/// </summary>
public static class ErrorHandlingMiddlewareExtensions
{
    /// <summary>
    /// 使用错误处理中间件
    /// </summary>
    /// <param name="app">应用构建器</param>
    /// <returns>应用构建器</returns>
    public static IApplicationBuilder UseErrorHandlingMiddleware(this IApplicationBuilder app)
    {
        return app.UseMiddleware<ErrorHandlingMiddleware>();
    }
}
