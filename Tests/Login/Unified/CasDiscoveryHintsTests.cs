using System.Text;
using XAUAT.EduApi.Login.Unified;

namespace XAUAT.EduApi.Tests.Login.UnifiedAuth;

/// <summary>
/// 页面级跳转线索的提取。
/// <para>
/// 这一层的存在理由见 <see cref="CasDiscoveryHints"/>：真实链路里教务系统会先回一个
/// 200 的前端空壳，SSO 入口只写在 JS 里。提取不到线索，整条通用认证链路就断在那一跳，
/// 且报错（"未跳转到统一身份认证登录页"）看起来像用户填错了地址。
/// </para>
/// </summary>
public class CasDiscoveryHintsTests
{
    private static string ReadFixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestFixtures", name), Encoding.UTF8);

    /// <summary>真实树维前端登录壳（已脱敏）：入口要从 <c>CONTEXT_PATH</c> + <c>ssoLinkList</c> 拼出来。</summary>
    [Fact]
    public void FindCandidates_OnRealFrontEndShell_ShouldYieldSiteSsoEntry()
    {
        var candidates = CasDiscoveryHints.FindCandidates(ReadFixture("supwisdom-login-shell.html"));

        Assert.Contains("/student/sso/login", candidates);
    }

    /// <summary>
    /// 真实页面上有 <c>top.location.href = self.location.href</c>（防被嵌 iframe）。
    /// 它右边不是引号字符串，绝不能被当成跳转目标——否则会一直请求页面自己。
    /// </summary>
    [Fact]
    public void FindCandidates_OnRealFrontEndShell_ShouldIgnoreIframeSelfGuard()
    {
        var candidates = CasDiscoveryHints.FindCandidates(ReadFixture("supwisdom-login-shell.html"));

        Assert.DoesNotContain("self.location.href", candidates);
        Assert.DoesNotContain(candidates, candidate => candidate.Contains("location.href"));
    }

    [Fact]
    public void FindCandidates_ShouldReadMetaRefresh()
    {
        var html = """<html><head><meta http-equiv="refresh" content="0;url=/portal/login"></head></html>""";

        Assert.Equal(new[] { "/portal/login" }, CasDiscoveryHints.FindCandidates(html));
    }

    [Fact]
    public void FindCandidates_ShouldReadMetaRefreshWithSpacesAndSingleQuotes()
    {
        var html = "<html><head><meta http-equiv='refresh' content='5; URL = /cas/login'></head></html>";

        Assert.Contains("/cas/login", CasDiscoveryHints.FindCandidates(html));
    }

    [Theory]
    [InlineData("""<script>window.location.href = "/cas/login";</script>""", "/cas/login")]
    [InlineData("""<script>location.href='/cas/login'</script>""", "/cas/login")]
    [InlineData("""<script>location.replace("/sso/entry")</script>""", "/sso/entry")]
    [InlineData("""<script>window.location = "https://sso.example.edu.cn/login"</script>""",
        "https://sso.example.edu.cn/login")]
    public void FindCandidates_ShouldReadJsLocationAssignments(string html, string expected)
    {
        Assert.Contains(expected, CasDiscoveryHints.FindCandidates(html));
    }

    [Fact]
    public void FindCandidates_ShouldKeepAbsoluteSsoEntryAsIs()
    {
        var html = """
            <script>window.CONTEXT_PATH='/student'; data.ssoLinkList = [{'entiry':'https://sso.example.edu.cn/login'}];</script>
            """;

        Assert.Contains("https://sso.example.edu.cn/login", CasDiscoveryHints.FindCandidates(html));
    }

    [Fact]
    public void FindCandidates_ShouldFallBackToRootRelative_WhenContextPathMissing()
    {
        var html = """<script>data.ssoLinkList = [{"entiry":"/sso/login"}];</script>""";

        Assert.Contains("/sso/login", CasDiscoveryHints.FindCandidates(html));
    }

    [Theory]
    [InlineData("<html><body>公开页面，没有登录入口</body></html>")]
    [InlineData("<html><body><a href=\"/news\">新闻</a></body></html>")]
    [InlineData("")]
    public void FindCandidates_ShouldReturnEmpty_WhenPageHasNoHints(string html)
    {
        Assert.Empty(CasDiscoveryHints.FindCandidates(html));
    }

    [Fact]
    public void FindCandidates_ShouldDeduplicateAndKeepOrder()
    {
        var html = """
            <script>window.CONTEXT_PATH='/student';
            location.href = "/student/sso/login";
            data.ssoLinkList = [{'entiry':'/sso/login'}];</script>
            """;

        var candidates = CasDiscoveryHints.FindCandidates(html);

        Assert.Equal(new[] { "/student/sso/login" }, candidates);
    }
}
