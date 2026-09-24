using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XAUAT.EduApi.Controllers.V1;

namespace XAUAT.EduApi.Tests.Controllers;

/// <summary>
/// 路由表冒烟测试：确认三个登录运维端点真的作为端点挂上了。
/// <para>
/// <b>为什么必须有</b>：<see cref="LoginOpsControllerTests"/> 是直接 <c>new</c> 控制器、
/// 调 action 方法的，它证明不了"路由、HTTP 方法"这一层。写错 <c>[Route]</c> 前缀或漏掉
/// <c>[HttpGet]</c>，那些用例照样全绿，线上却是 404——这正是本仓库踩过的坑。
/// </para>
/// <para>
/// <b>刻意不启动宿主</b>：<c>Program.cs</c> 启动时会跑 <c>Database.Migrate()</c>，
/// 测试里绝不能碰。这里只装配 MVC 路由，不注册数据库、不迁移、不发任何请求。
/// </para>
/// </summary>
public class LoginOpsRouteTests
{
    private static List<(string? Pattern, string? Method)> MapRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(LoginOpsController).Assembly);

        var app = builder.Build();
        app.MapControllers();

        // 注意要从 ((IEndpointRouteBuilder)app).DataSources 取，不是从
        // app.Services.GetRequiredService<EndpointDataSource>()——后者在只 MapControllers()
        // 未跑中间件时是空的（0 个端点），会得到一个永远失败的假信号。
        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => (
                Pattern: endpoint.RoutePattern.RawText,
                Method: endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.FirstOrDefault()))
            .ToList();
    }

    [Theory]
    [InlineData("v1/login-ops/user-count", "GET")]
    [InlineData("v1/login-ops/ban-logs", "GET")]
    [InlineData("v1/login-ops/unban", "POST")]
    public void LoginOpsRoutes_ShouldBeMapped(string pattern, string method)
    {
        var routes = MapRoutes();

        Assert.NotEmpty(routes);
        Assert.Contains(routes, route => route.Pattern == pattern && route.Method == method);
    }

    [Fact]
    public void LoginRoute_ShouldNotBeDisplacedByLoginOps()
    {
        // v1/login-ops 与既有的 v1/login 前缀相邻，确认没有被吃掉
        var routes = MapRoutes();

        Assert.Contains(routes, route => route.Pattern == "v1/login" && route.Method == "POST");
    }
}
