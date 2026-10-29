using System.Text;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Tests.Services;

/// <summary>
/// 研究生课表页的解码：<c>gmis</c> 的 Content-Type 不带 charset，
/// 一旦页面是 GBK 而按 UTF-8 解码，中文会静默变乱码（正则匹配不到任何课程）。
/// </summary>
public class GraduateHtmlDecoderTests
{
    private const string Chinese = "课程:高等数学";

    static GraduateHtmlDecoderTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [Fact]
    public void Decode_ShouldUseUtf8_WhenNoCharsetDeclared()
    {
        var bytes = Encoding.UTF8.GetBytes(Chinese);

        Assert.Equal(Chinese, GraduateHtmlDecoder.Decode(bytes, null));
    }

    [Fact]
    public void Decode_ShouldHonorDeclaredGbkCharset()
    {
        var bytes = Encoding.GetEncoding("gb18030").GetBytes(Chinese);

        Assert.Equal(Chinese, GraduateHtmlDecoder.Decode(bytes, "gbk"));
    }

    [Fact]
    public void Decode_ShouldSniffMetaCharset_WhenContentTypeHasNone()
    {
        var html = $"<html><head><meta charset=\"gbk\"></head><body>{Chinese}</body></html>";
        var bytes = Encoding.GetEncoding("gb18030").GetBytes(html);

        var decoded = GraduateHtmlDecoder.Decode(bytes, null);

        Assert.Contains(Chinese, decoded);
    }

    [Fact]
    public void Decode_ShouldFallBackToGb18030_WhenGbkHasNoHint()
    {
        var bytes = Encoding.GetEncoding("gb18030").GetBytes(Chinese);

        Assert.Equal(Chinese, GraduateHtmlDecoder.Decode(bytes, null));
    }
}
