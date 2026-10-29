using System.Text;
using System.Text.RegularExpressions;

namespace XAUAT.EduApi.Services;

/// <summary>
/// 研究生课表页的字符集解码。
/// <para>
/// <b>为什么不能直接 <c>ReadAsStringAsync()</c></b>：<c>gmis</c> 的响应头是
/// <c>Content-Type: text/html</c>，<b>不带 charset</b>。.NET 在这种情况下按 UTF-8 解码，
/// 而 Python 的 <c>requests.text</c> 会在没有 charset 时回退到字符集嗅探。
/// 一旦页面其实是 GBK，中文会变成乱码，正则里的 <c>课程/教室</c> 全部匹配不上，
/// 表现为"登录成功但课表为空"——不会抛任何异常。
/// </para>
/// <para>
/// 解析顺序与浏览器/requests 一致：Content-Type 声明 → 页面 <c>&lt;meta charset&gt;</c> →
/// UTF-8 合法性检测 → GB18030。GB18030 是 GBK/GB2312 的超集，统一用它兜底。
/// </para>
/// </summary>
internal static class GraduateHtmlDecoder
{
    private static readonly Regex MetaCharsetPattern = new(
        """charset\s*=\s*["']?\s*([\w-]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>GBK/GB2312 在 .NET Core 里需要显式注册代码页提供程序。</summary>
    static GraduateHtmlDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static async Task<string> DecodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return Decode(bytes, response.Content.Headers.ContentType?.CharSet);
    }

    /// <summary>按响应字节与（可能缺失的）charset 声明解码。</summary>
    internal static string Decode(byte[] bytes, string? declaredCharset)
    {
        return ResolveEncoding(bytes, declaredCharset).GetString(bytes);
    }

    private static Encoding ResolveEncoding(byte[] bytes, string? declaredCharset)
    {
        if (!string.IsNullOrWhiteSpace(declaredCharset) &&
            TryGetEncoding(declaredCharset, out var declared))
        {
            return declared;
        }

        var sniffed = SniffMetaCharset(bytes);
        if (sniffed is not null && TryGetEncoding(sniffed, out var fromMeta))
        {
            return fromMeta;
        }

        return IsValidUtf8(bytes)
            ? Encoding.UTF8
            : TryGetEncoding("gb18030", out var gb) ? gb : Encoding.UTF8;
    }

    /// <summary>从正文前 4KB 的 <c>&lt;meta charset=…&gt;</c> 里嗅探字符集。</summary>
    private static string? SniffMetaCharset(byte[] bytes)
    {
        var length = Math.Min(bytes.Length, 4096);
        var head = Encoding.ASCII.GetString(bytes, 0, length);
        var match = MetaCharsetPattern.Match(head);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool TryGetEncoding(string name, out Encoding encoding)
    {
        var normalized = name.Trim().Trim('"', '\'', ' ');

        // gb2312 / gbk 都是 GB18030 的子集，统一按超集解码可避免生僻字被替换
        if (normalized.Equals("gb2312", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("gbk", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "gb18030";
        }

        try
        {
            encoding = Encoding.GetEncoding(normalized);
            return true;
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
            return false;
        }
    }

    private static bool IsValidUtf8(byte[] bytes)
    {
        try
        {
            _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
