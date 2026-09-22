using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using EduApi.Data.Models;
using Polly;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Interfaces;

namespace XAUAT.EduApi.Services;

/// <summary>
/// <see cref="ILoginService"/> 的实现：把登录请求转给独立的登录服务。
/// <para>
/// 目标由 <c>LOGIN_API_BASE_URL</c> 决定，**留空时回落**到 Flask 的
/// <c>https://schedule.xauat.site</c>（在 DI 里解析掉，本类不感知这层分支）。
/// 两种实现共用同一个契约，因此本类只有一份代码。
/// </para>
/// <para>
/// 设计要点与 <see cref="HttpPaymentService"/> 相同：外界契约由两个 LoginController
/// 负责，本类只需 <b>忠实地抛出与原有实现相同类型的异常</b>。
/// </para>
/// <list type="bullet">
///   <item><c>200 + success=false</c> 或 <c>401</c> —— 语义是"用户名或密码错误"，
///     抛 <see cref="LoginFailedException"/>，控制器回 401。<b>前者必须保留</b>：
///     指向 Flask 时它是唯一的失败形态（Flask 登录失败也回 200）。</item>
///   <item><c>403 + banned</c> —— 语义是"账号被封"，还原为 <see cref="AccountBannedException"/>，
///     控制器回 429 + 封禁文案，并据此设置 Retry-After。</item>
///   <item>其余非 2xx（含登录服务自己的 500）—— 语义是"内部出错"，抛非
///     <see cref="LoginFailedException"/>，让控制器走 catch-all 回 500。</item>
///   <item>连接失败 —— 传输层异常直接冒泡，同样落到控制器的 catch-all（500）。</item>
/// </list>
/// </summary>
public class HttpLoginService(
    HttpClient httpClient,
    ICookieCodeService cookieCode,
    ILogger<HttpLoginService> logger,
    ITestAccountResolver? testAccountResolver = null,
    TimeSpan[]? retryDelays = null) : ILoginService
{
    /// <summary>
    /// 传输层失败后的重试退避：3 次，2/4/8 秒——与改造前的 <c>SSOLoginService</c> 逐字一致。
    /// </summary>
    /// <remarks>
    /// 显式列出来而不是把 <c>Math.Pow</c> 写在 lambda 里，是因为这是服务失败行为的一部分；
    /// 顺带让测试可以注入零退避，不必为一条用例真等 14 秒。
    /// </remarks>
    private static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8)
    ];

    /// <summary>
    /// 登录服务的契约是 camelCase，而这里的 <c>Deserialize</c> 把 snake_case 的
    /// <c>ban_reason</c>/<c>ban_until</c> 用 <see cref="JsonPropertyNameAttribute"/> 钉死了，
    /// 其余字段靠大小写不敏感兜住——否则会被静默反序列化成默认值。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <remarks>
    /// <paramref name="language"/> 未被使用：登录服务不做本地化（其上游接口不接受语言），
    /// 面向用户的文案一律由本服务根据异常类型生成。保留该参数是为了不改动控制器签名。
    /// </remarks>
    public async Task<LoginResponse> LoginAsync(string username, string password, string language = "zh")
    {
        if (testAccountResolver?.IsTestLogin(username, password) == true)
        {
            logger.LogInformation("用户 {Username} 命中测试账号登录", username);
            return testAccountResolver.CreateLoginResponse();
        }

        var result = await SendLoginAsync(username, password);

        var cookies = result.Cookies ?? "";

        // 上游说成功却没给 cookie，等价于失败，但先去打一次教务系统是白跑。
        // 判定结果与"拿空 cookie 去换取学号、换回空串"完全一致，只是少一次上游往返。
        if (string.IsNullOrEmpty(cookies))
        {
            logger.LogWarning("用户 {Username} 登录失败：登录服务未返回 cookie", username);
            throw new LoginFailedException();
        }

        // 学号不在登录服务的契约里（Flask 当年也不返回），仍然由 EduApi 拿 cookie 去教务系统反解。
        var studentId = await cookieCode.GetCode(cookies);
        if (string.IsNullOrEmpty(studentId) || studentId == "/student/login")
        {
            logger.LogWarning("用户 {Username} 登录失败，用户 Id {StudentId}", username, studentId);
            throw new LoginFailedException();
        }

        logger.LogInformation("用户 {Username} 登录成功", username);
        return new LoginResponse
        {
            Success = true,
            StudentId = studentId,
            Cookie = cookies
        };
    }

    /// <summary>
    /// 发起登录请求并把响应翻译成异常/结果。
    /// </summary>
    private async Task<LoginApiResponse> SendLoginAsync(string username, string password)
    {
        // 只重试传输层错误（连不上、超时），**不重试 5xx**——那意味着登录服务自己内部出错，
        // 重试无益，只会把 2+4+8 秒的退避加到调用方身上。
        // 也刻意不挂 AddPolicyHandler(PollyExtensions.GetRetryPolicy())：那个策略基于
        // HandleTransientHttpError()，会把 5xx 一并重试。
        var retryPolicy = Policy
            .Handle<HttpRequestException>()
            .Or<TaskCanceledException>()
            .WaitAndRetryAsync(retryDelays ?? DefaultRetryDelays);

        var (status, body) = await retryPolicy.ExecuteAsync(async () =>
        {
            using var response = await httpClient.PostAsJsonAsync("auth/login", new
            {
                username,
                password,
                school = "xauat"
            });

            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        });

        return Interpret(status, body, username);
    }

    /// <summary>
    /// 把 HTTP 状态码与响应体映射成结果或异常——本类的对外契约全部在这里。
    /// </summary>
    private LoginApiResponse Interpret(HttpStatusCode status, string body, string username)
    {
        var parsed = TryParse(body);

        if (status == HttpStatusCode.Forbidden && parsed?.Banned == true)
        {
            logger.LogWarning("用户 {Username} 已被登录服务封禁，理由 {Reason}，解封时刻 {UnbanAt}",
                username, parsed.BanReason, parsed.BanUntil);

            throw new AccountBannedException(
                parsed.Message ?? "账户已被暂时封禁",
                parsed.BanReason,
                parsed.BanUntil);
        }

        if (!IsSuccess(status))
        {
            // 401 是登录服务表达"用户名或密码错误"的方式，body 里的 message 就是它给出的原因。
            if (status == HttpStatusCode.Unauthorized)
            {
                logger.LogWarning("用户 {Username} 登录失败：{Message}", username, parsed?.Message);
                throw new LoginFailedException(parsed?.Message ?? "用户名或密码错误");
            }

            logger.LogError("登录服务返回 {StatusCode}: {Body}", (int)status, body);
            throw new InvalidOperationException($"登录服务返回 {(int)status}");
        }

        if (parsed is null)
        {
            logger.LogError("登录服务返回了无法解析的响应: {Body}", body);
            throw new InvalidOperationException("登录服务返回了无法解析的响应");
        }

        if (!parsed.Success)
        {
            // 指向 Flask 时这是**唯一的**失败形态：它登录失败同样回 200。
            logger.LogWarning("用户 {Username} 登录失败：{Message}", username, parsed.Message);
            throw new LoginFailedException(parsed.Message ?? "用户名或密码错误");
        }

        return parsed;
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and <= 299;

    /// <summary>
    /// 解析失败一律返回 null 而不是抛出：调用方需要根据状态码来区分
    /// "上游返回了坏数据"（500）与"上游正常地拒绝了这次登录"（401）。
    /// </summary>
    private LoginApiResponse? TryParse(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<LoginApiResponse>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "登录服务响应不是合法 JSON");
            return null;
        }
    }
}

/// <summary>
/// 登录服务 <c>POST /auth/login</c> 的响应。
/// 跨服务边界刻意各自持有 DTO 副本，不共享编译期模型。
/// </summary>
internal sealed class LoginApiResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }

    [JsonPropertyName("cookies")] public string? Cookies { get; set; }

    [JsonPropertyName("message")] public string? Message { get; set; }

    [JsonPropertyName("banned")] public bool? Banned { get; set; }

    [JsonPropertyName("ban_reason")] public string? BanReason { get; set; }

    /// <summary>解封时刻（unix 秒）。</summary>
    [JsonPropertyName("ban_until")] public long? BanUntil { get; set; }
}
