using System.Text.RegularExpressions;

namespace XAUAT.EduApi.Login.Xauat;

/// <summary>登录页上需要抓取的隐藏字段。</summary>
/// <param name="Lt">CAS 登录票据（<c>lt</c>）。</param>
/// <param name="Execution">CAS 执行上下文（<c>execution</c>）。</param>
/// <param name="EventId"><c>_eventId</c>，缺失时为 <c>submit</c>。</param>
/// <param name="EncryptSalt">
/// 密码加密盐。三态语义与 Flask 的 <c>salt_input.get('value', DEFAULT) if salt_input else None</c> 一致：
/// <list type="bullet">
/// <item><c>null</c> —— 页面上没有 <c>#pwdEncryptSalt</c> 输入框</item>
/// <item><c>""</c> —— 有输入框但 <c>value=""</c>（走明文）</item>
/// <item>其他 —— 正常盐值</item>
/// </list>
/// </param>
internal readonly record struct LoginFormFields(
    string Lt, string Execution, string EventId, string? EncryptSalt);

/// <summary>
/// 上游 HTML 的解析。
/// <para>
/// <b>为什么是正则而不是 HTML 解析库</b>：Native AOT 下 HtmlAgilityPack/AngleSharp 都不可用
/// （反射 + XPath 会触发 IL2026/IL3050，而 csproj 把它们设成了编译错误）。
/// 好在真正需要"解析"的只剩登录页那两处，且用 <see cref="GeneratedRegexAttribute"/> 源生成的正则
/// 完全够用且零反射。
/// </para>
/// <para>
/// 输入框的提取刻意**不用"一条正则匹配整个标签"**：那样会把属性顺序写死
/// （假定 <c>name</c> 在 <c>value</c> 之前）。这里改成先切出 <c>&lt;input ...&gt;</c> 标签、
/// 再用属性正则逐个解析，与 BeautifulSoup 的"按 name 找元素、再取属性"行为等价，
/// 对属性顺序、单双引号、无引号三种写法都成立。
/// </para>
/// </summary>
internal static partial class XauatHtmlParser
{
    [GeneratedRegex(@"<input\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InputTagRegex();

    /// <summary>匹配 <c>attr="v"</c> / <c>attr='v'</c> / <c>attr=v</c> 三种写法。</summary>
    [GeneratedRegex("""([A-Za-z_:][-A-Za-z0-9_:.]*)\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'>]+))""", RegexOptions.IgnoreCase)]
    private static partial Regex AttributeRegex();

    /// <summary>CAS 的 <c>&lt;span id="msg"&gt;</c> 错误提示。</summary>
    [GeneratedRegex("""<span[^>]*\bid\s*=\s*["']msg["'][^>]*>(.*?)</span>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MessageSpanRegex();

    /// <summary>
    /// 解析登录页的所有必要参数。等价于 Flask 的 <c>get_login_params</c>。
    /// </summary>
    public static LoginFormFields ParseLoginPage(string html)
    {
        var lt = FindInputAttribute(html, "name", "lt", "value") ?? "";
        var execution = FindInputAttribute(html, "name", "execution", "value") ?? "";

        // Flask: 缺失 -> 'submit'；存在但无 value 属性 -> 取默认 'submit'
        var eventId = FindInputAttribute(html, "name", "_eventId", "value") ?? "submit";

        // 这里必须区分"没有这个输入框"与"有但 value 为空"——两者的下游行为不同
        var saltTag = FindInputTag(html, "id", "pwdEncryptSalt");
        string? salt = saltTag is null
            ? null
            : GetAttributeValue(saltTag, "value") ?? XauatConstants.DefaultEncryptSalt;

        return new LoginFormFields(lt, execution, eventId, salt);
    }

    /// <summary>提取 <c>span#msg</c> 的文本；找不到返回 null。等价于 Flask 的 <c>soup.find('span', {'id': 'msg'}).text</c>。</summary>
    public static string? ParseMessageSpan(string html)
    {
        var match = MessageSpanRegex().Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// 判断一个页面是不是统一身份认证的登录页。
    /// <para>
    /// 通用统一认证就是靠这个从跳转链里"认出"登录页的，因此判定必须<b>宽松</b>：
    /// 命中 CAS 的 <c>execution</c> 隐藏域、UAAP 的 <c>#pwdEncryptSalt</c>、
    /// 或最朴素的 username + password 输入框，任一成立即算。
    /// </para>
    /// </summary>
    public static bool LooksLikeCasLoginPage(string html)
    {
        if (FindInputTag(html, "id", "pwdEncryptSalt") is not null) return true;
        if (FindInputTag(html, "name", "execution") is not null) return true;
        if (FindInputTag(html, "name", "lt") is not null) return true;

        return FindInputTag(html, "name", "username") is not null &&
               FindInputTag(html, "name", "password") is not null;
    }

    /// <summary>
    /// 页面上全部 <c>type="hidden"</c> 输入框的 name/value（按出现顺序，同名取第一个）。
    /// <para>
    /// 通用统一认证用它做"原样透传"：各校 CAS 皮肤之间真正会变的只有这些隐藏域，
    /// 透传之后换一套皮肤不用改代码。属性缺失的 <c>value</c> 记为空串。
    /// </para>
    /// </summary>
    public static List<(string Name, string Value)> ParseHiddenInputs(string html)
    {
        var inputs = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match tag in InputTagRegex().Matches(html))
        {
            var attributes = ParseAttributes(tag.Value);

            if (!attributes.TryGetValue("type", out var type) ||
                !string.Equals(type, "hidden", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!attributes.TryGetValue("name", out var name) || name.Length == 0) continue;
            if (!seen.Add(name)) continue;

            inputs.Add((name, GetAttributeValue(attributes, "value") ?? ""));
        }

        return inputs;
    }

    /// <summary>
    /// 找到第一个满足 <paramref name="matchAttribute"/> == <paramref name="matchValue"/> 的
    /// <c>&lt;input&gt;</c> 标签，返回它全部属性的字典（属性名小写）。
    /// </summary>
    private static Dictionary<string, string>? FindInputTag(string html, string matchAttribute, string matchValue)
    {
        foreach (Match tag in InputTagRegex().Matches(html))
        {
            var attributes = ParseAttributes(tag.Value);
            if (attributes.TryGetValue(matchAttribute, out var value) &&
                string.Equals(value, matchValue, StringComparison.Ordinal))
            {
                return attributes;
            }
        }

        return null;
    }

    private static string? FindInputAttribute(
        string html, string matchAttribute, string matchValue, string wantedAttribute)
    {
        var attributes = FindInputTag(html, matchAttribute, matchValue);
        return attributes is null ? null : GetAttributeValue(attributes, wantedAttribute);
    }

    /// <summary>属性字典里取指定属性；不存在返回 null（区别于"存在但为空串"）。</summary>
    private static string? GetAttributeValue(Dictionary<string, string> attributes, string name)
        => attributes.TryGetValue(name, out var value) ? value : null;

    private static Dictionary<string, string> ParseAttributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AttributeRegex().Matches(tag))
        {
            var name = match.Groups[1].Value;
            // 三个捕获组对应双引号 / 单引号 / 无引号，只有一个非空
            var value = match.Groups[2].Success ? match.Groups[2].Value
                : match.Groups[3].Success ? match.Groups[3].Value
                : match.Groups[4].Value;

            // 同名属性以第一个为准（浏览器行为）
            attributes.TryAdd(name, value);
        }

        return attributes;
    }
}
