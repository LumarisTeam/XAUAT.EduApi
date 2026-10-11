using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Login.Unified;
using XAUAT.EduApi.Tests.Login.TestSupport;

namespace XAUAT.EduApi.Tests.Login.UnifiedAuth;

/// <summary>
/// 通用统一身份认证客户端的整条链路：目标地址 → 发现 CAS 登录页 → 提交凭据 → 跳回取 cookie。
/// <para>
/// 这些用例保护的是"只给一个 URL 就能登录"这个承诺：任何一步的判定放宽或收紧，
/// 表现都是"某些学校的统一认证登不上"，而不会在编译期暴露。
/// </para>
/// </summary>
public class UnifiedSsoClientTests
{
    private const string TargetUrl = "https://swjw.xauat.edu.cn/student/index";
    private const string AuthServerPrefix = "http://authserver.xauat.edu.cn/authserver/login";
    private const string CallbackUrl = "https://swjw.xauat.edu.cn/student/sso/login?ticket=ST-1";
    private const string Username = "alice";
    private const string Password = "my-password";

    private const string LoginPageHtml = """
        <html><body>
        <form id="casLoginForm" method="post">
          <input type="hidden" name="lt" value="LT-1234">
          <input type="hidden" name="execution" value="e1s1">
          <input type="hidden" name="_eventId" value="submit">
          <input type="hidden" name="type" value="userNameLogin">
          <input id="pwdEncryptSalt" value="rjBFAaHsNkKAhpoi">
          <input type="text" name="username" id="username">
          <input type="password" name="password" id="password">
        </form>
        </body></html>
        """;

    private const string LandingPageHtml = "<html><body>欢迎，张三 <a href=\"/logout\">退出</a></body></html>";

    /// <summary>
    /// 教务系统自己的前端登录壳（真实链路里的那一跳，见
    /// <c>Tests/TestFixtures/supwisdom-login-shell.html</c>）：200、没有表单、
    /// SSO 入口只写在 JS 里。发现逻辑必须能从这里挖出下一跳。
    /// </summary>
    private const string FrontEndShellHtml = """
        <html><head><title>登入页面</title></head><body>
        <script>
          window.CONTEXT_PATH = '/student';
          window.onload = function () {
            if (top.location.href != self.location.href) { top.location.href = self.location.href; }
            data.ssoLinkList = [{'entiry':'/sso/login'}];
          }
        </script>
        </body></html>
        """;

    private readonly ScriptedHttpHandler _handler = new();

    private UnifiedSsoClient CreateClient(HostAllowList? allowList = null)
        => new(new StubHttpClientFactory(_handler),
            allowList ?? HostAllowList.Create(null),
            NullLogger<UnifiedSsoClient>.Instance);

    private Task<UnifiedAuthOutcome> LoginAsync(string url = TargetUrl)
        => CreateClient().LoginAsync(new UnifiedAuthCommand(url, Username, Password), CancellationToken.None);

    /// <summary>把目标站点 → CAS 登录页 → 凭据提交 → 跳回落地 这条正常链路铺好。</summary>
    private void ScriptHappyPath(string postResponse = "")
    {
        _handler
            .OnGet(TargetUrl, () => FakeResponses.Redirect(AuthServerPrefix + "?service=https%3A%2F%2Fswjw.xauat.edu.cn%2Fstudent%2Fsso%2Flogin"))
            .OnGetStartingWith(AuthServerPrefix, () => FakeResponses.Html(LoginPageHtml, HttpStatusCode.OK, "JSESSIONID=node1"));

        if (postResponse.Length > 0)
        {
            _handler.OnPostStartingWith(AuthServerPrefix,
                () => FakeResponses.Html(postResponse, HttpStatusCode.OK));
            return;
        }

        _handler
            .OnPostStartingWith(AuthServerPrefix,
                () => FakeResponses.Redirect(CallbackUrl, "CASTGC=TGT-1234; Path=/"))
            .OnGet(CallbackUrl,
                () => FakeResponses.Html(LandingPageHtml, HttpStatusCode.OK, "__pstsid__=abc123; Path=/"));
    }

