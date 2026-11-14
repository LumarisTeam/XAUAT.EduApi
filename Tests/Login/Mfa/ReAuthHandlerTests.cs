using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using XAUAT.EduApi.Login.Mfa;
using XAUAT.EduApi.Login.Xauat;
using XAUAT.EduApi.Tests.Login.TestSupport;

namespace XAUAT.EduApi.Tests.Login.Mfa;

public class ReAuthHandlerTests
{
    private const string AuthBase = "http://authserver.xauat.edu.cn";
    private const string AuthSecure = "https://authserver.xauat.edu.cn";
    private const string LoginUrl =
        AuthBase + "/authserver/login?service=https%3A%2F%2Fswjw.xauat.edu.cn%2Fstudent%2Fsso%2Flogin";
    private const string Service = "https://swjw.xauat.edu.cn/student/sso/login";
    private const string Salt = "0123456789abcdef";

    private static (ReAuthHandler Handler, ScriptedHttpHandler Http) Create()
    {
        var http = new ScriptedHttpHandler();
        return (new ReAuthHandler(new StubHttpClientFactory(http), NullLogger<ReAuthHandler>.Instance), http);
    }

    private static HttpClient Client(ScriptedHttpHandler http)
        => new StubHttpClientFactory(http).CreateClient("test");

    private static void ScriptTicketFlow(ScriptedHttpHandler http, string submitJson)
    {
        http.OnPostStartingWith(
            AuthSecure + "/authserver/reAuthCheck/reAuthSubmit.do",
            () => FakeResponses.Json(submitJson));
        http.OnGetStartingWith(
            AuthSecure + "/authserver/login?service=",
            () => FakeResponses.Redirect("https://swjw.xauat.edu.cn/student/sso/login?ticket=ST-1"));
        http.OnGetStartingWith(
            "https://swjw.xauat.edu.cn/student/sso/login",
            () => FakeResponses.Html("<html>退出</html>", HttpStatusCode.OK, "__pstsid__=abc; Path=/"));
    }

    [Fact]
    public async Task Complete_Password_UsesReAuthSaltAndFollowsTicket()
    {
        var (handler, http) = Create();
        ScriptTicketFlow(http, "{\"msg\":\"认证成功\",\"code\":\"reAuth_success\"}");

        var jar = new XauatCookieJar();
        var result = await handler.CompleteAsync(
            new Uri(LoginUrl), Service, 2, isMultifactor: false, trustDevice: true, jar,
            code: null, password: "pwd", encryptSalt: Salt,
            client: Client(http), allowHop: _ => true, cancellationToken: default);

        Assert.True(result.Success);
        var post = http.Sent.Single(request => request.Method == "POST");
        Assert.Contains("reAuthType=2", post.Body);
        Assert.Contains("skipTmpReAuth=true", post.Body);
        Assert.DoesNotContain("password=pwd", post.Body);
        Assert.Contains(result.Cookies, cookie => cookie.Name == "__pstsid__");
    }

    [Fact]
    public async Task Complete_Otp_UsesOtpCode()
    {
        var (handler, http) = Create();
        ScriptTicketFlow(http, "{\"msg\":\"认证成功\",\"code\":\"reAuth_success\"}");

        var result = await handler.CompleteAsync(
            new Uri(LoginUrl), Service, 10, false, true, new XauatCookieJar(),
            code: "123456", password: null, encryptSalt: null,
            client: Client(http), allowHop: _ => true, cancellationToken: default);

        Assert.True(result.Success);
        var post = http.Sent.Single(request => request.Method == "POST");
        Assert.Contains("reAuthType=10", post.Body);
        Assert.Contains("otpCode=123456", post.Body);
    }

    [Theory]
    [InlineData(3, "654321")]
    [InlineData(4, "445205")]
    public async Task Complete_DynamicCode_UsesDynamicCode(int reAuthType, string code)
    {
        var (handler, http) = Create();
        ScriptTicketFlow(http, "{\"msg\":\"认证成功\",\"code\":\"reAuth_success\"}");

        var result = await handler.CompleteAsync(
            new Uri(LoginUrl), Service, reAuthType, false, true, new XauatCookieJar(),
            code: code, password: null, encryptSalt: null,
            client: Client(http), allowHop: _ => true, cancellationToken: default);

        Assert.True(result.Success);
        var post = http.Sent.Single(request => request.Method == "POST");
        Assert.Contains($"reAuthType={reAuthType}", post.Body);
        Assert.Contains($"dynamicCode={code}", post.Body);
    }

