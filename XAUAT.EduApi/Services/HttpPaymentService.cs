using System.Net;
using System.Text.Json;
using EduApi.Data.Models;
using XAUAT.EduApi.Exceptions;

namespace XAUAT.EduApi.Services;

/// <summary>
/// <see cref="IPaymentService"/> 的反向代理实现：把请求转给独立的 XAUAT.PaymentAPI。
/// <para>
/// 设计要点：外界契约由 <c>PaymentController</c>（V1 与 Old）负责，本类只需
/// <b>忠实地抛出与直连实现相同类型的异常</b>，契约就由构造保证，而不必假设两边的 JSON 形状一致。
/// </para>
/// <list type="bullet">
///   <item>PaymentAPI 返回 503 —— 语义是"校园卡上游失败"，还原为 <see cref="PaymentServiceException"/>，
///     让控制器回 503 + 原始 message。</item>
///   <item>其他非 2xx（含 PaymentAPI 自身 500）—— 语义是"内部出错"，抛非 <see cref="PaymentServiceException"/>，
///     让控制器走 catch-all 分支回 500 + 本地化文案。</item>
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

    public async Task<string> Login(string cardNum, string password = "202411", string language = "zh")
    {
        var body = await SendAsync(
            $"v1/payment/{Uri.EscapeDataString(cardNum)}?password={Uri.EscapeDataString(password)}",
            language,
            "登录失败");

        return JsonSerializer.Deserialize<ApiResponse<string>>(body, JsonOptions)?.Data ?? "";
    }

    public async Task<PaymentData> GetTurnoverAsync(string cardNum, string password = "202411", string language = "zh")
    {
        var body = await SendAsync(
            $"v1/payment/{Uri.EscapeDataString(cardNum)}/turnover?password={Uri.EscapeDataString(password)}",
            language,
            "获取消费记录失败");

        var response = JsonSerializer.Deserialize<ApiResponse<PaymentTurnoverResult>>(body, JsonOptions);

        return new PaymentData
        {
            Records = response?.Data?.Records ?? [],
            // PaymentAPI 的 Balance 对应 EduApi 对外契约里的 Total
            Total = response?.Data?.Balance ?? 0
        };
    }

    private async Task<string> SendAsync(string path, string language, string errorPrefix)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.TryAddWithoutValidation("x-language", language);
            response = await httpClient.SendAsync(request);
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
                throw new PaymentServiceException(ExtractMessage(body) ?? $"{errorPrefix}: 上游返回 503");
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("PaymentAPI 返回 {StatusCode}: {Body}", (int)response.StatusCode, body);
                throw new InvalidOperationException(
                    ExtractMessage(body) ?? $"PaymentAPI 返回 {(int)response.StatusCode}");
            }

            return body;
        }
    }

    private static string? ExtractMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("message", out var message) ? message.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
