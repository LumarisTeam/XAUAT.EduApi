using System.Net;
using System.Text;
using EduApi.Data.Models;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Tests.Services;

/// <summary>
/// <see cref="HttpLoginService"/> 的测试。
/// <para>
/// 登录实现已移出 EduApi（转给 XAUAT.LoginApi，留空时回落 Flask），本类是 EduApi 内唯一的
/// <c>ILoginService</c> 实现。它到异常类型的映射直接决定了对外状态码
/// （401 / 429 / 500），因此是这里最关键的覆盖点。
/// </para>
/// </summary>
public class HttpLoginServiceTests
{
    private const string BaseAddress = "http://loginapi.test";

    /// <summary>零退避：等价于"重试 3 次但不等"，否则传输失败用例要真等 14 秒。</summary>
    private static readonly TimeSpan[] NoDelay = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];

    // ------------------------------------------------------------------ 成功路径

    [Fact]
    public async Task Login_ShouldReturnStudentIdResolvedFromCookies()
    {
        var cookieCode = new Mock<ICookieCodeService>();
        cookieCode.Setup(x => x.GetCode("__pstsid__=abc;")).ReturnsAsync("20239999");

        var service = CreateService(
            HttpStatusCode.OK, """{"success":true,"cookies":"__pstsid__=abc;"}""",
            cookieCode: cookieCode);

        var result = await service.LoginAsync("20239999", "pw");

        Assert.True(result.Success);
        Assert.Equal("20239999", result.StudentId);
        Assert.Equal("__pstsid__=abc;", result.Cookie);
    }

    [Fact]
    public async Task Login_ShouldRequestAuthLoginPath_WithHardcodedXauatSchool()
    {
        // 路径与请求体必须与改造前逐字一致——LoginApi 与 Flask 共用这一个契约。
        string? path = null;
        string? body = null;

        var cookieCode = new Mock<ICookieCodeService>();
        cookieCode.Setup(x => x.GetCode(It.IsAny<string>())).ReturnsAsync("20239999");

        var service = CreateService(
            HttpStatusCode.OK, """{"success":true,"cookies":"c"}""",
            cookieCode: cookieCode,
            onRequest: async request =>
            {
                path = request.RequestUri?.PathAndQuery;
                body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            });

        await service.LoginAsync("20239999", "pw");

        Assert.Equal("/auth/login", path);
        Assert.Contains("\"username\":\"20239999\"", body);
        Assert.Contains("\"password\":\"pw\"", body);
        Assert.Contains("\"school\":\"xauat\"", body);
    }

    // ------------------------------------------------------------------ 401：用户名或密码错误

    [Fact]
    public async Task Login_ShouldThrowLoginFailed_WhenFlaskShapeReturns200WithSuccessFalse()
    {
        // 指向 Flask 时这是**唯一**的失败形态：它登录失败同样回 200。
        // 这条分支丢了的话，灰度期切回 Flask 会把"密码错误"报成 500。
        var service = CreateService(
            HttpStatusCode.OK, """{"success":false,"message":"账号或密码错误"}""");

        var ex = await Assert.ThrowsAsync<LoginFailedException>(() => service.LoginAsync("20239999", "bad"));

        Assert.Equal("账号或密码错误", ex.Message);
    }

    [Fact]
    public async Task Login_ShouldSurfaceUpstreamMessage_WhenApiReturns401()
    {
        var service = CreateService(
            HttpStatusCode.Unauthorized, """{"success":false,"message":"账号或密码错误"}""");

        var ex = await Assert.ThrowsAsync<LoginFailedException>(() => service.LoginAsync("20239999", "bad"));

        Assert.Equal("账号或密码错误", ex.Message);
    }

    [Fact]
    public async Task Login_ShouldThrowLoginFailedWithoutTouchingCookieCode_WhenUpstreamGivesNoCookies()
    {
        // 上游说成功却没给 cookie —— 等价于失败，但不该再打一次教务系统去换学号。
        var cookieCode = new Mock<ICookieCodeService>();

        var service = CreateService(
            HttpStatusCode.OK, """{"success":true}""",
            cookieCode: cookieCode);

        await Assert.ThrowsAsync<LoginFailedException>(() => service.LoginAsync("20239999", "pw"));

        cookieCode.Verify(x => x.GetCode(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Login_ShouldThrowLoginFailed_WhenCookieCodeYieldsNoStudentId()
    {
        var cookieCode = new Mock<ICookieCodeService>();
        cookieCode.Setup(x => x.GetCode(It.IsAny<string>())).ReturnsAsync("");

        var service = CreateService(
            HttpStatusCode.OK, """{"success":true,"cookies":"c"}""",
            cookieCode: cookieCode);

        await Assert.ThrowsAsync<LoginFailedException>(() => service.LoginAsync("20239999", "pw"));
    }

    // ------------------------------------------------------------------ 403：账号封禁

    [Fact]
    public async Task Login_ShouldThrowAccountBanned_WithReasonAndUnbanTime()
    {
        var unbanAt = DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds();

        var service = CreateService(HttpStatusCode.Forbidden, $$"""
            {"success":false,"message":"账户已被暂时封禁，请稍后重试或联系管理员",
             "banned":true,"ban_reason":"连续触发登录限流","ban_until":{{unbanAt}}}
            """);

        var ex = await Assert.ThrowsAsync<AccountBannedException>(() => service.LoginAsync("20239999", "pw"));

        Assert.Equal("账户已被暂时封禁，请稍后重试或联系管理员", ex.Message);
        Assert.Equal("连续触发登录限流", ex.Reason);
        Assert.Equal(unbanAt, ex.UnbanAt);

        // Retry-After 必须来自解封时刻，而不是本地限流窗口推算出的 60 秒——
        // 否则一个被封 24 小时的账号会被告知"60 秒后再试"。
        Assert.InRange(ex.RetryAfterSeconds!.Value, 86395, 86400);
    }

    [Fact]
    public async Task Login_ShouldNotTreat403WithoutBannedFlagAsBan()
    {
        // 没有 banned 标记的 403 不是封禁（例如反代配错了），
        // 必须落到"内部出错 → 500"而不是给用户报一个不存在的封禁。
        var service = CreateService(HttpStatusCode.Forbidden, """{"message":"forbidden"}""");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => service.LoginAsync("20239999", "pw"));

        Assert.IsNotType<AccountBannedException>(ex);
        Assert.IsNotType<LoginFailedException>(ex);
    }

    // ------------------------------------------------------------------ 500：内部出错

    [Fact]
    public async Task Login_ShouldThrowNonLoginFailedException_WhenUnexpectedStatus()
    {
        var service = CreateService(HttpStatusCode.InternalServerError, "boom");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => service.LoginAsync("20239999", "pw"));

        Assert.IsNotType<LoginFailedException>(ex);
        Assert.IsNotType<AccountBannedException>(ex);
        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task Login_ShouldNotRetryServerError()
    {
        // 刻意不挂 AddPolicyHandler(GetRetryPolicy())：那个策略基于 HandleTransientHttpError()，
        // 会把 5xx 一并重试。5xx 是登录服务自己内部出错，重试只会把 14 秒退避压到调用方身上。
        var attempts = 0;
        var service = CreateService(HttpStatusCode.InternalServerError, "boom",
            onRequest: _ => { attempts++; return Task.CompletedTask; }, retryDelays: NoDelay);

        await Assert.ThrowsAnyAsync<Exception>(() => service.LoginAsync("20239999", "pw"));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Login_ShouldThrowUnparseable_WhenSuccessBodyIsNotJson()
    {
        var service = CreateService(HttpStatusCode.OK, "<html>gateway</html>", "text/html");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => service.LoginAsync("20239999", "pw"));

        Assert.IsNotType<LoginFailedException>(ex);
    }

    // ------------------------------------------------------------------ 传输层

    [Fact]
    public async Task Login_ShouldRetryTransportFailures_ThenPropagate()
    {
        var attempts = 0;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((_, _) =>
            {
                attempts++;
                throw new HttpRequestException("Connection refused");
            });

        var service = new HttpLoginService(
            new HttpClient(handler.Object) { BaseAddress = new Uri(BaseAddress) },
            new Mock<ICookieCodeService>().Object,
            new Mock<ILogger<HttpLoginService>>().Object,
            retryDelays: NoDelay);

        // 连接失败直接冒泡，由控制器的 catch-all 回 500 + 本地化文案
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => service.LoginAsync("20239999", "pw"));

        Assert.IsNotType<LoginFailedException>(ex);
        Assert.Equal(4, attempts); // 首次 + 3 次重试
    }

    // ------------------------------------------------------------------ 测试账号旁路

    [Fact]
    public async Task Login_ShouldShortCircuit_WhenTestAccountMatches()
    {
        var expected = new LoginResponse
        {
            Success = true,
            StudentId = "20239999",
            Cookie = "__pstsid__=frontend-test-marker;"
        };

        var resolver = new Mock<ITestAccountResolver>();
        resolver.Setup(x => x.IsTestLogin("frontend-test", "frontend-test-password")).Returns(true);
        resolver.Setup(x => x.CreateLoginResponse()).Returns(expected);

        var attempts = 0;
        var cookieCode = new Mock<ICookieCodeService>();

        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((_, _) =>
            {
                attempts++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"success":true,"cookies":"c"}""", Encoding.UTF8, "application/json")
                });
            });

        var service = new HttpLoginService(
            new HttpClient(handler.Object) { BaseAddress = new Uri(BaseAddress) },
            cookieCode.Object,
            new Mock<ILogger<HttpLoginService>>().Object,
            resolver.Object);

        var result = await service.LoginAsync("frontend-test", "frontend-test-password");

        Assert.Equal(expected.StudentId, result.StudentId);
        Assert.Equal(expected.Cookie, result.Cookie);

        // 命中旁路就必须一次网络都不出，也不去教务系统换学号
        Assert.Equal(0, attempts);
        cookieCode.Verify(x => x.GetCode(It.IsAny<string>()), Times.Never);
    }

    // ------------------------------------------------------------------ 辅助

    private static HttpLoginService CreateService(
        HttpStatusCode statusCode,
        string body,
        string contentType = "application/json",
        Func<HttpRequestMessage, Task>? onRequest = null,
        Mock<ICookieCodeService>? cookieCode = null,
        TimeSpan[]? retryDelays = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(async (request, _) =>
            {
                if (onRequest is not null)
                {
                    await onRequest(request);
                }

                return new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(body, Encoding.UTF8, contentType)
                };
            });

        return new HttpLoginService(
            new HttpClient(handler.Object) { BaseAddress = new Uri(BaseAddress) },
            (cookieCode ?? new Mock<ICookieCodeService>()).Object,
            new Mock<ILogger<HttpLoginService>>().Object,
            retryDelays: retryDelays ?? NoDelay);
    }
}