    [Fact]
    public async Task Complete_ReAuthFailed_ReturnsInvalidCode()
    {
        var (handler, http) = Create();
        http.OnPostStartingWith(
            AuthSecure + "/authserver/reAuthCheck/reAuthSubmit.do",
            () => FakeResponses.Json("{\"msg\":\"认证失败\",\"code\":\"reAuth_failed\"}"));

        var result = await handler.CompleteAsync(
            new Uri(LoginUrl), Service, 4, false, true, new XauatCookieJar(),
            code: "445205", password: null, encryptSalt: null,
            client: Client(http), allowHop: _ => true, cancellationToken: default);

        Assert.False(result.Success);
        Assert.Equal(ReAuthFailure.InvalidCode, result.Failure);
    }

    [Fact]
    public async Task Start_WeCom_SendsWithWeChatAuthCodeTypeName()
    {
        var (handler, http) = Create();
        http.OnPostStartingWith(
            AuthSecure + "/authserver/reAuthCheck/changeReAuthType.do",
            () => FakeResponses.Json("{\"code\":\"1\",\"data\":{\"reAuthType\":\"4\",\"reAuthUserNameInput\":\"刘家林\"}}"));
        http.OnPostStartingWith(
            AuthSecure + "/authserver/dynamicCode/getDynamicCodeByReauth.do",
            () => FakeResponses.Json("{\"res\":\"wechat_success\",\"returnMessage\":\"验证码已发送成功\"}"));

        var page = new ReAuthPageParams(10, Service, Salt, false, true, 0, null, null, null, "20240001");
        var result = await handler.StartAsync(
            MfaFlowKind.Unified, "20240001", new Uri(LoginUrl), new XauatCookieJar(),
            page, MfaMethod.WeCom, trustDevice: true, entryUrl: null,
            sessionCacheKey: null, ticketCacheKey: null, cancellationToken: default);

        Assert.True(result.Success);
        Assert.Equal("wecom", result.Seed!.Method);
        Assert.Equal("刘家林", result.Seed.MaskedTarget);

        var change = http.Sent.Single(request => request.Url.Contains("changeReAuthType.do"));
        Assert.Contains("reAuthType=4", change.Body);
        var send = http.Sent.Single(request => request.Url.Contains("getDynamicCodeByReauth.do"));
        Assert.Contains("authCodeTypeName=reAuthWChatDynamicCodeType", send.Body);
    }