    // ================================================================ 正常链路

    [Fact]
    public async Task Login_ShouldDiscoverAuthServerAndReturnLandingCookie()
    {
        ScriptHappyPath();

        var outcome = await LoginAsync();

        Assert.True(outcome.Success);
        Assert.Equal(UnifiedAuthFailure.None, outcome.Failure);
        Assert.Equal(CallbackUrl, outcome.LandingUrl);
        Assert.StartsWith(AuthServerPrefix, outcome.AuthServerUrl);
        Assert.Contains("service=", outcome.AuthServerUrl, StringComparison.Ordinal);

        // 只回落地站点的 cookie：认证服务器的 CASTGC 混进来会被业务系统判为异常
        Assert.Equal("__pstsid__=abc123", outcome.LandingCookie);

        // 全量明细里两份 cookie 都要在，且归属 host 正确
        var cookies = outcome.Cookies!;
        Assert.Contains(cookies, c => c is { Host: "swjw.xauat.edu.cn", Name: "__pstsid__", Value: "abc123" });
        Assert.Contains(cookies, c => c is { Host: "authserver.xauat.edu.cn", Name: "CASTGC", Value: "TGT-1234" });
    }

    [Fact]
    public async Task Login_ShouldSubmitUaapFormAndReuseSessionCookie()
    {
        ScriptHappyPath();

        await LoginAsync();

        var post = Assert.Single(_handler.Sent, request => request.Method == "POST");
        var form = ParseForm(post.Body!);

        Assert.Equal(Username, form["username"]);

        // 密码必须被盐值加密过（UAAP 的 AES），绝不能是明文
        Assert.NotEqual(Password, form["password"]);
        Assert.NotEmpty(form["password"]);
        Assert.True(Convert.TryFromBase64String(form["password"], new byte[256], out _), "加密后的密码应是 Base64");

        // 登录页的隐藏域原样透传
        Assert.Equal("LT-1234", form["lt"]);
        Assert.Equal("e1s1", form["execution"]);
        Assert.Equal("submit", form["_eventId"]);
        Assert.Equal("userNameLogin", form["type"]);

        // 金智模板的固定字段
        Assert.Equal("userNameLogin", form["cllt"]);
        Assert.Equal("generalLogin", form["dllt"]);
        Assert.Equal("true", form["rememberMe"]);

        // 登录页那一跳种下的 JSESSIONID 必须带上，否则 CAS 认为表单过期
        Assert.Contains("JSESSIONID=node1", post.CookieHeader, StringComparison.Ordinal);
    }

    // ================================================================ 失败分支

