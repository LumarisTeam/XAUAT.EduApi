using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using XAUAT.EduApi.Exceptions;

namespace XAUAT.EduApi.Login.Mfa;

internal sealed record ReAuthPageParams(
    int ReAuthType,
    string Service,
    string? EncryptSalt,
    bool IsMultifactor,
    bool IsSleepAccount,
    int IpAnomaly,
    string? OtpCodeRequired,
    bool? IsBindOtp,
    string? OtpUrl,
    string? ReAuthUserId);

/// <summary>
/// re-auth 页面解析与识别。页面里的 <c>reAuthParams</c> 是 JS 对象而不是严格 JSON，
/// 因此逐字段正则抓取，不依赖完整反序列化。
/// </summary>
internal static partial class ReAuthPageParser
{
    public const string PasswordChangeUrl =
        "https://authserver.xauat.edu.cn/authserver/improveInfo/improveUserInfo.do";

    public static bool IsReAuthUrl(Uri? uri)
        => uri is not null &&
           uri.AbsolutePath.Contains("/reAuthCheck/", StringComparison.OrdinalIgnoreCase);

    public static bool LooksLikeReAuthHtml(string html)
        => html.Contains("reAuthParams", StringComparison.Ordinal) ||
           html.Contains("reAuthLoginView.do", StringComparison.Ordinal);

    public static bool IsPasswordExpiredUrl(Uri? uri)
        => uri is not null &&
           uri.AbsolutePath.Contains("/improveInfo/", StringComparison.OrdinalIgnoreCase);

    public static bool LooksLikePasswordExpiredHtml(string html)
        => html.Contains("当前密码已过期", StringComparison.Ordinal) ||
           (html.Contains("完善资料", StringComparison.Ordinal) &&
            html.Contains("密码", StringComparison.Ordinal));

    public static ReAuthPageParams Parse(string html)
    {
        var typeMatch = ReAuthTypeRegex().Match(html);
        var serviceMatch = ServiceRegex().Match(html);

        if (!typeMatch.Success || !serviceMatch.Success)
        {
            throw new MfaProtocolException("re-auth 页面缺少 reAuthType 或 service");
        }

        var type = int.Parse(typeMatch.Groups[1].Value);
        var service = UnescapeJsonString(serviceMatch.Groups[1].Value);

        var saltMatch = SaltRegex().Match(html);
        var salt = saltMatch.Success ? UnescapeJsonString(saltMatch.Groups[1].Value) : null;

        var multifactorMatch = IsMultifactorRegex().Match(html);
        var isMultifactor = multifactorMatch.Success &&
                            !multifactorMatch.Groups[2].Success &&
                            string.Equals(multifactorMatch.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);

        var sleepMatch = IsSleepAccountRegex().Match(html);
        var isSleepAccount = sleepMatch.Success && sleepMatch.Groups[1].Value != "0";

        var anomalyMatch = IpAnomalyRegex().Match(html);
        var ipAnomaly = anomalyMatch.Success ? int.Parse(anomalyMatch.Groups[1].Value) : 0;

        var otpRequiredMatch = OtpCodeRequiredRegex().Match(html);
        var otpRequired = otpRequiredMatch.Success ? UnescapeJsonString(otpRequiredMatch.Groups[1].Value) : null;

        var bindOtpMatch = IsBindOtpRegex().Match(html);
        bool? isBindOtp = bindOtpMatch.Success
            ? string.Equals(bindOtpMatch.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase)
            : null;

        var otpUrlMatch = OtpUrlRegex().Match(html);
        var otpUrl = otpUrlMatch.Success ? UnescapeJsonString(otpUrlMatch.Groups[1].Value) : null;

        var userIdMatch = ReAuthUserIdRegex().Match(html);
        var userId = userIdMatch.Success ? UnescapeJsonString(userIdMatch.Groups[1].Value) : null;

        return new ReAuthPageParams(
            type, service, salt, isMultifactor, isSleepAccount, ipAnomaly,
            otpRequired, isBindOtp, otpUrl, userId);
    }

    /// <summary>
    /// reAuthParams 是 JSON 对象字面量，URL 中的 <c>/</c> 常被写成 <c>\/</c>。
    /// 正则只能拿到原始字符，必须按 JSON 字符串规则反转义；否则 <c>service</c> 会带着
    /// 反斜杠进入 reAuthSubmit.do / combinedLogin.do，上游对非法参数直接返回 200 错误页
    /// （表现为"combinedLogin.do 未返回微信扫码跳转"）。
    /// </summary>
    private static string UnescapeJsonString(string raw)
    {
        if (raw.IndexOf('\\') < 0) return raw;

        var builder = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var current = raw[i];
            if (current != '\\' || i + 1 >= raw.Length)
            {
                builder.Append(current);
                continue;
            }

            var escaped = raw[++i];
            switch (escaped)
            {
                case '"': builder.Append('"'); break;
                case '\\': builder.Append('\\'); break;
                case '/': builder.Append('/'); break;
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case 'u' when i + 4 < raw.Length &&
                               ushort.TryParse(raw.AsSpan(i + 1, 4), NumberStyles.HexNumber,
                                   CultureInfo.InvariantCulture, out var code):
                    builder.Append((char)code);
                    i += 4;
                    break;
                default:
                    builder.Append('\\').Append(escaped);
                    break;
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"reAuthType[""']?\s*:\s*[""']?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ReAuthTypeRegex();

    [GeneratedRegex(@"service[""']?\s*:\s*[""']([^""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceRegex();

    [GeneratedRegex(@"pwdEncryptSalt[""']?\s*:\s*[""']([^""']*)", RegexOptions.IgnoreCase)]
    private static partial Regex SaltRegex();

    [GeneratedRegex(@"isMultifactor[""']?\s*:\s*(?:[""']([^""']*)[""']|(null))", RegexOptions.IgnoreCase)]
    private static partial Regex IsMultifactorRegex();

    [GeneratedRegex(@"isSleepAccount[""']?\s*:\s*[""']?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex IsSleepAccountRegex();

    [GeneratedRegex(@"ipAnomaly[""']?\s*:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex IpAnomalyRegex();

    [GeneratedRegex(@"otpCodeRequired[""']?\s*:\s*[""']([^""']*)", RegexOptions.IgnoreCase)]
    private static partial Regex OtpCodeRequiredRegex();

    [GeneratedRegex(@"_isBindOtp[""']?\s*:\s*(true|false)", RegexOptions.IgnoreCase)]
    private static partial Regex IsBindOtpRegex();

    [GeneratedRegex(@"_otpUrl[""']?\s*:\s*[""']([^""']*)", RegexOptions.IgnoreCase)]
    private static partial Regex OtpUrlRegex();

    [GeneratedRegex(@"reAuthUserId[""']?\s*:\s*[""']([^""']*)", RegexOptions.IgnoreCase)]
    private static partial Regex ReAuthUserIdRegex();
}
