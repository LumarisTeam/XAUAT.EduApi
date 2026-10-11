using EduApi.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using NpgsqlDataProtection;
using StackExchange.Redis;
using System.Net;
using System.Threading.RateLimiting;
using XAUAT.EduApi.Caching;
using XAUAT.EduApi.Configuration;
using XAUAT.EduApi.Filters;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Login;
using XAUAT.EduApi.Login.Unified;
using XAUAT.EduApi.Login.Xauat;
using XAUAT.EduApi.Queues;
using XAUAT.EduApi.Repos;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Extensions;

/// <summary>
/// 服务注册扩展方法
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <param name="services">服务集合</param>
    extension(IServiceCollection services)
    {
        /// <summary>
        /// 注册数据库服务
        /// </summary>
        /// <param name="sqlConnectionString">SQL连接字符串</param>
        /// <returns>服务集合</returns>
        public IServiceCollection AddDatabaseServices(string? sqlConnectionString)
        {
            if (string.IsNullOrEmpty(sqlConnectionString))
            {
                services.AddDbContextFactory<EduContext>(opt =>
                {
                    opt.UseSqlite("Data Source=Data.db",
                        o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
                    opt.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
                });

                services.AddDataProtection()
                    .PersistKeysToFileSystem(new DirectoryInfo("./keys"));
            }
            else
            {
                services.AddDbContextFactory<EduContext>(opt =>
                {
                    opt.UseNpgsql(sqlConnectionString,
                        o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
                    opt.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
                });
                services.AddDataProtection()
                    .PersistKeysToPostgres(sqlConnectionString, true);
            }

            return services;
        }

        /// <summary>
        /// 注册Redis服务
        /// </summary>
        /// <param name="redisConnectionString">Redis连接字符串</param>
        /// <returns>服务集合</returns>
        public IServiceCollection AddRedisServices(string? redisConnectionString)
        {
            if (!string.IsNullOrEmpty(redisConnectionString))
            {
                services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisConnectionString));
            }

            return services;
        }

        /// <summary>
        /// 注册仓库服务
        /// </summary>
        /// <returns>服务集合</returns>
        public IServiceCollection AddRepositoryServices()
        {
            services.AddScoped<IScoreRepository, ScoreRepository>();
            services.AddScoped<IExamRepository, ExamRepository>();
            services.AddScoped<IElectricitySubscriptionRepository, ElectricitySubscriptionRepository>();
            services.AddScoped<IMapPoiRepository, MapPoiRepository>();

            return services;
        }

        /// <summary>
        /// 注册测试账号相关服务
        /// </summary>
        /// <param name="options">测试账号配置</param>
        /// <returns>服务集合</returns>
        public IServiceCollection AddTestAccountServices(TestAccountOptions options)
        {
            services.AddSingleton(Options.Create(options));
            services.AddSingleton<ITestAccountResolver, TestAccountResolver>();
            services.AddSingleton<ITestDataProvider, TestDataProvider>();

            return services;
        }

        /// <summary>
        /// 注册业务服务
        /// </summary>
        /// <returns>服务集合</returns>
        public IServiceCollection AddBusinessServices(ServiceConfiguration configuration)
        {
            // 把配置本身也放进容器：此前它只在 Program.cs 里以局部变量存在，谁都注入不到，
            // 而 AddLoginServices 里的 typed client 与若干服务要读它。
            // 注册在这里（而不是 Program.cs）是为了让 AddAllServices 自成闭环——
            // 组合根守护测试（Tests/Extensions/ServiceRegistrationTests.cs）只跑 AddAllServices，
            // 放外面它就会缺这一项。
            services.AddSingleton(configuration);

            services.AddSingleton<ILanguageResolver, HeaderLanguageResolver>();
            services.AddSingleton<IApiMessageLocalizer, ApiMessageLocalizer>();
            services.AddScoped<ICodeService, CodeService>();
            services.AddScoped<IExamService, ExamService>();
            services.AddScoped<IProgramService, ProgramService>();
            services.AddScoped<IInfoService, InfoService>();

            // 登录：EduApi 自己做完整的 CAS/UAAP 握手与封禁编排，见 Login/。
            // 原先这里是两个 AddHttpClient<ILoginService/ILoginAdminService, HttpXxx>，
            // 把请求转给独立的 XAUAT.LoginApi；那个服务已退出调用链。
            services.AddLoginServices(configuration);

            // 支付：EduApi 不再内置实现，一律转发到 XAUAT.PaymentAPI
            var paymentApiBaseUrl = configuration.PaymentApiBaseUrl;
            if (string.IsNullOrEmpty(paymentApiBaseUrl))
            {
                throw new InvalidOperationException(
                    "缺少 PAYMENT_API_BASE_URL：EduApi 已不再内置支付实现，必须配置 XAUAT.PaymentAPI 的地址。");
            }

            // 刻意不挂 Polly 重试策略：PaymentAPI 的 503 承载"校园卡上游失败"的业务语义，
            // 重试它会让校园卡系统承受 4 倍压力（每次重试都会再打一次上游）。
            services.AddHttpClient<IPaymentService, HttpPaymentService>(client =>
            {
                client.BaseAddress = new Uri(paymentApiBaseUrl);
                client.Timeout = HttpTimeouts.EduSystem;
            });
            services.AddSingleton<IClassTimeService, ClassTimeService>();
            services.AddScoped<ICourseService, CourseService>();
            // 日历订阅：课程/考试数据复用上面的 CourseService/ExamService，
            // 只有 ICS 组装是本服务自己的（见 Services/CalendarService.cs）
            services.AddScoped<ICalendarService, CalendarService>();
            services.AddScoped<IScoreService, ScoreService>();
            services.AddScoped<IBusService, BusService>();
            services.AddScoped<IRedisService, RedisService>();
            services.AddScoped<ICookieCodeService, CookieCodeService>();
            services.AddScoped<ISchoolNavService, SchoolNavService>();
            services.AddScoped<IMapService, MapService>();
            services.AddScoped<IMapAdminTokenService, MapAdminTokenService>();
            services.AddScoped<IElectricityService, ElectricityService>();
            services.AddScoped<IElectricitySubscriptionService, ElectricitySubscriptionService>();
            services.AddScoped<IElectricityNotificationEmailService, ElectricityNotificationEmailService>();
            services.AddSingleton<IScheduleTimeService, ScheduleTimeService>();
            services.AddSingleton<IStudentRateLimitState, StudentRateLimitState>();
            services.AddScoped<IStudentRateLimitExecutor, StudentRateLimitExecutor>();
            services.AddScoped<EduCrawlerRateLimitFilter>();
            services.AddSingleton<IScorePersistenceQueue, ChannelScorePersistenceQueue>();
            services.AddHostedService<ScorePersistenceBackgroundService>();
            services.AddHostedService<ExamCleanupBackgroundService>();
            services.AddSingleton<IElectricityNotificationQueue, ChannelElectricityNotificationQueue>();
            services.AddHostedService<ElectricitySubscriptionMonitorBackgroundService>();
            services.AddHostedService<ElectricityNotificationBackgroundService>();

            return services;
        }

        /// <summary>
        /// 注册登录子系统（见 <c>Login/</c>）。从 <see cref="AddBusinessServices"/> 调用。
        /// <para>
        /// 整块是从 XAUAT.LoginApi 搬过来的：CAS 协议层 + 裸 <c>IDatabase</c> 存储 +
        /// 登录三级优先级编排 + 两级限流封禁。它自己那一套 Redis 键名**刻意不带任何前缀**
        /// （与 Flask 共用，见 <c>LoginCacheKeys</c>），因此**不能**改走本仓库的
        /// <c>ICacheService</c>——那会给键加 <c>EduApi:</c> 前缀并把值包成 JSON 信封。
        /// </para>
        /// </summary>
        public IServiceCollection AddLoginServices(ServiceConfiguration configuration)
        {
            // 单例：四个都是无状态的（状态要么在方法局部，要么在 IDatabase 里），
            // 与既有的 IStudentRateLimitState 同为单例一个道理。
            services.AddSingleton<ILoginRedisStore, LoginRedisStore>();
            services.AddSingleton<BanService>();
            services.AddSingleton<StatisticsService>();
            services.AddSingleton<SsoAuthService>();

            // 认证服务器客户端。**必须**关掉自动重定向与自动 cookie 管理：
            //   关自动重定向 —— 换票逻辑靠"逐跳捕获 Set-Cookie"（教务会话种在跳转链第 2 跳），
            //                    自动跟随会把中间响应丢掉，表现为所有人登录都报"未能获取有效会话"；
            //   关自动 cookie —— CookieContainer 的生命周期绑在池化的 HttpMessageHandler 上，
            //                    共享会让并发登录串号（A 的教务会话被 B 覆盖）。
            //                    cookie 由每次调用新建的 XauatCookieJar 显式管理。
            // 也刻意不挂 AddPolicyHandler(PollyExtensions.GetRetryPolicy())：那个策略基于
            // HandleTransientHttpError()，会把 5xx 一并重试，而登录只该重试传输层错误。
            services.AddHttpClient(XauatSsoClient.ClientName)
                .ConfigureHttpClient(client =>
                {
                    client.Timeout = XauatConstants.AuthServerTimeout;
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", XauatConstants.UserAgent);
                    client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", XauatConstants.AcceptHtml);
                    client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", XauatConstants.AcceptLanguage);
                    client.DefaultRequestHeaders.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
                })
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    AutomaticDecompression = DecompressionMethods.All,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
                });

            // 认证服务器需要会话化调用（先 GET 登录页拿 lt/execution，再 POST），
            // 因此单例持有 IHttpClientFactory 而不是 HttpClient
            services.AddSingleton<IXauatSsoClient, XauatSsoClient>();

            // Scoped（不是单例）：SsoLoginService 依赖 Scoped 的 ICookieCodeService，
            // 注册成单例就是 captive dependency——Development 下每次请求抛异常，
            // Production 下静默捕获它直到进程结束。EduApi 其余业务服务同为 Scoped。
            services.AddScoped<ILoginService, SsoLoginService>();
            services.AddScoped<ILoginAdminService, LocalLoginAdminService>();

            // ---- 通用统一身份认证（/v1/login/unified，见 Login/Unified/）
            // 与上面的 XAUAT 专用客户端**刻意分成两个命名客户端**：通用客户端要额外挂
            // "建连前丢弃内网地址"（UnifiedConnectGuard）并禁用代理，而把这两条加到
            // XauatAuthClient 上等于给既有的教务登录链路增加失败点，收益为零。
            services.AddSingleton(HostAllowList.Create(configuration.UnifiedAuthAllowedHosts));

            services.AddHttpClient(UnifiedSsoClient.ClientName)
                .ConfigureHttpClient(client =>
                {
                    client.Timeout = XauatConstants.AuthServerTimeout;
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", XauatConstants.UserAgent);
                    client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", XauatConstants.AcceptHtml);
                    client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", XauatConstants.AcceptLanguage);
                    client.DefaultRequestHeaders.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
                })
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    // 同 XauatAuthClient：重定向与 cookie 都由调用方手工管理（见 UnifiedSsoClient）
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    // 走代理时 TCP 连接由代理建立，ConnectCallback 里的地址校验就形同虚设，
                    // 而那道校验正是防 SSRF 的第二道闸——因此这里必须显式关掉代理。
                    UseProxy = false,
                    AutomaticDecompression = DecompressionMethods.All,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    ConnectCallback = UnifiedConnectGuard.ConnectAsync
                });

            services.AddSingleton<IUnifiedSsoClient, UnifiedSsoClient>();
            services.AddScoped<IUnifiedAuthService, UnifiedAuthService>();

            return services;
        }

        public IServiceCollection AddRateLimiterServices()
        {
            services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.OnRejected = async (context, cancellationToken) =>
                {
                    var httpContext = context.HttpContext;
                    var languageResolver = httpContext.RequestServices.GetRequiredService<ILanguageResolver>();
                    var messageLocalizer = httpContext.RequestServices.GetRequiredService<IApiMessageLocalizer>();
                    var language = languageResolver.Resolve(httpContext);

                    httpContext.Response.ContentType = "application/json";

                    var response = new
                    {
                        error = "rate_limited",
                        message = messageLocalizer.Get(language, ApiMessageKey.EduSystemRateLimited),
                        retryAfterSeconds = 1
                    };

                    await httpContext.Response.WriteAsJsonAsync(response, cancellationToken);
                };

                options.AddPolicy("EduCrawler", httpContext =>
                {
                    var partitionKey = httpContext.Request.CreateRequestRateLimitPartitionKey();

                    return RateLimitPartition.GetConcurrencyLimiter(partitionKey, _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = 8,
                        QueueLimit = 16,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    });
                });
            });

            return services;
        }

        /// <summary>
        /// 注册HTTP客户端服务
        /// </summary>
        /// <returns>服务集合</returns>
        public IServiceCollection AddHttpClientServices()
        {
            // 配置默认的HttpClient（不跳过SSL验证）
            services.AddHttpClient("DefaultClient")
                .ConfigureHttpClient(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(10); // 设置默认超时时间为10秒
                })
                .AddPolicyHandler(PollyExtensions.GetRetryPolicy()); // 添加重试策略

            // 配置专门用于BusController的HttpClient（跳过SSL验证）
            services.AddHttpClient("BusClient")
                .ConfigureHttpClient(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(15); // 设置超时时间为10秒
                })
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler()
                {
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                    MaxConnectionsPerServer = 100, // 设置每个服务器的最大连接数
                    AllowAutoRedirect = true, // 允许自动重定向
                    UseCookies = true // 使用Cookie
                })
                .AddPolicyHandler(PollyExtensions.GetRetryPolicy()); // 添加重试策略

            // 配置专门用于PaymentService的HttpClient（跳过SSL验证）
            services.AddHttpClient("PaymentClient")
                .ConfigureHttpClient(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(15); // 设置超时时间为15秒
                })
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler()
                {
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                    MaxConnectionsPerServer = 100, // 设置每个服务器的最大连接数
                    AllowAutoRedirect = true, // 允许自动重定向
                    UseCookies = true // 使用Cookie
                })
                .AddPolicyHandler(PollyExtensions.GetRetryPolicy()); // 添加重试策略

            // 配置专门用于外部API的HttpClient
            services.AddHttpClient("ExternalApiClient")
                .ConfigureHttpClient(client => { client.Timeout = TimeSpan.FromSeconds(10); })
                .AddPolicyHandler(PollyExtensions.GetRetryPolicy());

            return services;
        }

        /// <summary>
        /// 注册所有服务
        /// </summary>
        /// <param name="configuration">服务配置</param>
        /// <returns>服务集合</returns>
        public IServiceCollection AddAllServices(ServiceConfiguration configuration)
        {
            var serviceCollection = services
                .AddDatabaseServices(configuration.SqlConnectionString)
                .AddRedisServices(configuration.RedisConnectionString)
                .AddTestAccountServices(configuration.TestAccount)
                .AddCacheServices() // 添加缓存服务
                .AddRepositoryServices()
                .AddBusinessServices(configuration)
                .AddHttpClientServices()
                .AddRateLimiterServices();

            return serviceCollection;
        }
    }
}