    [Fact]
    public async Task Start_WeChatQr_ParsesUuidStateAndImage()
    {
        var (handler, http) = Create();
        http.OnPostStartingWith(
            AuthSecure + "/authserver/reAuthCheck/changeReAuthType.do",
            () => FakeResponses.Json("{\"code\":\"1\",\"data\":{\"reAuthType\":\"8\"}}"));
        http.OnGetStartingWith(
            AuthSecure + "/authserver/combinedLogin.do",
            () => FakeResponses.Redirect(
                "https://open.weixin.qq.com/connect/qrconnect?appid=wx123&redirect_uri=https%3A%2F%2Fauthserver.xauat.edu.cn%2Fauthserver%2Fcallback&scope=snsapi_login&state=STATE123"));
        http.OnGetStartingWith(
            "https://open.weixin.qq.com/connect/qrconnect",
            () => FakeResponses.Html("<html><img src=\"/connect/qrcode/UUID12345\"/></html>"));
        http.OnGet(
            "https://open.weixin.qq.com/connect/qrcode/UUID12345",
            () => FakeResponses.Binary([1, 2, 3]));

        var page = new ReAuthPageParams(10, Service, Salt, false, true, 0, null, null, null, "20240001");
        var result = await handler.StartAsync(
            MfaFlowKind.Unified, "20240001", new Uri(LoginUrl), new XauatCookieJar(),
            page, MfaMethod.WeChatQr, trustDevice: true, entryUrl: null,
            sessionCacheKey: null, ticketCacheKey: null, cancellationToken: default);

        Assert.True(result.Success);
        Assert.Equal("wechat_qr", result.Seed!.Method);
        Assert.Equal("UUID12345", result.Seed.QrUuid);
        Assert.Equal("STATE123", result.Seed.QrState);
        Assert.False(string.IsNullOrEmpty(result.Seed.QrImageBase64));
        Assert.Contains(http.Sent, request =>
            request.Url.StartsWith(AuthSecure + "/authserver/combinedLogin.do", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PollQr_405_CompletesCallbackAndTicket()
    {
        var (handler, http) = Create();
        http.OnGetStartingWith(
            "https://lp.open.weixin.qq.com/connect/l/qrconnect",
            () => FakeResponses.Html("window.wx_errcode=405;window.wx_code='CODE123';"));
        http.OnGetStartingWith(
            AuthSecure + "/authserver/callback?code=CODE123&state=STATE123",
            () => FakeResponses.Redirect(
                AuthBase + "/authserver/login?service=https%3A%2F%2Fswjw.xauat.edu.cn%2Fstudent%2Fsso%2Flogin"));
        http.OnGetStartingWith(
            AuthSecure + "/authserver/login?service=",
            () => FakeResponses.Redirect("https://swjw.xauat.edu.cn/student/sso/login?ticket=ST-1"));
        http.OnGetStartingWith(
            "https://swjw.xauat.edu.cn/student/sso/login",
            () => FakeResponses.Html("<html>退出</html>", HttpStatusCode.OK, "__pstsid__=abc; Path=/"));

        var result = await handler.PollQrAsync(
            new Uri(LoginUrl), "UUID12345", "STATE123", new XauatCookieJar(),
            Client(http), allowHop: _ => true, cancellationToken: default);

        Assert.Equal(QrPollStatus.Success, result.Status);
        Assert.NotNull(result.Completion);
        Assert.Contains(result.Completion!.Cookies, cookie => cookie.Name == "__pstsid__");
        Assert.Contains(http.Sent, request =>
            request.Url.StartsWith(AuthSecure + "/authserver/login?service=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PollQr_LandingTimeoutAfterTicket_ReturnsSuccessWithTicketUrl()
    {
        var (handler, http) = Create();
        http.OnGetStartingWith(
            "https://lp.open.weixin.qq.com/connect/l/qrconnect",
            () => FakeResponses.Html("window.wx_errcode=405;window.wx_code='CODE123';"));
        http.OnGetStartingWith(
            AuthSecure + "/authserver/callback?code=CODE123&state=STATE123",
            () => FakeResponses.Redirect(
                AuthBase + "/authserver/login?service=https%3A%2F%2Fzhfwpt.xauat.edu.cn%3A8080%2FlogCas"));
        http.OnGetStartingWith(
            AuthSecure + "/authserver/login?service=",
            () => FakeResponses.Redirect("https://zhfwpt.xauat.edu.cn:8080/logCas?ticket=ST-1"));
        http.OnGetStartingWith(
            "https://zhfwpt.xauat.edu.cn:8080/logCas?ticket=",
            () => throw new TaskCanceledException("simulated landing timeout"));

        var result = await handler.PollQrAsync(
            new Uri(LoginUrl), "UUID12345", "STATE123", new XauatCookieJar(),
            Client(http), allowHop: _ => true, cancellationToken: default);

        Assert.Equal(QrPollStatus.Success, result.Status);
        Assert.NotNull(result.Completion);
        Assert.Equal(
            "https://zhfwpt.xauat.edu.cn:8080/logCas?ticket=ST-1",
            result.Completion!.LandingUrl!.ToString());
    }

    [Fact]
    public async Task PollQr_408_ReturnsPending()
    {
        var (handler, http) = Create();
        http.OnGetStartingWith(
            "https://lp.open.weixin.qq.com/connect/l/qrconnect",
            () => FakeResponses.Html("window.wx_errcode=408;"));

        var result = await handler.PollQrAsync(
            new Uri(LoginUrl), "UUID12345", "STATE123", new XauatCookieJar(),
            Client(http), allowHop: _ => true, cancellationToken: default);

        Assert.Equal(QrPollStatus.Pending, result.Status);
    }
}
