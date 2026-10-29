using Microsoft.AspNetCore.Http;
using XAUAT.EduApi.Extensions;

namespace XAUAT.EduApi.Tests.Extensions;

/// <summary>
/// <c>x-loginMode</c> 的取值契约：缺失/0 = 本科生，1 = 研究生，其它 = 参数错误。
/// </summary>
public class LoginModeExtensionsTests
{
    [Theory]
    [InlineData(null, EduSystemMode.Undergraduate)]
    [InlineData("", EduSystemMode.Undergraduate)]
    [InlineData("0", EduSystemMode.Undergraduate)]
    [InlineData(" 0 ", EduSystemMode.Undergraduate)]
    [InlineData("1", EduSystemMode.Graduate)]
    [InlineData(" 1 ", EduSystemMode.Graduate)]
    public void TryGetEduSystemMode_ShouldResolveSupportedValues(
        string? headerValue, EduSystemMode expected)
    {
        var request = BuildRequest(headerValue);

        Assert.True(request.TryGetEduSystemMode(out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData("graduate")]
    [InlineData("undergraduate")]
    public void TryGetEduSystemMode_ShouldRejectUnknownValues(string headerValue)
    {
        var request = BuildRequest(headerValue);

        Assert.False(request.TryGetEduSystemMode(out _));
    }

    [Fact]
    public void GetGraduateAuthCookie_ShouldPreferXauatHeader()
    {
        var request = BuildRequest(null);
        request.Headers["xauat"] = "JSESSIONID=from-header";
        request.Headers.Cookie = "JSESSIONID=from-cookie";

        Assert.Equal("JSESSIONID=from-header", request.GetGraduateAuthCookie());
    }

    [Fact]
    public void GetGraduateAuthCookie_ShouldAcceptRawCookieHeader_WithoutUndergradMarker()
    {
        // 研究生系统的 cookie 没有 __pstsid__，不能沿用本科生的过滤条件
        var request = BuildRequest(null);
        request.Headers.Cookie = "JSESSIONID=abc; route=xyz";

        Assert.Equal("JSESSIONID=abc; route=xyz", request.GetGraduateAuthCookie());
    }

    [Fact]
    public void GetGraduateAuthCookie_ShouldIgnoreRiderTestTraffic()
    {
        var request = BuildRequest(null);
        request.Headers.Cookie = "Rider-Client=1";

        Assert.Equal("", request.GetGraduateAuthCookie());
    }

    private static HttpRequest BuildRequest(string? headerValue)
    {
        var context = new DefaultHttpContext();
        if (headerValue is not null)
        {
            context.Request.Headers[LoginModeExtensions.HeaderName] = headerValue;
        }

        return context.Request;
    }
}
