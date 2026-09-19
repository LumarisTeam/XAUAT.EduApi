using System.Net;
using System.Text.Json;
using EduApi.Data.Models;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Interfaces;

namespace XAUAT.EduApi.Services;

/// <summary>
/// <see cref="IPaymentService"/> 的实现：把请求转给独立的 XAUAT.PaymentAPI。
/// <para>
/// 设计要点：外界契约由 <c>PaymentController</c>（V1 与 Old）负责，本类只需
/// <b>忠实地抛出与原有实现相同类型的异常</b>，契约就由构造保证。
/// </para>
/// <list type="bullet">
///   <item>PaymentAPI 返回 503 —— 语义是"校园卡上游失败"，其纯文本 body 即上游原始消息，
///     还原为 <see cref="PaymentServiceException"/>，让控制器回 503 + 该消息。</item>
///   <item>其他非 2xx（含 PaymentAPI 自身 500）—— 语义是"内部出错"，抛非
///     <see cref="PaymentServiceException"/>，让控制器走 catch-all 分支回 500 + 本地化文案。</item>
///   <item>连接失败 —— 语义是"支付功能不可用"，同样还原为 <see cref="PaymentServiceException"/>（503 而非 500）。</item>
/// </list>
/// </summary>
public class HttpPaymentService(HttpClient httpClient, ILogger<HttpPaymentService> logger) : IPaymentService
{
    /// <summary>
    /// PaymentAPI 的内部契约是 camelCase，而这里的 <c>Deserialize</c> 没有传入命名策略，
    /// 因此必须显式大小写不敏感，否则所有字段都会静默反序列化成 null。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <remarks>
    /// <paramref name="language"/> 未被使用：PaymentAPI 不做本地化（上游接口不接受语言），
    /// 面向用户的文案一律由本服务根据异常类型生成。保留该参数是为了不改动控制器签名。
    /// </remarks>
    public async Task<string> Login(string cardNum, string password = "202411", string language = "zh")
    {
        var body = await SendAsync(TokenPath(cardNum, password), "登录失败");

        return JsonSerializer.Deserialize<PaymentTokenResponse>(body, JsonOptions)?.Token ?? "";
    }

    /// <inheritdoc cref="Login"/>
    public async Task<PaymentData> GetTurnoverAsync(string cardNum, string password = "202411", string language = "zh")
    {
        var body = await SendAsync(TurnoverPath(cardNum, password), "获取消费记录失败");

        var result = JsonSerializer.Deserialize<PaymentTurnoverResult>(body, JsonOptions);

        return new PaymentData
        {
            Records = result?.Records ?? [],
            // PaymentAPI 的 Balance 对应 EduApi 对外契约里的 Total
            Total = result?.Balance ?? 0
        };
    }

    private static string TokenPath(string cardNum, string password)
        => $"payment/{Uri.EscapeDataString(cardNum)}/token?password={Uri.EscapeDataString(password)}";

    private static string TurnoverPath(string cardNum, string password)
        => $"payment/{Uri.EscapeDataString(cardNum)}/turnover?password={Uri.EscapeDataString(password)}";

    private async Task<string> SendAsync(string path, string errorPrefix)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.GetAsync(path);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new PaymentServiceException($"{errorPrefix}: {ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();

            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                // 503 的 body 就是上游原始消息（纯文本，没有再包一层 JSON）
                throw new PaymentServiceException(
                    string.IsNullOrWhiteSpace(body) ? $"{errorPrefix}: 上游返回 503" : body.Trim());
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("PaymentAPI 返回 {StatusCode}: {Body}", (int)response.StatusCode, body);
                throw new InvalidOperationException($"PaymentAPI 返回 {(int)response.StatusCode}");
            }

            return body;
        }
    }
}

/// <summary>
/// PaymentAPI <c>/payment/{cardNum}/token</c> 的响应。
/// 跨服务边界刻意各自持有 DTO 副本，不共享编译期模型。
/// </summary>
internal sealed record PaymentTokenResponse(string Token);
