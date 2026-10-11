using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using System.Net;
using System.Reflection;
using XAUAT.EduApi.Controllers.V1;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Logging;
using XAUAT.EduApi.Login;
using XAUAT.EduApi.Login.Unified;
using XAUAT.EduApi.Login.Xauat;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Tests.Extensions;

/// <summary>
/// <see cref="ServiceCollectionExtensions.AddLoginServices"/> 与
/// <see cref="ServiceCollectionExtensions.AddBusinessServices"/> 的注册测试。
/// <para>
/// 单元测试都是直接 <c>new SsoAuthService(...)</c> 的，绕开了 DI，因此证明不了容器能真的
/// 把它构造出来——而这些服务全都依赖真实的 <c>IHttpClientFactory</c> / Redis，
/// 漏注册只会在**请求时**才 500。这个测试就是守这条路径的。
/// </para>
/// </summary>
public class ServiceRegistrationTests
{
    [Fact]
    public void LoginService_ShouldBeResolvable()
    {
        using var provider = BuildProvider();

        var loginService = provider.GetRequiredService<ILoginService>();

        Assert.IsType<SsoLoginService>(loginService);
    }

    [Fact]
    public void LoginService_ShouldBeScoped_NotSingleton()
    {
        // SsoLoginService 依赖 Scoped 的 ICookieCodeService。注册成单例就是 captive dependency：
        // Development 下每次请求抛异常，Production 下静默把它捕获到进程结束——
        // 后者意味着 CookieCodeService 的 HttpClient 再也不会轮换。
        var services = BuildServices();

        Assert.Equal(ServiceLifetime.Scoped, DescribeLifetime<ILoginService>(services));
        Assert.Equal(ServiceLifetime.Scoped, DescribeLifetime<ILoginAdminService>(services));
    }

    [Fact]
    public void LoginRedisStore_ShouldNotDependOnEduApiCacheService()
    {
        // 登录那套 Redis 键**刻意不带任何前缀**（与 Flask 共用），而 EduApi 的 ICacheService
        // 会给键加 "EduApi:" 前缀、并把值包成 CacheItem<T> JSON 信封。
        // 一旦有人"顺手统一"成 ICacheService，不会有任何报错——只表现为缓存始终不命中、
        // 活跃用户数恒为 0、封禁状态读不出来。这条就是拦那次重构的。
        var parameters = typeof(LoginRedisStore).GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToList();

        Assert.DoesNotContain(typeof(Caching.ICacheService), parameters);
        Assert.Contains(typeof(IEnumerable<IConnectionMultiplexer>), parameters);
    }

    [Fact]
    public void LoginRedisStore_ShouldReportUnavailable_WhenNoMultiplexerRegistered()
    {
        // 没配 REDIS 时容器注入空集合，而不是抛异常——这是"降级为无缓存、无限流登录"的入口。
        using var provider = BuildProvider();

        Assert.False(provider.GetRequiredService<ILoginRedisStore>().IsAvailable);
    }

    [Fact]
    public void LoginRedisStore_ShouldPickUpRegisteredMultiplexer()
    {
        // 反向：注册了多路复用器就必须真的用上。
        // 若将来 AddRedisServices 改了注册方式而这里读不到，故障是静默的（登录仍能work，
        // 只是永远不缓存、限流封禁全部失效）。
        var database = new Mock<IDatabase>();
        var multiplexer = new Mock<IConnectionMultiplexer>();
        multiplexer.Setup(mux => mux.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);

        using var provider = BuildProvider(multiplexer.Object);

        Assert.True(provider.GetRequiredService<ILoginRedisStore>().IsAvailable);
    }

    /// <summary>
    /// 认证服务器客户端的两项开关。
    /// <para>
    /// 这是整条登录链路里**最容易配错、失败信息最不像配置错误**的一处：
    /// 自动重定向一旦打开，<see cref="XauatSsoClient"/> 手工逐跳捕获 Set-Cookie 的逻辑
    /// 就拿不到中间响应，教务会话（种在跳转链第 2 跳）取不到，
    /// 表现为「所有用户登录都报『未能获取有效会话』」——看起来像上游挂了。
    /// 自动 cookie 管理打开则会让并发登录共用一个 CookieContainer 而串号。
    /// </para>
    /// <para>
    /// 断言方式：把生成好的 handler 链一路剥到最内层的 <see cref="SocketsHttpHandler"/> 再读属性。
    /// 依赖 <c>DelegatingHandler._innerHandler</c> 这个私有字段名；名字若变，这里会直接失败
    /// （而不是静默放过），正是想要的失败模式。
    /// </para>
    /// </summary>
    [Fact]
    public void XauatAuthClient_ShouldDisableAutoRedirectAndCookieHandling()
    {
        using var provider = BuildFullProvider();
        var handlerFactory = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        using var handler = handlerFactory.CreateHandler(XauatSsoClient.ClientName);
        var sockets = Assert.IsType<SocketsHttpHandler>(Unwrap(handler));

        Assert.False(sockets.AllowAutoRedirect, "认证客户端必须关掉自动重定向（换票靠逐跳捕获 Set-Cookie）");
        Assert.False(sockets.UseCookies, "认证客户端必须关掉自动 cookie（并发登录会串号）");
    }

