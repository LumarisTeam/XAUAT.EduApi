using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using XAUAT.EduApi.Controllers.V1;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Logging;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Tests.Extensions;

/// <summary>
/// <see cref="ServiceCollectionExtensions.AddBusinessServices"/> 里登录那段的注册测试。
/// <para>
/// 单元测试都是直接 <c>new HttpLoginService(...)</c> 的，绕开了 DI，因此证明不了
/// 容器能真的把它构造出来——而它的构造签名里有两个可选参数
/// （<c>ITestAccountResolver?</c> 与 <c>TimeSpan[]?</c>，生产环境都不注册），
/// 一旦 <c>ActivatorUtilities</c> 不认默认值，故障发生在启动时而不是测试里。
/// 这个测试就是守这条路径的。
/// </para>
/// </summary>
public class ServiceRegistrationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ResolvedLoginApiBaseUrl_ShouldFallBackToFlask_WhenNotConfigured(string? configured)
    {
        var configuration = new ServiceConfiguration { LoginApiBaseUrl = configured };

        Assert.Equal(ServiceConfiguration.FlaskLoginBaseUrl, configuration.ResolvedLoginApiBaseUrl);
        Assert.Equal("https://schedule.xauat.site", configuration.ResolvedLoginApiBaseUrl);
    }

    [Fact]
    public void ResolvedLoginApiBaseUrl_ShouldPreferConfiguredValue()
    {
        var configuration = new ServiceConfiguration { LoginApiBaseUrl = "http://xauat-loginapi:8080" };

        Assert.Equal("http://xauat-loginapi:8080", configuration.ResolvedLoginApiBaseUrl);
    }

    [Fact]
    public void LoginService_ShouldBeResolvable_WhenLoginApiNotConfigured()
    {
        // ITestAccountResolver 与 retryDelays 在生产注册里都不存在，
        // 必须靠默认值兜住——这正是这条用例要钉死的行为。
        using var provider = BuildProvider(loginApiBaseUrl: null);

        var loginService = provider.GetRequiredService<ILoginService>();

        Assert.IsType<HttpLoginService>(loginService);
    }

    [Fact]
    public void LoginService_ShouldBeResolvable_WhenLoginApiConfigured()
    {
        using var provider = BuildProvider("http://xauat-loginapi:8080");

        Assert.IsType<HttpLoginService>(provider.GetRequiredService<ILoginService>());
    }

    [Fact]
    public void LoginClient_ShouldTargetConfiguredLoginApi()
    {
        using var provider = BuildProvider("http://xauat-loginapi:8080");
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        var client = factory.CreateClient(nameof(ILoginService));

        Assert.Equal(new Uri("http://xauat-loginapi:8080"), client.BaseAddress);
        Assert.Equal(HttpTimeouts.Slow, client.Timeout);
    }

    [Fact]
    public void LoginClient_ShouldTargetFlask_WhenLoginApiNotConfigured()
    {
        using var provider = BuildProvider(loginApiBaseUrl: null);
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        var client = factory.CreateClient(nameof(ILoginService));

        Assert.Equal(new Uri("https://schedule.xauat.site"), client.BaseAddress);
    }


    [Fact]
    public void CalendarService_ShouldBeResolvable()
    {
        // 日历端点整条链路都靠 DI：漏注册 ICalendarService 只会在**请求时**才 500，
        // 直接 new 的单元测试永远发现不了。
        using var provider = BuildFullProvider();

        Assert.IsType<CalendarService>(provider.GetRequiredService<ICalendarService>());
    }

    [Fact]
    public void AllControllers_ShouldBeActivatable()
    {
        using var provider = BuildFullProvider();

        var controllers = typeof(CourseController).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } &&
                           typeof(ControllerBase).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(controllers);

        var failures = new List<string>();
        foreach (var controller in controllers)
        {
            try
            {
                // MVC 是用 ActivatorUtilities 激活控制器的（控制器本身不注册进容器），
                // 这里复刻同一条路径：构造函数里有解析不出来的依赖会当场抛，
                // 而不是等到线上某个请求才 500。
                ActivatorUtilities.CreateInstance(provider, controller);
            }
            catch (Exception ex)
            {
                failures.Add($"{controller.Name}: {ex.Message}");
            }
        }

        Assert.Empty(failures);
    }

    /// <summary>
    /// 按**生产同一条路径**（<see cref="ServiceCollectionExtensions.AddAllServices"/>）装配容器。
    /// <para>
    /// 与 <see cref="BuildProvider"/> 的区别：那个只注册业务服务，够登录那几条用例用；
    /// 要激活控制器就必须走真实的全量注册，否则缺的是哪一层都说不清。
    /// </para>
    /// <para>
    /// 安全性：<c>AddDbContextFactory</c> 与 DataProtection 都是惰性的，只 <c>Build</c>
    /// 不解析就不会建 <c>Data.db</c>/<c>keys/</c>；这里也**刻意不调用** <c>Database.Migrate()</c>
    /// （那是 Program.cs 启动时干的事），更不读 <c>.env</c>——<c>ServiceConfiguration</c> 由本方法直接构造。
    /// </para>
    /// </summary>
    private static ServiceProvider BuildFullProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // 下面两项在生产里不在 AddAllServices 里，而在 Program.cs 里注册，
        // 裸 ServiceCollection 拿不到：
        //   IHostEnvironment —— 由宿主提供（TestDataProvider 要用 ContentRootPath）
        //   ILogStore        —— LogsController 的依赖
        services.AddSingleton(Mock.Of<IHostEnvironment>());
        services.AddSingleton<ILogStore>(new InMemoryLogStore());

        services.AddAllServices(new ServiceConfiguration
        {
            // 支付那段缺了会直接抛
            PaymentApiBaseUrl = "http://xauat-paymentapi:8080",
            // 留空走 SQLite 分支：只注册工厂，不连接
            SqlConnectionString = "",
            RedisConnectionString = null
        });

        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildProvider(string? loginApiBaseUrl)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // CookieCodeService 要 IHttpClientFactory，而它由 AddBusinessServices 里的
        // AddHttpClient 注册；这里替换成替身只是为了让测试不依赖它的真实行为。
        services.AddSingleton(new Mock<ICookieCodeService>().Object);

        services.AddBusinessServices(new ServiceConfiguration
        {
            // 支付那段的必需项，缺了会直接抛
            PaymentApiBaseUrl = "http://xauat-paymentapi:8080",
            LoginApiBaseUrl = loginApiBaseUrl
        });

        return services.BuildServiceProvider();
    }
}
