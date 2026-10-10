using XAUAT.EduApi.Configuration;

namespace XAUAT.EduApi.Extensions;

/// <summary>
/// 服务配置类
/// 用于封装所有服务配置信息
/// </summary>
public class ServiceConfiguration
{
    /// <summary>
    /// SQL连接字符串
    /// </summary>
    public string? SqlConnectionString { get; set; }

    /// <summary>
    /// Redis连接字符串
    /// </summary>
    public string? RedisConnectionString { get; set; }

    /// <summary>
    /// 是否启用Prometheus监控
    /// </summary>
    public bool EnablePrometheus { get; set; } = true;

    /// <summary>
    /// 是否启用日志
    /// </summary>
    public bool EnableLogging { get; set; } = true;

    /// <summary>
    /// 测试账号配置
    /// </summary>
    public TestAccountOptions TestAccount { get; set; } = new();

    /// <summary>
    /// XAUAT.PaymentAPI 的基础地址（如 http://xauat-paymentapi:8080）。
    /// 支付逻辑已不在 EduApi 内，这是必需的配置项——缺失时启动会直接失败。
    /// </summary>
    public string? PaymentApiBaseUrl { get; set; }

    /// <summary>
    /// 从环境变量中创建ServiceConfiguration实例
    /// </summary>
    /// <returns>ServiceConfiguration实例</returns>
    public static ServiceConfiguration FromEnvironment()
    {
        return new ServiceConfiguration
        {
            SqlConnectionString = EnvironmentVariableHelper.GetString("SQL"),
            RedisConnectionString = EnvironmentVariableHelper.GetString("REDIS", "Redis"),
            EnablePrometheus = EnvironmentVariableHelper.GetBoolOrDefault(true, "PROMETHEUS_ENABLED", "Prometheus__Enabled"),
            EnableLogging = EnvironmentVariableHelper.GetBoolOrDefault(true, "LOGGING_ENABLED", "Logging__Enabled"),
            TestAccount = EnvironmentVariableHelper.BuildTestAccountOptions(),
            PaymentApiBaseUrl = EnvironmentVariableHelper.GetString("PAYMENT_API_BASE_URL", "PaymentApi__BaseUrl")
        };
    }
}