    [Fact]
    public async Task Login_ShouldReportInvalidCredentials_WhenAuthServerRejects()
    {
        ScriptHappyPath("<html><body><span id=\"msg\">用户名或密码错误</span></body></html>");

        var outcome = await LoginAsync();

        Assert.False(outcome.Success);
        Assert.Equal(UnifiedAuthFailure.InvalidCredentials, outcome.Failure);
        Assert.Contains("用户名或密码错误", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_ShouldReportInvalidCredentials_WhenBouncedBackToLoginPage()
    {
        // 票没被认：跳回目标站点时又被弹回登录页
        _handler
            .OnGet(TargetUrl, () => FakeResponses.Redirect(AuthServerPrefix + "?service=x"))
            .OnGetStartingWith(AuthServerPrefix, () => FakeResponses.Html(LoginPageHtml))
            .OnPostStartingWith(AuthServerPrefix, () => FakeResponses.Redirect(CallbackUrl, "CASTGC=TGT-1"))
            .OnGet(CallbackUrl, () => FakeResponses.Html(LoginPageHtml));

        var outcome = await LoginAsync();

        Assert.Equal(UnifiedAuthFailure.InvalidCredentials, outcome.Failure);
    }

    [Fact]
    public async Task Login_ShouldReportNotCasProtected_WhenTargetNeedsNoAuthentication()
    {
        _handler.OnGet(TargetUrl, () => FakeResponses.Html("<html><body>公开页面</body></html>"));

        var outcome = await LoginAsync();

        Assert.False(outcome.Success);
        Assert.Equal(UnifiedAuthFailure.NotCasProtected, outcome.Failure);
    }

    [Fact]
    public async Task Login_ShouldThrow_WhenHostNotInAllowList()
    {
        var exception = await Assert.ThrowsAsync<UnifiedAuthRequestException>(
            () => LoginAsync("https://evil.example.com/secret"));

        Assert.Equal(ApiMessageKey.UnifiedAuthHostNotAllowed, exception.MessageKey);
    }

    [Fact]
    public async Task Login_ShouldThrow_WhenSchemeIsNotHttp()
    {
        var exception = await Assert.ThrowsAsync<UnifiedAuthRequestException>(
            () => LoginAsync("file:///etc/passwd"));

        Assert.Equal(ApiMessageKey.UnifiedAuthInvalidTarget, exception.MessageKey);
    }

    /// <summary>
    /// 真实链路的关键一跳：教务系统先回 200 的前端空壳（SSO 入口写在 JS 里），
    /// 只跟 302 的实现会在这里误报"该地址未跳转到统一身份认证登录页"。
    /// </summary>
    [Fact]
    public async Task Login_ShouldFollowPageLevelHints_WhenSiteReturnsFrontEndShell()
    {
        _handler
            .OnGet(TargetUrl, () => FakeResponses.Redirect("https://swjw.xauat.edu.cn/student/login?refer=x"))
            .OnGetStartingWith("https://swjw.xauat.edu.cn/student/login",
                () => FakeResponses.Html(FrontEndShellHtml))
            .OnGet("https://swjw.xauat.edu.cn/student/sso/login",
                () => FakeResponses.Redirect(AuthServerPrefix + "?service=https%3A%2F%2Fswjw.xauat.edu.cn%2Fstudent%2Fsso%2Flogin"))
            .OnGetStartingWith(AuthServerPrefix, () => FakeResponses.Html(LoginPageHtml))
            .OnPostStartingWith(AuthServerPrefix, () => FakeResponses.Redirect(CallbackUrl, "CASTGC=TGT-1"))
            .OnGet(CallbackUrl, () => FakeResponses.Html(LandingPageHtml, HttpStatusCode.OK, "__pstsid__=abc123"));

        var outcome = await LoginAsync();

        Assert.True(outcome.Success);
        Assert.Equal("__pstsid__=abc123", outcome.LandingCookie);
        // 凭据必须落到认证服务器上，而不是停在教务站点自己的页面
        Assert.StartsWith(AuthServerPrefix, outcome.AuthServerUrl);
    }

    [Fact]
    public async Task Login_ShouldFollowMultiHopDiscovery_AndReturnLandingCookie()
    {        // 真实链路通常不止一跳：目标页 → 站点自己的 SSO 入口 → CAS 登录页。
        // 发现逻辑必须能一路跟下去，而不是只看第一跳。
        _handler
            .OnGet(TargetUrl, () => FakeResponses.Redirect("https://swjw.xauat.edu.cn/student/sso/login"))
            .OnGet("https://swjw.xauat.edu.cn/student/sso/login",
                () => FakeResponses.Redirect(AuthServerPrefix + "?service=https%3A%2F%2Fswjw.xauat.edu.cn%2Fstudent%2Fsso%2Flogin"))
            .OnGetStartingWith(AuthServerPrefix, () => FakeResponses.Html(LoginPageHtml))
            .OnPostStartingWith(AuthServerPrefix, () => FakeResponses.Redirect(CallbackUrl, "CASTGC=TGT-1"))
            .OnGet(CallbackUrl, () => FakeResponses.Html(LandingPageHtml, HttpStatusCode.OK, "SESSION=zzz"));

        var outcome = await LoginAsync();

        Assert.True(outcome.Success);
        Assert.Equal("SESSION=zzz", outcome.LandingCookie);
        Assert.Equal(CallbackUrl, outcome.LandingUrl);
    }

    // ================================================================ 小工具

    /// <summary>把 <c>application/x-www-form-urlencoded</c> 请求体还原成字典。</summary>
    private static Dictionary<string, string> ParseForm(string body)
        => body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "");
}
