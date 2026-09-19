using System.Diagnostics;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Middlewares;

/// <summary>
/// 指标收集中间件
/// 用于自动收集API请求的持续时间和错误信息
/// </summary>
public class MetricsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IMonitoringService _monitoringService;
    private readonly ILogger<MetricsMiddleware> _logger;
    
    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="next">下一个中间件</param>
    /// <param name="monitoringService">监控服务</param>
    /// <param name="logger">日志记录器</param>
    public MetricsMiddleware(RequestDelegate next, IMonitoringService monitoringService, ILogger<MetricsMiddleware> logger)
    {
        _next = next;
        _monitoringService = monitoringService;
        _logger = logger;
    }
    
    /// <summary>
    /// 处理请求
    /// </summary>
    /// <param name="context">HTTP上下文</param>
    public async Task InvokeAsync(HttpContext context)
    {
        // 记录请求开始时间
        var stopwatch = Stopwatch.StartNew();
        
        try
        {
            // 调用下一个中间件
            await _next(context);
            
            // 记录API调用
            _monitoringService.RecordApiCall(context.Request.Path, context.Request.Method);
            
            // 记录API请求持续时间
            stopwatch.Stop();
            _monitoringService.RecordApiRequestDuration(stopwatch.Elapsed.TotalMilliseconds);
            
            // 记录错误（如果状态码 >= 400）
            if (context.Response.StatusCode >= 400)
            {
                _monitoringService.RecordApiError();
            }
        }
        catch (Exception)
        {
            // 记录异常
            _monitoringService.RecordApiError();
            _monitoringService.RecordBusinessError();
            
            // 重新抛出异常，让其他中间件处理
            throw;
        }
    }
}