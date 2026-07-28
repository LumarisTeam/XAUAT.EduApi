using EduApi.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using NpgsqlDataProtection;
using StackExchange.Redis;
using System.Threading.RateLimiting;
using XAUAT.EduApi.Caching;
using XAUAT.EduApi.Configuration;
using XAUAT.EduApi.Filters;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Localization;
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
        public IServiceCollection AddBusinessServices()
        {
            services.AddSingleton<ILanguageResolver, HeaderLanguageResolver>();
            services.AddSingleton<IApiMessageLocalizer, ApiMessageLocalizer>();
            services.AddScoped<ICodeService, CodeService>();
            services.AddScoped<ILoginService, SSOLoginService>();
            services.AddScoped<IExamService, ExamService>();
            services.AddScoped<IProgramService, ProgramService>();
            services.AddScoped<IInfoService, InfoService>();
            services.AddScoped<IPaymentService, PaymentService>();
            services.AddScoped<ICourseService, CourseService>();
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
                .AddBusinessServices()
                .AddHttpClientServices()
                .AddRateLimiterServices();

            return serviceCollection;
        }
    }
}
