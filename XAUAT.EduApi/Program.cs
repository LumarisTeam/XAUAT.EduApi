using DotNetEnv;
using EduApi.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using XAUAT.EduApi.Logging;
using XAUAT.EduApi.Caching;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.OpenApi;

LoadDotEnvIfPresent();

// 先创建builder对象
var builder = WebApplication.CreateBuilder(args);
var logStore = new InMemoryLogStore();
builder.Services.AddSingleton<ILogStore>(logStore);

// 统一日志配置，并保留最近日志供 /Logs 分页查询。
var serilogLogger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console(
        outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .WriteTo.File(
        Environment.GetEnvironmentVariable("SERILOG_FILE_PATH") ?? "logs/log-.txt",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 31,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .WriteTo.Sink(new InMemoryLogSink(logStore))
    .CreateLogger();

builder.Logging
    .ClearProviders()
    .SetMinimumLevel(LogLevel.Information)
    .AddSerilog(serilogLogger);

// 基础服务配置
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi(options =>
{
    options.AddOperationTransformer<LanguageHeaderOperationTransformer>();
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(
    Options.Create(EnvironmentVariableHelper.BuildElectricitySubscriptionOptions()));
builder.Services.AddSingleton(
    Options.Create(EnvironmentVariableHelper.BuildSmtpOptions()));
builder.Services.AddSingleton(
    Options.Create(EnvironmentVariableHelper.BuildMapAdminOptions()));

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(origin =>
                origin.EndsWith(".zeabur.app") || // 支持所有 zeabur.app 子域名
                origin.EndsWith(".xauat.site") || // 支持所有 xauat.site 子域名
                origin.StartsWith("http://localhost")) // 支持本地开发环境
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials(); // 如果需要发送凭据（如cookies、认证头等）
    });
});

// 获取配置
var serviceConfig = ServiceConfiguration.FromEnvironment();

// 模块化服务注册
builder.Services.AddAllServices(serviceConfig);

// MCP 服务注册（HTTP transport，支持本地和网络访问）
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

// 配置中间件管道
app.UseErrorHandling()
    .UseRateLimiter()
    .UseAuthorization()
    .UseCustomCors();

// 配置端点
app.ConfigureApiEndpoints();

// 应用数据库迁移
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var context = services.GetRequiredService<IDbContextFactory<EduContext>>().CreateDbContext();
    var logger = services.GetRequiredService<ILogger<Program>>();

    var pending = context.Database.GetPendingMigrations();
    var enumerable = (pending as string[]) ?? pending.ToArray();

    if (enumerable.Length != 0)
    {
        logger.LogInformation("Pending migrations: {Migrations}", string.Join(", ", enumerable));
        try
        {
            await context.Database.MigrateAsync();
            logger.LogInformation("Migrations applied successfully.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Migration error: {Message}", ex.Message);
            throw; // 让异常冒泡，方便定位问题
        }
    }
    else
    {
        logger.LogInformation("No pending migrations.");
    }

    await context.SaveChangesAsync();
    await context.DisposeAsync();
}

// 执行缓存预热（异步执行，不阻塞启动）
_ = Task.Run(async () =>
{
    using var scope = app.Services.CreateScope();
    var cacheService = scope.ServiceProvider.GetRequiredService<ICacheService>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        logger.LogInformation("Starting cache warmup...");
        var startTime = DateTime.Now;

        // 添加关键业务数据预热任务
        // 示例：添加学期信息预热
        cacheService.AddWarmupTask(new CacheWarmupItem
        {
            Key = "cache:semesters",
            ValueFactory = () =>
                Task.FromResult<object>(
                    new List<string> { "2025-2026-1", "2025-2026-2", "2024-2025-1", "2024-2025-2" }),
            Expiration = TimeSpan.FromDays(7),
            Priority = 10,
            BusinessTags = ["core", "semester"]
        });

        // 执行预热
        var successCount = await cacheService.ExecuteWarmupAsync();

        var elapsed = DateTime.Now - startTime;
        logger.LogInformation(
            "Cache warmup completed successfully. Warmed up {SuccessCount} items in {ElapsedMilliseconds}ms",
            successCount, elapsed.TotalMilliseconds);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error during cache warmup: {Message}", ex.Message);
    }
});

// 注册应用程序停止事件，用于清理资源
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    // 确保所有日志都被正确写入
    Log.CloseAndFlush();
};

// 注册应用程序取消事件
var cts = new CancellationTokenSource();
app.Lifetime.ApplicationStopping.Register(cts.Cancel);

app.Run();

// 确保所有日志都被正确写入
Log.CloseAndFlush();
return;

void LoadDotEnvIfPresent()
{
    var currentDirectory = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (currentDirectory is not null)
    {
        var dotEnvPath = Path.Combine(currentDirectory.FullName, ".env");
        if (File.Exists(dotEnvPath))
        {
            Env.NoClobber().Load(dotEnvPath);
            return;
        }

        currentDirectory = currentDirectory.Parent;
    }
}
