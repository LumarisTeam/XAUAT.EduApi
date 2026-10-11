using System.Net;
using System.Text;

namespace XAUAT.EduApi.Tests.Login.TestSupport;

/// <summary>一次实际发出的请求，供断言透传的 Cookie 头与表单体。</summary>
internal sealed record SentRequest(string Method, string Url, string? Body, string? CookieHeader);

/// <summary>
/// 按脚本应答的 <see cref="HttpMessageHandler"/>。
/// <para>
/// 通用统一认证客户端要跑"目标地址 → CAS 登录页 → POST → 跳回"这条多点链路，
/// 每一点的响应形态（302 / 200 登录页 / 200 落地页）都不同，用 Moq 拼 httphandler 太啰嗦，
/// 这里用"匹配器 + 响应工厂"直接描述整条链路。
/// </para>
/// <para>
/// 每个响应都必须新建：<see cref="HttpResponseMessage"/> 只能被消费一次。
/// 因此规则里存的是工厂而不是现成对象。
/// </para>
/// </summary>
internal sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly List<Rule> _rules = [];

    /// <summary>按发生顺序记录的全部请求。</summary>
    public List<SentRequest> Sent { get; } = [];

    public ScriptedHttpHandler On(
        Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _rules.Add(new Rule(match, respond));
        return this;
    }

    /// <summary>GET 精确匹配某个 URL。</summary>
    public ScriptedHttpHandler OnGet(string url, Func<HttpResponseMessage> respond)
        => On(request => request.Method == HttpMethod.Get && request.RequestUri!.ToString() == url,
            _ => respond());

    /// <summary>GET 匹配某个 URL 前缀（CAS 会把 service/ticket 拼在 query 上，前缀匹配更省事）。</summary>
    public ScriptedHttpHandler OnGetStartingWith(string urlPrefix, Func<HttpResponseMessage> respond)
        => On(request => request.Method == HttpMethod.Get &&
                         request.RequestUri!.ToString().StartsWith(urlPrefix, StringComparison.Ordinal),
            _ => respond());

    /// <summary>POST 匹配某个 URL 前缀。</summary>
    public ScriptedHttpHandler OnPostStartingWith(string urlPrefix, Func<HttpResponseMessage> respond)
        => On(request => request.Method == HttpMethod.Post &&
                         request.RequestUri!.ToString().StartsWith(urlPrefix, StringComparison.Ordinal),
            _ => respond());

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        var cookieHeader = request.Headers.TryGetValues("Cookie", out var values)
            ? string.Join("; ", values)
            : null;

        Sent.Add(new SentRequest(request.Method.Method, request.RequestUri!.ToString(), body, cookieHeader));

        foreach (var rule in _rules)
        {
            if (rule.Match(request)) return rule.Respond(request);
        }

        return FakeResponses.Html("<html><body>no scripted rule</body></html>", HttpStatusCode.NotFound);
    }

    private sealed record Rule(
        Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond);
}

/// <summary>响应构造的小工具。每个方法都返回**新实例**。</summary>
internal static class FakeResponses
{
    public static HttpResponseMessage Html(
        string html, HttpStatusCode status = HttpStatusCode.OK, params string[] setCookies)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        };
        AddCookies(response, setCookies);
        return response;
    }

    public static HttpResponseMessage Redirect(string location, params string[] setCookies)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) }
        };
        AddCookies(response, setCookies);
        return response;
    }

    private static void AddCookies(HttpResponseMessage response, string[] setCookies)
    {
        foreach (var cookie in setCookies)
        {
            response.Headers.TryAddWithoutValidation("Set-Cookie", cookie);
        }
    }
}

/// <summary>固定返回同一个 handler 的 <see cref="IHttpClientFactory"/> 替身（客户端只按名字取用）。</summary>
internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
