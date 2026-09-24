using System.Net;
using System.Text;
using System.Text.Json;
using Polly;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Interfaces;

namespace XAUAT.EduApi.Services;

/// <summary>
/// <see cref="ILoginAdminService"/> 的实现：把运维/统计请求转给独立的 XAUAT.LoginApi。
/// <para>
/// 设计要点与 <see cref="HttpPaymentService"/>、<see cref="HttpLoginService"/> 相同：
/// 外界契约由 <c>LoginOpsController</c> 负责，本类只需忠实地把上游结果翻译成
/// "结果或异常"。
/// </para>
/// <list type="bullet">
///   <item>连不上 / 超时 —— 还原为 <see cref="LoginOpsUnavailableException"/>，控制器回 503。</item>
///   <item>其余非 2xx（含 LoginApi 自己的 500）—— 语义是"内部出错"，抛非领域异常
///     （<see cref="InvalidOperationException"/>），让控制器走 catch-all 回 500。</item>
///   <item>解封的 <b>400</b> 是例外：那是"该用户当前没被封"这一业务结果，不是内部错误，
///     解析成 <see cref="UnbanResult.Success"/> 为 false 的正常返回值。</item>
/// </list>
/// </summary>
/// <remarks>
/// 重试策略与 <see cref="HttpLoginService"/> 一致：只重试传输层错误
/// （<see cref="HttpRequestException"/> / <see cref="TaskCanceledException"/>），
/// <b>不重试 5xx</b>——那意味着登录服务自己内部出错，重试无益。也刻意不在 DI 里挂
/// <c>AddPolicyHandler(PollyExtensions.GetRetryPolicy())</c>：那个策略基于
/// <c>HandleTransientHttpError()</c>，会把 5xx 一并重试。
/// </remarks>
public class HttpLoginAdminService(
    HttpClient httpClient,
    ILogger<HttpLoginAdminService> logger,
    ServiceConfiguration configuration,
    TimeSpan[]? retryDelays = null) : ILoginAdminService
{
    /// <summary>传输层失败后的重试退避：3 次，2/4/8 秒——与登录链路逐字一致。</summary>
    /// <remarks>
    /// 做成字段而不是内联 <c>Math.Pow</c>，是为了让测试注入空数组拿到零退避，
    /// 不必为一条用例真等 14 秒。
    /// </remarks>
    private static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8)
    ];

    /// <summary>
    /// 上游契约是 snake_case，而这里的 <c>Deserialize</c> 没有传入命名策略，因此必须显式
    /// 大小写不敏感。注意它 <b>兜不住 snake_case</b>——<c>created_at</c> 与 <c>CreatedAt</c>
    /// 是两个不同的名字，那些字段靠 <c>[JsonPropertyName]</c> 钉死。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<int> GetUserCountAsync()
    {
        var body = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "user_count"),
            "获取活跃用户数失败");

        var parsed = Deserialize<LoginUserCount>(body);

        return parsed.Count;
    }

    public async Task<BanLogPage> GetBanLogsAsync(string? school, string? username)
    {
        // 见 LoginOpsNotConfiguredException 的注释：封禁日志的 JSON 接口只有 XAUAT.LoginApi 有，
        // 回落状态下打过去只会 404。与其让它冒泡成一句含糊的 500，不如在这里说清楚。
        if (string.IsNullOrEmpty(configuration.LoginApiBaseUrl))
        {
            throw new LoginOpsNotConfiguredException(
                "未配置 LOGIN_API_BASE_URL：封禁日志只有 XAUAT.LoginApi 提供，Flask 侧没有对应的 JSON 接口");
        }

        var body = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, BanLogsPath(school, username)),
            "获取封禁日志失败");

        return Deserialize<BanLogPage>(body);
    }

    public async Task<UnbanResult> UnbanAsync(string school, string username)
    {
        var (status, body) = await ExecuteAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "security/ban_status")
            {
                Content = new StringContent(
                    // 匿名的属性名就是 school / username，这里没有命名策略，按原样写出
                    JsonSerializer.Serialize(new { school, username }), Encoding.UTF8, "application/json")
            },
            "解封失败");

        // 200 是"已解封"，400 是"该用户当前未被封禁"——两者都是上游的正常业务应答，
        // 区别只在 success 字段。其余状态码才是真的出错。
        if (status is HttpStatusCode.OK or HttpStatusCode.BadRequest)
        {
            return Deserialize<UnbanResult>(body);
        }

        logger.LogError("LoginApi 返回 {StatusCode}: {Body}", (int)status, body);
        throw new InvalidOperationException($"LoginApi 返回 {(int)status}");
    }

    private static string BanLogsPath(string? school, string? username)
    {
        var query = new List<string>(2);

        if (!string.IsNullOrEmpty(school)) query.Add($"school={Uri.EscapeDataString(school)}");
        if (!string.IsNullOrEmpty(username)) query.Add($"username={Uri.EscapeDataString(username)}");

        return query.Count == 0
            ? "security/ban_logs"
            : $"security/ban_logs?{string.Join('&', query)}";
    }

    private async Task<string> SendAsync(Func<HttpRequestMessage> createRequest, string errorPrefix)
    {
        var (status, body) = await ExecuteAsync(createRequest, errorPrefix);

        if (!IsSuccess(status))
        {
            // 刻意不是领域异常：让控制器走 catch-all 回 500，与 HttpPaymentService 一致
            logger.LogError("LoginApi 返回 {StatusCode}: {Body}", (int)status, body);
            throw new InvalidOperationException($"LoginApi 返回 {(int)status}");
        }

        return body;
    }

    private async Task<(HttpStatusCode Status, string Body)> ExecuteAsync(
        Func<HttpRequestMessage> createRequest, string errorPrefix)
    {
        var retryPolicy = Policy
            .Handle<HttpRequestException>()
            .Or<TaskCanceledException>()
            .WaitAndRetryAsync(retryDelays ?? DefaultRetryDelays);

        try
        {
            return await retryPolicy.ExecuteAsync(async () =>
            {
                // HttpRequestMessage 不能跨重试复用，因此每次尝试都重新构造
                using var request = createRequest();
                using var response = await httpClient.SendAsync(request);

                return (response.StatusCode, await response.Content.ReadAsStringAsync());
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new LoginOpsUnavailableException($"{errorPrefix}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 解析失败一律抛异常而不是返回默认值：对封禁日志或用户数来说，
    /// "解析不出来"与"真的是 0 条"必须能被区分开，否则页面会安静地显示成"没有数据"。
    /// </summary>
    /// <remarks>
    /// 两种失败形态（body 不是合法 JSON、body 是字面量 <c>null</c>）统一收敛成
    /// <see cref="InvalidOperationException"/>，与 <see cref="HttpLoginService"/> 的做法一致。
    /// 关键是**不能**抛 <see cref="LoginOpsUnavailableException"/>——那会被控制器映射成 503
    /// "稍后重试"，而解析失败是实打实的 bug，必须回 500。
    /// </remarks>
    private static T Deserialize<T>(string body) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions)
                   ?? throw new InvalidOperationException("LoginApi 返回了无法解析的响应");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("LoginApi 返回了无法解析的响应", ex);
        }
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and <= 299;
}
