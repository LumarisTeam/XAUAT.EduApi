using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Tests.Services;

/// <summary>
/// <see cref="HttpPaymentService"/> 的测试。
/// <para>
/// 支付实现已完全移出 EduApi，本类是 EduApi 内唯一的 <c>IPaymentService</c> 实现，
/// 它到 HTTP 状态码的异常映射直接决定了对外契约（503 vs 500），因此是这里最关键的覆盖点。
/// </para>
/// </summary>
public class HttpPaymentServiceTests
{
    private const string BaseAddress = "http://paymentapi.test";

    [Fact]
    public async Task Login_ShouldMapToken_FromCamelCaseResponse()
    {
        // PaymentAPI 的内部契约是 camelCase；若代理侧没有大小写不敏感，
        // data 会被静默反序列化成 null，所有端点都会返回空 token 且不报错。
        var service = CreateService(HttpStatusCode.OK, """{"data":"token-123","code":0,"message":"ok"}""");

        var token = await service.Login("20239999");

        Assert.Equal("token-123", token);
    }

    [Fact]
    public async Task GetTurnover_ShouldMapBalanceToTotal()
    {
        const string body = """
            {"data":{"records":[{"turnoverType":"消费","datetimeStr":"2026-05-01 12:30:00",
            "resume":"测试食堂午餐","tranamt":18.5}],"balance":128.5},"code":0,"message":"ok"}
            """;
        var service = CreateService(HttpStatusCode.OK, body);

        var result = await service.GetTurnoverAsync("20239999");

        Assert.Equal(128.5, result.Total);
        var record = Assert.Single(result.Records);
        Assert.Equal("消费", record.TurnoverType);
        Assert.Equal(18.5, record.Tranamt);
    }

    [Fact]
    public async Task Login_ShouldPreserveUpstreamMessage_WhenServiceUnavailable()
    {
        // 503 承载"校园卡上游失败"的语义，必须还原成 PaymentServiceException，
        // 让控制器的 503 分支拿到原始 message。
        var service = CreateService(HttpStatusCode.ServiceUnavailable,
            """{"error":"payment_upstream_failed","message":"登录失败: 上游超时"}""");

        var ex = await Assert.ThrowsAsync<PaymentServiceException>(() => service.Login("20239999"));

        Assert.Equal("登录失败: 上游超时", ex.Message);
    }

    [Fact]
    public async Task Login_ShouldThrowNonPaymentServiceException_WhenUnexpectedStatus()
    {
        // 非 503 代表"内部出错"，必须抛非 PaymentServiceException，
        // 让控制器走 catch-all 分支回 500 + 本地化文案，而不是误报成上游失败。
        var service = CreateService(HttpStatusCode.InternalServerError,
            """{"error":"payment_unknown_error","message":"登录过程中发生未知错误"}""");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => service.Login("20239999"));

        Assert.IsNotType<PaymentServiceException>(ex);
    }

    [Fact]
    public async Task Login_ShouldThrowPaymentServiceException_WhenConnectionFails()
    {
        // 代理不可达 = 支付功能不可用，语义上等同于上游失败 → 503 而非 500
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var service = new HttpPaymentService(
            new HttpClient(handler.Object) { BaseAddress = new Uri(BaseAddress) },
            new Mock<ILogger<HttpPaymentService>>().Object);

        var ex = await Assert.ThrowsAsync<PaymentServiceException>(() => service.Login("20239999"));

        Assert.StartsWith("登录失败: ", ex.Message);
    }

    [Fact]
    public async Task Login_ShouldForwardLanguageHeader()
    {
        string? forwarded = null;
        var service = CreateService(HttpStatusCode.OK, """{"data":"t","code":0,"message":"ok"}""",
            request => forwarded = request.Headers.TryGetValues("x-language", out var values)
                ? string.Join(",", values)
                : null);

        await service.Login("20239999", "202411", "zh-Hant");

        Assert.Equal("zh-Hant", forwarded);
    }

    [Fact]
    public async Task Login_ShouldRequestCorrectPath()
    {
        string? path = null;
        var service = CreateService(HttpStatusCode.OK, """{"data":"t","code":0,"message":"ok"}""",
            request => path = request.RequestUri?.PathAndQuery);

        await service.Login("20239999", "202411");

        Assert.Equal("/v1/payment/20239999?password=202411", path);
    }

    [Fact]
    public async Task GetTurnover_ShouldRequestTurnoverPath()
    {
        string? path = null;
        var service = CreateService(HttpStatusCode.OK,
            """{"data":{"records":[],"balance":0},"code":0,"message":"ok"}""",
            request => path = request.RequestUri?.PathAndQuery);

        await service.GetTurnoverAsync("20239999", "202411");

        Assert.Equal("/v1/payment/20239999/turnover?password=202411", path);
    }

    private static HttpPaymentService CreateService(HttpStatusCode statusCode, string body,
        Action<HttpRequestMessage>? onRequest = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((request, _) =>
            {
                onRequest?.Invoke(request);
                return Task.FromResult(new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                });
            });

        return new HttpPaymentService(
            new HttpClient(handler.Object) { BaseAddress = new Uri(BaseAddress) },
            new Mock<ILogger<HttpPaymentService>>().Object);
    }
}
