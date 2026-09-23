using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XAUAT.EduApi.Controllers.V1;

namespace XAUAT.EduApi.Tests.Controllers;

/// <summary>
/// 路由表冒烟测试：确认 <c>v1/course/Calendar</c> 真的作为端点挂上了。
/// <para>
/// <b>为什么必须有</b>：<see cref="CalendarControllerTests"/> 是直接 <c>new</c> 控制器、
/// 调 action 方法的，它证明不了"路由、HTTP 方法"这一层。写错 <c>[Route]</c> 前缀或漏掉
/// <c>[HttpGet]</c>，那些用例照样全绿，线上却是 404——这正是本仓库踩过的坑
/// （LoginApi 漏了一行 <c>app.MapAuthEndpoints()</c>，编译、单测、AOT 全过）。
/// </para>
/// <para>
/// <b>刻意不启动宿主</b>：<c>Program.cs</c> 启动时会跑 <c>Database.Migrate()</c>，
/// 测试里绝不能碰（本仓库既有教训：<c>.env</c> 覆盖环境变量叠加迁移会毁数据）。
/// 这里只装配 MVC 路由，不注册数据库、不迁移、不发任何请求。
/// </para>
/// <para>
/// 注意端点要从 <c>((IEndpointRouteBuilder)app).DataSources</c> 取，**不是**从
/// <c>app.Services.GetRequiredService&lt;EndpointDataSource&gt;()</c>——后者在只
/// <c>MapControllers()</c> 未跑中间件时是空的（0 个端点），会得到一个永远失败的假信号。
/// </para>
/// </summary>
public class CalendarRouteTests
{
    private static List<(string? Pattern, string? Method)> MapRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(CalendarController).Assembly);

        var app = builder.Build();
        app.MapControllers();

        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => (
                Pattern: endpoint.RoutePattern.RawText,
                Method: endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.FirstOrDefault()))
            .ToList();
    }

    [Fact]
    public void CalendarRoute_ShouldBeMappedAsGet()
    {
        var routes = MapRoutes();

        Assert.NotEmpty(routes);
        Assert.Contains(routes, route =>
            route.Pattern == "v1/course/Calendar" && route.Method == "GET");
    }

    [Fact]
    public void CourseRoutes_ShouldNotBeDisplacedByCalendar()
    {
        // 日历原本是 CourseController 上的一个 action，现在挪到了独立控制器。
        // 这条确认同前缀的另两个端点都还在。
        var routes = MapRoutes();

        Assert.Contains(routes, route => route.Pattern == "v1/course" && route.Method == "GET");
        Assert.Contains(routes, route => route.Pattern == "v1/course/ScheduleTime" && route.Method == "GET");
    }
}
