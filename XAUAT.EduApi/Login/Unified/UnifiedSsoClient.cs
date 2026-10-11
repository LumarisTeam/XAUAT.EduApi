using System.Net;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Login.Xauat;

namespace XAUAT.EduApi.Login.Unified;

/// <summary>
/// 通用统一身份认证（CAS / 金智 UAAP）客户端：<b>只给一个受保护的目标地址，其余全靠跳转链发现</b>。
/// <para>
/// 完整流程：
/// </para>
/// <list type="number">
/// <item>GET 目标地址，手工跟随重定向（不自动跟随，见下），直到某一跳的响应体<b>长得像 CAS 登录页</b>；
/// 该跳的 URL（含上游给的 <c>service</c> 参数）就是登录地址，也是后面 CAS 跳回来的地址。</item>
/// <item>从登录页解析隐藏域（<c>lt</c>/<c>execution</c>/<c>_eventId</c>/<c>pwdEncryptSalt</c> 等），
/// 密码按 UAAP 规则用页面盐值 AES 加密后 POST。</item>
/// <item>POST <b>不跟随重定向</b>：CAS 的成功响应是 302 + <c>Set-Cookie: CASTGC=...</c>，
/// 票据只在这一跳响应里；跟随了就拿不到（与 <see cref="XauatSsoClient"/> 同样的坑）。</item>
/// <item>顺着 302 跳回目标站点，<b>逐跳</b>捕获 cookie——业务会话通常种在跳转链中间某一跳，
/// 而不是最后一跳。落地页不再是登录页即视为成功。</item>
/// </list>
/// <para>
/// 与 <see cref="XauatSsoClient"/> 的关系：那个类把 XAUAT 的地址、service 参数、加密方式全部写死，
/// 是"一个学校一条链路"；本类是它的通用版，复用了同一套 cookie 罐、HTML 解析与密码加密，
/// 但地址、表单字段、跳转跳数都来自运行时发现。两者互不影响——本类改动不会碰到既有登录链路。
/// </para>
/// </summary>
internal sealed class UnifiedSsoClient(
    IHttpClientFactory httpClientFactory,
    HostAllowList allowList,
    ILogger<UnifiedSsoClient> logger) : IUnifiedSsoClient
{
    /// <summary>
    /// 专用命名客户端：关自动重定向与自动 cookie（同 <c>XauatAuthClient</c>），
    /// 另加建连前的内网地址闸门 <see cref="UnifiedConnectGuard"/>。
    /// </summary>
    public const string ClientName = "UnifiedAuthClient";

    /// <summary>跳转跳数上限。CAS 正常链路 3～4 跳，10 跳足够且能挡住配置错误造成的死循环。</summary>
    public const int MaxRedirects = 10;

    /// <summary>登录页的判定：能不能解析出 UAAP 的登录表单。</summary>
    private static bool LooksLikeLoginPage(string html) => XauatHtmlParser.LooksLikeCasLoginPage(html);

    public async Task<UnifiedAuthOutcome> LoginAsync(UnifiedAuthCommand command, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(command.EntryUrl, UriKind.Absolute, out var entry))
        {
            throw new UnifiedAuthRequestException(
                ApiMessageKey.UnifiedAuthInvalidTarget, $"目标地址不是合法的绝对 URL：{command.EntryUrl}");
        }

        var client = httpClientFactory.CreateClient(ClientName);
        var jar = new XauatCookieJar();

        try
        {
            EnsureAllowed(entry);

            var loginPage = await DiscoverLoginPageAsync(client, jar, entry, cancellationToken);
            if (loginPage is null)
            {
                return UnifiedAuthOutcome.Fail(UnifiedAuthFailure.NotCasProtected,
                    "该地址未跳转到统一身份认证登录页，可能不需要认证或不是受 CAS 保护的页面");
            }

            var fields = XauatHtmlParser.ParseLoginPage(loginPage.Html);
            var form = BuildLoginForm(loginPage.Html, command, fields);

            logger.LogInformation("通用统一认证：已发现登录页 {LoginUrl}，准备提交凭据（学号 {Username}）",
                Redact(loginPage.Url), command.Username);

            return await SubmitCredentialsAsync(client, jar, loginPage, form, cancellationToken);
        }
        catch (UnifiedAuthRequestException)
        {
            // 调用方的问题（URL 非法、域名不在白名单、跳转过多），原样上抛 → 400
            throw;
        }
        catch (BlockedTargetException ex)
        {
            return UnifiedAuthOutcome.Fail(UnifiedAuthFailure.BlockedTarget, ex.Message);
        }
        catch (HttpRequestException ex) when (ex.InnerException is BlockedTargetException blocked)
        {
            return UnifiedAuthOutcome.Fail(UnifiedAuthFailure.BlockedTarget, blocked.Message);
        }
        catch (TaskCanceledException)
        {
            logger.LogWarning("通用统一认证：请求超时");
            return UnifiedAuthOutcome.Fail(UnifiedAuthFailure.Upstream, "请求超时，无法连接到目标站点或认证服务器");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "通用统一认证：网络连接错误");
            return UnifiedAuthOutcome.Fail(UnifiedAuthFailure.Upstream, "网络连接错误，无法连接到目标站点或认证服务器");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "通用统一认证：登录出错");
            return UnifiedAuthOutcome.Fail(UnifiedAuthFailure.Upstream, $"登录出错：{ex.Message}");
        }
    }

    // ================================================================ 阶段 1：发现登录页

    /// <summary>
    /// 从目标地址出发跟随重定向，返回第一个"看起来是 CAS 登录页"的响应。
    /// <para>
    /// <b>不只是跟 302</b>：新版树维/金智教务前台会在某一跳返回 200 的前端空壳
    /// （<c>/student/login</c>），把 SSO 入口写在页面 JS 里，由浏览器执行后才会跳转。
    /// 这类页面交给 <see cref="CasDiscoveryHints"/> 挖线索继续跟，否则整条链路会在这里断掉，
    /// 表现为"该地址未跳转到统一身份认证登录页"。
    /// </para>
    /// <para>
    /// 找不到就返回 <c>null</c>（说明目标地址不设防，或不是 CAS 保护的页面）——
    /// 这是调用方输入错，不是上游故障，所以不走异常路径。
    /// </para>
    /// </summary>
    private async Task<DiscoveredLoginPage?> DiscoverLoginPageAsync(
        HttpClient client, XauatCookieJar jar, Uri entry, CancellationToken cancellationToken)
    {
        var current = entry;

        // 既防 302 成环，也防页面线索成环（A 页指向 B、B 页指回 A）
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            EnsureAllowed(current);

            if (!visited.Add(current.ToString()))
            {
                logger.LogWarning("通用统一认证：页面线索指回了已访问过的 {Url}，停止发现", Redact(current));
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await XauatHttpSession.SendAsync(client, request, jar, cancellationToken);

            if (IsRedirect(response.StatusCode) && response.Headers.Location is not null)
            {
                current = Resolve(current, response.Headers.Location);
                continue;
            }

            var html = await XauatHttpUtils.ReadTextAsync(response, logger, cancellationToken);

            if (LooksLikeLoginPage(html))
            {
                return new DiscoveredLoginPage(current, html);
            }

            var next = FindNextHop(current, html, visited);
            if (next is null)
            {
                logger.LogInformation("通用统一认证：{Url} 未跳转到认证登录页（HTTP {Status}）",
                    Redact(current), (int)response.StatusCode);
                return null;
            }

            logger.LogInformation("通用统一认证：{From} 是前端门面页，按页内线索继续 -> {To}",
                Redact(current), Redact(next));
            current = next;
        }

        logger.LogWarning("通用统一认证：跟随跳转与页面线索超过 {Max} 跳仍未出现登录页", MaxRedirects);
        return null;
    }

    /// <summary>
    /// 从页面线索里挑下一个可访问的目标：能解析成 http(s)、在白名单内、且还没访问过。
    /// <para>
    /// 白名单外的线索**直接跳过**而不是抛异常：那些往往是页面里的无关外链，
    /// 不该把一次正常登录判成"目标不被允许"。
    /// </para>
    /// </summary>
    private Uri? FindNextHop(Uri current, string html, HashSet<string> visited)
    {
        foreach (var candidate in CasDiscoveryHints.FindCandidates(html))
        {
            if (!Uri.TryCreate(current, candidate, out var uri)) continue;
            if (uri.Scheme is not ("http" or "https")) continue;
            if (!allowList.IsAllowed(uri.Host)) continue;
            if (visited.Contains(uri.ToString())) continue;

            return uri;
        }

        return null;
    }

    // ================================================================ 阶段 2 / 3：提交凭据并跳回

    private async Task<UnifiedAuthOutcome> SubmitCredentialsAsync(
        HttpClient client, XauatCookieJar jar, DiscoveredLoginPage loginPage,
        Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, loginPage.Url)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Referrer = loginPage.Url;
        request.Headers.TryAddWithoutValidation("Origin", Origin(loginPage.Url));

        using var response = await XauatHttpSession.SendAsync(client, request, jar, cancellationToken);
        var status = (int)response.StatusCode;

        if (IsRedirect(response.StatusCode) && response.Headers.Location is not null)
        {
            // 认证成功后 CAS 会 302 回 service，并把 ticket 拼在 Location 上。
            // 票据（CASTGC）已经在上面的 SendAsync 里进了罐，这里只管顺路跳回去。
            var next = Resolve(loginPage.Url, response.Headers.Location);
            return await FollowBackAsync(client, jar, next, loginPage.Url, cancellationToken);
        }

        var html = await XauatHttpUtils.ReadTextAsync(response, logger, cancellationToken);

        if (status == 200 && LooksLoggedIn(html))
        {
            // 少数皮肤直接在登录请求上返回已登录页面（不 302），与 XauatSsoClient 的处理一致
            return Success(jar, loginPage.Url, loginPage.Url);
        }

        return InvalidCredentials(XauatHtmlParser.ParseMessageSpan(html));
    }

    /// <summary>
    /// 跟完 CAS 跳回目标站点的每一跳，逐跳收 cookie。
    /// </summary>
    private async Task<UnifiedAuthOutcome> FollowBackAsync(
        HttpClient client, XauatCookieJar jar, Uri start, Uri loginUrl, CancellationToken cancellationToken)
    {
        var current = start;

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            EnsureAllowed(current);

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await XauatHttpSession.SendAsync(client, request, jar, cancellationToken);
            var status = (int)response.StatusCode;

            if (IsRedirect(response.StatusCode) && response.Headers.Location is not null)
            {
                current = Resolve(current, response.Headers.Location);
                continue;
            }

            var html = await XauatHttpUtils.ReadTextAsync(response, logger, cancellationToken);

            // 又被弹回登录页 = ticket 没被认（多为密码错后的错误页转发），按凭据错误处理
            if (LooksLikeLoginPage(html))
            {
                return InvalidCredentials(XauatHtmlParser.ParseMessageSpan(html));
            }

            if (status is 401 or 403)
            {
                return InvalidCredentials(XauatHtmlParser.ParseMessageSpan(html));
            }

            if (status != 200)
            {
                return UnifiedAuthOutcome.Fail(UnifiedAuthFailure.Upstream,
                    $"登录后回跳失败：目标站点返回 HTTP {status}");
            }

            logger.LogInformation("通用统一认证：登录成功，落地 {LandingUrl}", Redact(current));
            return Success(jar, loginUrl, current);
        }

        return UnifiedAuthOutcome.Fail(UnifiedAuthFailure.Upstream,
            $"登录后跳转超过 {MaxRedirects} 跳仍未落地，已中止");
    }

    // ================================================================ 表单与结果组装

    /// <summary>
    /// 组装登录表单。
    /// <para>
    /// 页面上所有 <c>type="hidden"</c> 的输入原样透传（<c>lt</c>/<c>execution</c>/<c>type</c> 等），
    /// 再按金智 UAAP 模板补上固定字段。透传这一步是关键：不同学校/不同 CAS 版本真正会变的
    /// 就是这些隐藏域，透传后换一套皮肤不用改代码。
    /// </para>
    /// </summary>
    private Dictionary<string, string> BuildLoginForm(
        string html, UnifiedAuthCommand command, LoginFormFields fields)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, value) in XauatHtmlParser.ParseHiddenInputs(html))
        {
            form.TryAdd(name, value);
        }

        form["username"] = command.Username;
        // 有盐值走 UAAP 的 AES，没有盐值则按上游既有行为发明文（PasswordEncryptor 内部会打警告）
        form["password"] = PasswordEncryptor.Encrypt(command.Password, fields.EncryptSalt, logger);

        if (fields.Lt.Length > 0) form["lt"] = fields.Lt;
        if (fields.Execution.Length > 0) form["execution"] = fields.Execution;
        form["_eventId"] = fields.EventId;

        form["captcha"] = "";
        form["cllt"] = "userNameLogin";
        form["dllt"] = "generalLogin";
        form["rememberMe"] = "true";

        return form;
    }

    private UnifiedAuthOutcome InvalidCredentials(string? upstreamMessage)
    {
        var message = string.IsNullOrWhiteSpace(upstreamMessage)
            ? "登录失败：用户名或密码错误"
            : $"登录失败：{upstreamMessage}";
        logger.LogInformation("通用统一认证：{Message}", message);
        return UnifiedAuthOutcome.Fail(UnifiedAuthFailure.InvalidCredentials, message);
    }

    private static UnifiedAuthOutcome Success(XauatCookieJar jar, Uri authServerUrl, Uri landingUrl)
        => new(
            true,
            "登录成功",
            UnifiedAuthFailure.None,
            authServerUrl.ToString(),
            landingUrl.ToString(),
            // 只回落地站点的 cookie：认证服务器的 CASTGC 发给业务系统没有意义，
            // 混在同一个 Cookie 头里还可能被上游判为异常。全量明细见 Cookies。
            jar.CookieHeaderFor(landingUrl.Host),
            jar.Snapshot());

    // ================================================================ 小工具

    /// <summary>白名单 + 协议校验。<c>LoginAsync</c> 与每一次跳转前都会走一遍。</summary>
    private void EnsureAllowed(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https"))
        {
            throw new UnifiedAuthRequestException(
                ApiMessageKey.UnifiedAuthInvalidTarget, $"仅支持 http/https 地址，实际为 {uri.Scheme}");
        }

        if (!allowList.IsAllowed(uri.Host))
        {
            throw new UnifiedAuthRequestException(
                ApiMessageKey.UnifiedAuthHostNotAllowed,
                $"目标域名 {uri.Host} 不在允许的白名单内（默认仅 *.edu.cn，可用 UNIFIED_AUTH_ALLOWED_HOSTS 追加）");
        }
    }

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
            or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static Uri Resolve(Uri current, Uri location)
        => location.IsAbsoluteUri ? location : new Uri(current, location);

    private static string Origin(Uri uri) => $"{uri.Scheme}://{uri.Authority}";

    /// <summary>日志用的脱敏 URL：丢掉 query。<c>ticket=ST-...</c> 那类一次性凭据不允许落日志。</summary>
    private static string Redact(Uri uri) => $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";

    /// <summary>页面里出现"退出/logout"即认为已是登录态——与 <see cref="XauatSsoClient"/> 同一启发式。</summary>
    private static bool LooksLoggedIn(string html)
        => html.Contains("退出", StringComparison.Ordinal) ||
           html.Contains("logout", StringComparison.OrdinalIgnoreCase);

    private sealed record DiscoveredLoginPage(Uri Url, string Html);
}