    [Fact]
    public void CalendarService_ShouldBeResolvable()
    {
        // 日历端点整条链路都靠 DI：漏注册 ICalendarService 只会在**请求时**才 500，
        // 直接 new 的单元测试永远发现不了。
        using var provider = BuildFullProvider();

        Assert.IsType<CalendarService>(provider.GetRequiredService<ICalendarService>());
    }

    /// <summary>
    /// 通用统一身份认证客户端的 handler 开关。
    /// <para>
    /// 与 <see cref="XauatAuthClient_ShouldDisableAutoRedirectAndCookieHandling"/> 同理，
    /// 但多两条是 SSRF 防线的组成部分：
    /// <c>UseProxy = false</c>——走代理时 TCP 连接由代理建立，<c>ConnectCallback</c> 里的
    /// 内网地址校验就形同虚设；<c>ConnectCallback</c> 必须真的挂上，
    /// 光有域名白名单挡不住"合法域名被解析到 127.0.0.1"。
    /// </para>
    /// </summary>
    [Fact]
    public void UnifiedAuthClient_ShouldDisableProxyAndAutoRedirect_AndGuardConnect()
    {
        using var provider = BuildFullProvider();
        var handlerFactory = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        using var handler = handlerFactory.CreateHandler(UnifiedSsoClient.ClientName);
        var sockets = Assert.IsType<SocketsHttpHandler>(Unwrap(handler));

        Assert.False(sockets.AllowAutoRedirect, "通用认证客户端必须关掉自动重定向（票据在 302 那一跳上）");
        Assert.False(sockets.UseCookies, "通用认证客户端必须关掉自动 cookie（并发登录会串号）");
        Assert.False(sockets.UseProxy, "必须禁用代理，否则建连前的内网地址校验会被绕过");
        Assert.NotNull(sockets.ConnectCallback);
    }

    [Fact]
    public void UnifiedAuthService_ShouldBeResolvable()
    {
        using var provider = BuildFullProvider();

        Assert.IsType<UnifiedAuthService>(provider.GetRequiredService<IUnifiedAuthService>());
    }

    [Fact]
    public void LoginAdminService_ShouldBeResolvable()
    {
        using var provider = BuildFullProvider();

        Assert.IsType<LocalLoginAdminService>(provider.GetRequiredService<ILoginAdminService>());
    }

    [Fact]
    public void ServiceConfiguration_ShouldBeResolvable()
    {
        // 此前 ServiceConfiguration 只是 Program.cs 里的一个局部变量，谁都没法注入。
        // 这条守住它已经在 AddAllServices 里注册上了。
        using var provider = BuildFullProvider();

        Assert.NotNull(provider.GetRequiredService<ServiceConfiguration>());
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

    private static ServiceProvider BuildProvider(IConnectionMultiplexer? multiplexer = null)
        => BuildServices(multiplexer).BuildServiceProvider();

    /// <summary>
    /// 只装配业务服务（<see cref="ServiceCollectionExtensions.AddBusinessServices"/>）。
    /// <para>
    /// 够登录那几条用例用；要激活控制器就必须走 <see cref="BuildFullProvider"/> 那条真实全量注册，
    /// 否则缺的是哪一层都说不清。
    /// </para>
    /// </summary>
    private static IServiceCollection BuildServices(IConnectionMultiplexer? multiplexer = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // CookieCodeService 要 IHttpClientFactory，而它由 AddBusinessServices 里的
        // AddHttpClient 注册；这里替换成替身只是为了让测试不依赖它的真实行为。
        services.AddSingleton(new Mock<ICookieCodeService>().Object);

        // ITestAccountResolver 由 AddTestAccountServices 注册（在 AddAllServices 里），
        // 不在 AddBusinessServices 内，因此这里补一个替身——与上面同理。
        services.AddSingleton(Mock.Of<ITestAccountResolver>());

        if (multiplexer is not null)
        {
            services.AddSingleton(multiplexer);
        }

        services.AddBusinessServices(new ServiceConfiguration
        {
            // 支付那段的必需项，缺了会直接抛
            PaymentApiBaseUrl = "http://xauat-paymentapi:8080"
        });

        return services;
    }

    /// <summary>注册的生命周期。用来钉住 SsoLoginService 不能是单例（captive dependency）。</summary>
    private static ServiceLifetime DescribeLifetime<T>(IServiceCollection services)
        => services.First(descriptor => descriptor.ServiceType == typeof(T)).Lifetime;

    /// <summary>
    /// 把 handler 链一路剥到最内层（<c>DelegatingHandler._innerHandler</c>）。
    /// 用反射是因为 <c>IHttpMessageHandlerFactory</c> 只给最外层
    /// （<c>LifetimeTrackingHttpMessageHandler</c>），而我们要看的是被它包住的 primary handler。
    /// <para>
    /// 必须沿类型继承链找那个私有字段：<c>GetField</c> 不会去基类里翻私有成员，
    /// 而 <c>_innerHandler</c> 声明在 <see cref="DelegatingHandler"/> 上。
    /// </para>
    /// </summary>
    private static HttpMessageHandler Unwrap(HttpMessageHandler handler)
    {
        while (FindInner(handler) is { } inner)
        {
            handler = inner;
        }

        return handler;
    }

    private static HttpMessageHandler? FindInner(HttpMessageHandler handler)
    {
        for (var type = handler.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField("_innerHandler", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(handler) is HttpMessageHandler inner)
            {
                return inner;
            }
        }

        return null;
    }
}
