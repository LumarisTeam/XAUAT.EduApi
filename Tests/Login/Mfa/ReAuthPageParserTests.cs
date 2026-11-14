using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Login.Mfa;

namespace XAUAT.EduApi.Tests.Login.Mfa;

public class ReAuthPageParserTests
{
    private const string Page = """
        <html><body><script>
        var reAuthParams = {
            "isSleepAccount": "1",
            "reAuthType": "10",
            "isMultifactor": null,
            "_isBindOtp": true,
            "_otpUrl": null,
            "ipAnomaly": 0,
            "service": "https://qiwei.xauat.edu.cn/cas",
            "reAuthUserId": "20240001",
            "pwdEncryptSalt": "salt123",
            "otpCodeRequired": "请输入6位有效的安全令牌"
        };
        </script></body></html>
        """;

    [Fact]
    public void Parse_ShouldExtractAllFields()
    {
        var page = ReAuthPageParser.Parse(Page);

        Assert.Equal(10, page.ReAuthType);
        Assert.Equal("https://qiwei.xauat.edu.cn/cas", page.Service);
        Assert.Equal("salt123", page.EncryptSalt);
        Assert.False(page.IsMultifactor);
        Assert.True(page.IsSleepAccount);
        Assert.Equal(0, page.IpAnomaly);
        Assert.Equal("请输入6位有效的安全令牌", page.OtpCodeRequired);
        Assert.True(page.IsBindOtp);
        Assert.Equal("20240001", page.ReAuthUserId);
    }

    [Fact]
    public void Parse_JsonEscapedSlashesInService_AreUnescaped()
    {
        // 线上 reAuthParams 是 JSON 字面量，service 里的 / 被写成 \/。
        // 不反转义会让 combinedLogin.do 的 success 参数非法，上游回 200 HTML 而不是微信 302。
        const string page = """
            <script>
            var reAuthParams = {
                "reAuthType": "8",
                "service": "https:\/\/zhfwpt.xauat.edu.cn:8080\/logCas",
                "isMultifactor": null,
                "pwdEncryptSalt": "q1hVinmcu2i7fPyF"
            };
            </script>
            """;

        var parsed = ReAuthPageParser.Parse(page);

        Assert.Equal(8, parsed.ReAuthType);
        Assert.Equal("https://zhfwpt.xauat.edu.cn:8080/logCas", parsed.Service);
        Assert.Equal("q1hVinmcu2i7fPyF", parsed.EncryptSalt);
    }

    [Fact]
    public void Parse_MissingTypeOrService_ThrowsProtocolException()
    {
        Assert.Throws<MfaProtocolException>(() =>
            ReAuthPageParser.Parse("<html>no reAuthParams here</html>"));
    }

    [Fact]
    public void DetectReAuthAndPasswordExpiredPages()
    {
        Assert.True(ReAuthPageParser.LooksLikeReAuthHtml(Page));
        Assert.True(ReAuthPageParser.IsReAuthUrl(new Uri(
            "https://authserver.xauat.edu.cn/authserver/reAuthCheck/reAuthLoginView.do?service=x")));
        Assert.True(ReAuthPageParser.LooksLikePasswordExpiredHtml(
            "<div>当前密码已过期。为了您的账号安全，管理员要求您90天修改一次密码</div>"));
        Assert.True(ReAuthPageParser.IsPasswordExpiredUrl(new Uri(
            "https://authserver.xauat.edu.cn/authserver/improveInfo/improveUserInfo.do")));
        Assert.False(ReAuthPageParser.LooksLikeReAuthHtml("<html>普通登录页</html>"));
    }
}
