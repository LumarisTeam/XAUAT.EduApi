using XAUAT.EduApi.Middlewares;
using Scalar.AspNetCore;

namespace XAUAT.EduApi.Extensions;

/// <summary>
/// 应用程序构建器扩展方法
/// 用于模块化注册中间件
/// </summary>
public static class ApplicationBuilderExtensions
{
    /// <param name="app">应用程序构建器</param>
    extension(IApplicationBuilder app)
    {
        /// <summary>
        /// 使用错误处理中间件
        /// </summary>
        /// <returns>应用程序构建器</returns>
        public IApplicationBuilder UseErrorHandling()
        {
            app.UseErrorHandlingMiddleware();
            return app;
        }

        /// <summary>
        /// 使用CORS中间件
        /// </summary>
        /// <returns>应用程序构建器</returns>
        public IApplicationBuilder UseCustomCors()
        {
            app.UseCors();
            return app;
        }
    }

    /// <param name="app">应用程序</param>
    extension(WebApplication app)
    {
        /// <summary>
        /// 配置API端点
        /// </summary>
        /// <returns>应用程序</returns>
        public WebApplication ConfigureApiEndpoints()
        {
            app.MapOpenApi();
            app.MapControllers();
            app.MapScalarApiReference();
            return app;
        }

    }

    /// <summary>
    /// 配置完整的中间件管道
    /// </summary>
    /// <param name="app">应用程序构建器</param>
    /// <returns>应用程序构建器</returns>
    public static IApplicationBuilder ConfigureMiddlewarePipeline(this IApplicationBuilder app)
    {
        return app
            .UseErrorHandling()
            .UseAuthorization()
            .UseCustomCors();
    }

    /// <summary>
    /// 配置完整的应用程序
    /// </summary>
    /// <param name="app">应用程序</param>
    /// <returns>应用程序</returns>
    public static WebApplication ConfigureApplication(this WebApplication app)
    {
        // 配置中间件管道
        app.ConfigureMiddlewarePipeline();

        // 配置端点
        app.ConfigureApiEndpoints();

        return app;
    }
}
