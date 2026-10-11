using System.Text.RegularExpressions;

namespace XAUAT.EduApi.Login.Unified;

/// <summary>
/// 从"既不是 CAS 登录页、也不是终点"的页面里挖出继续前进的线索。
/// <para>
/// <b>为什么必须有这一层</b>：新版树维/金智教务前台（<c>/student/login</c>）只回一个 200 的
/// 前端空壳，真正的统一认证入口写在页面内的 JS 里——
/// <c>window.CONTEXT_PATH='/student'</c> + <c>ssoLinkList=[{'entiry':'/sso/login'}]</c>，
/// 由 <c>require.js</c> 在浏览器里拼出来再跳转。纯 HTML 客户端不可能执行 JS，
/// 只跟 302 的实现在这里必然断掉，表现为"该地址未跳转到统一身份认证登录页"。
/// </para>
/// <para>
/// 提取的线索按可靠性排序：<c>meta refresh</c>（服务端写明）→ JS <c>location</c> 赋值
/// （静态可读）→ 树维的 <c>ssoLinkList</c> 声明。调用方逐个尝试，全部失败才判定"不是 CAS 页面"。
/// </para>
/// </summary>
internal static partial class CasDiscoveryHints
{
    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTagRegex();

    [GeneratedRegex("""content\s*=\s*(?:"([^"]*)"|'([^']*)')""", RegexOptions.IgnoreCase)]
    private static partial Regex MetaContentRegex();

    /// <summary>从 <c>content="0;url=/x"</c> 里取 <c>/x</c>。</summary>
    [GeneratedRegex("""url\s*=\s*([^;"'\s>]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRefreshUrlRegex();

    /// <summary>
    /// <c>location.href = "…"</c> / <c>location = "…"</c> / <c>location.replace("…")</c>。
    /// 三条分支都必须要求右边是**引号字符串**，否则会把
    /// <c>top.location.href = self.location.href</c>（真实页面里的防嵌套 iframe 代码）当成跳转。
    /// </summary>
    [GeneratedRegex(
        """(?:window\.)?location(?:\.href)?\s*=\s*(?:"([^"]*)"|'([^']*)')|location\.replace\s*\(\s*(?:"([^"]*)"|'([^']*)')""",
        RegexOptions.IgnoreCase)]
    private static partial Regex JsLocationRegex();

    [GeneratedRegex("""window\.CONTEXT_PATH\s*=\s*(?:"([^"]*)"|'([^']*)')""", RegexOptions.IgnoreCase)]
    private static partial Regex ContextPathRegex();

    [GeneratedRegex("""ssoLinkList\s*=\s*\[(?<list>.*?)\]""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SsoLinkListRegex();

    /// <summary>键名写作 <c>entiry</c>（上游拼写如此，不是 <c>entry</c>），可能被引号包裹。</summary>
    [GeneratedRegex("""['"]?entiry['"]?\s*:\s*(?:"([^"]*)"|'([^']*)')""", RegexOptions.IgnoreCase)]
    private static partial Regex SsoEntryRegex();

    /// <summary>
    /// 页面里所有可继续跟随的目标（原样返回，可能是相对路径/绝对 URL）。
    /// 调用方负责按当前地址解析并做白名单校验。
    /// </summary>
    public static IReadOnlyList<string> FindCandidates(string html)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? candidate)
        {
            var value = candidate?.Trim().Trim('"', '\'').Trim();
            if (string.IsNullOrWhiteSpace(value)) return;
            if (seen.Add(value)) candidates.Add(value);
        }

        foreach (var target in MetaRefreshTargets(html)) Add(target);

        foreach (Match match in JsLocationRegex().Matches(html)) Add(FirstValue(match));

        foreach (var target in SsoEntryTargets(html)) Add(target);

        return candidates;
    }

    private static IEnumerable<string> MetaRefreshTargets(string html)
    {
        foreach (Match tag in MetaTagRegex().Matches(html))
        {
            if (!tag.Value.Contains("refresh", StringComparison.OrdinalIgnoreCase)) continue;

            var content = MetaContentRegex().Match(tag.Value);
            if (!content.Success) continue;

            var url = MetaRefreshUrlRegex().Match(FirstValue(content) ?? "");
            if (url.Success) yield return url.Groups[1].Value;
        }
    }

    /// <summary>
    /// 树维模板的 SSO 入口声明：<c>ssoLinkList = [{'entiry':'/sso/login'}]</c>，
    /// 相对路径要拼在 <c>window.CONTEXT_PATH</c> 之后（本站在 <c>/student</c> 下）。
    /// </summary>
    private static IEnumerable<string> SsoEntryTargets(string html)
    {
        var contextPathMatch = ContextPathRegex().Match(html);
        var contextPath = contextPathMatch.Success ? (FirstValue(contextPathMatch) ?? "").TrimEnd('/') : "";

        foreach (Match list in SsoLinkListRegex().Matches(html))
        {
            foreach (Match entry in SsoEntryRegex().Matches(list.Groups["list"].Value))
            {
                var path = (FirstValue(entry) ?? "").Trim();
                if (path.Length == 0) continue;

                if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    yield return path;
                    continue;
                }

                yield return contextPath + (path.StartsWith('/') ? path : "/" + path);
            }
        }
    }

    /// <summary>取第一个成功匹配的捕获组（每个正则都写了双引号/单引号两套分支）。</summary>
    private static string? FirstValue(Match match)
    {
        for (var i = 1; i < match.Groups.Count; i++)
        {
            if (match.Groups[i].Success) return match.Groups[i].Value;
        }

        return null;
    }
}
