using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Login.Xauat;

namespace XAUAT.EduApi.Login.Mfa;

internal enum ReAuthStartFailure
{
    None,
    MethodUnsupported,
    SendFailed,
    RateLimited,
    Protocol,
    PasswordExpired,
    Upstream
}

internal sealed record ReAuthStartResult(
    bool Success,
    ReAuthStartFailure Failure,
    string Message,
    MfaSeed? Seed = null,
    int? RetryAfterSeconds = null)
{
    public static ReAuthStartResult Ok(MfaSeed seed) => new(true, ReAuthStartFailure.None, "ok", seed);
    public static ReAuthStartResult Unsupported(string message)
        => new(false, ReAuthStartFailure.MethodUnsupported, message);
    public static ReAuthStartResult Fail(ReAuthStartFailure failure, string message, int? retryAfter = null)
        => new(false, failure, message, null, retryAfter);
}

internal enum ReAuthFailure
{
    None,
    InvalidCode,
    Unauthorized,
    PasswordExpired,
    Protocol,
    Upstream
}

internal sealed record ReAuthCompletion(
    bool Success,
    ReAuthFailure Failure,
    string Message,
    IReadOnlyList<(string Host, string Name, string Value)> Cookies,
    Uri? LandingUrl = null,
    Uri? AuthServerUrl = null)
{
    public static ReAuthCompletion Fail(ReAuthFailure failure, string message)
        => new(false, failure, message, Array.Empty<(string Host, string Name, string Value)>());
}

internal enum QrPollStatus
{
    Pending,
    Scanned,
    Success,
    Expired,
    Failed
}

internal sealed record QrPollResult(
    QrPollStatus Status,
    string Message,
    ReAuthCompletion? Completion = null,
    int? RetryAfterSeconds = null);

/// <summary>
/// re-auth 协议层：识别/解析页面、切换方式、发送动态码、提交验证码、微信扫码启动与轮询、跟票。
/// <para>所有 HTTP 请求都通过调用方传入的 <see cref="XauatCookieJar"/> 与命名客户端，
/// 复用 <see cref="XauatHttpSession.SendAsync"/> 的逐跳 cookie 语义。</para>
/// </summary>
internal sealed partial class ReAuthHandler(
    IHttpClientFactory httpClientFactory,
    ILogger<ReAuthHandler> logger)
{
    private const string ReAuthSubmitPath = "/authserver/reAuthCheck/reAuthSubmit.do";
    private const string ChangeTypePath = "/authserver/reAuthCheck/changeReAuthType.do";
    private const string SendCodePath = "/authserver/dynamicCode/getDynamicCodeByReauth.do";
    private const string CombinedLoginPath = "/authserver/combinedLogin.do";
    private const string CallbackPath = "/authserver/callback";

    private const string WeChatQrHost = "open.weixin.qq.com";
    private const string WeChatPollHost = "lp.open.weixin.qq.com";

    /// <summary>开始二次认证：选择方式、发送动态码或启动微信扫码。</summary>
    public async Task<ReAuthStartResult> StartAsync(
        MfaFlowKind kind,
        string username,
        Uri authServerLoginUrl,
        XauatCookieJar jar,
        ReAuthPageParams page,
        MfaMethod requested,
        bool trustDevice,
        string? entryUrl,
        string? sessionCacheKey,
        string? ticketCacheKey,
        CancellationToken cancellationToken)
    {
        var pageMethod = MfaMethods.FromReAuthType(page.ReAuthType);
        var target = requested == MfaMethod.Auto ? pageMethod : requested;

        if (target == MfaMethod.Auto)
        {
            return ReAuthStartResult.Unsupported("authserver 返回了未知的二次认证方式");
        }

        string? maskedTarget = null;
        if (target != pageMethod)
        {
            if (target == MfaMethod.Password)
            {
                return ReAuthStartResult.Unsupported("当前页面不支持切换为密码认证");
            }

            var change = await ChangeTypeAsync(authServerLoginUrl, jar, page, target, cancellationToken);
            if (!change.Success)
            {
                return change.Unsupported
                    ? ReAuthStartResult.Unsupported(change.Message)
                    : ReAuthStartResult.Fail(ReAuthStartFailure.Upstream, change.Message);
            }

            maskedTarget = change.MaskedTarget;
        }

        var seed = new MfaSeed
        {
            Kind = kind,
            Username = username,
            AuthServerLoginUrl = authServerLoginUrl.ToString(),
            Service = page.Service,
            ReAuthType = (int)target,
            Method = MfaMethods.ToApiName(target),
            EncryptSalt = page.EncryptSalt,
            IsMultifactor = page.IsMultifactor,
            IsSleepAccount = page.IsSleepAccount,
            MaskedTarget = maskedTarget,
            Cookies = jar.Snapshot().Select(ToMfaCookie).ToList(),
            EntryUrl = entryUrl,
            SessionCacheKey = sessionCacheKey,
            TicketCacheKey = ticketCacheKey
        };

        switch (target)
        {
            case MfaMethod.Password:
                return ReAuthStartResult.Ok(seed);

            case MfaMethod.Otp:
                return ReAuthStartResult.Ok(new MfaSeed
                {
                    Kind = seed.Kind,
                    Username = seed.Username,
                    AuthServerLoginUrl = seed.AuthServerLoginUrl,
                    Service = seed.Service,
                    ReAuthType = seed.ReAuthType,
                    Method = seed.Method,
                    EncryptSalt = seed.EncryptSalt,
                    IsMultifactor = seed.IsMultifactor,
                    IsSleepAccount = seed.IsSleepAccount,
                    MaskedTarget = "安全令牌",
                    Hint = page.OtpCodeRequired ?? "请输入6位有效的安全令牌",
                    Cookies = seed.Cookies,
                    EntryUrl = seed.EntryUrl,
                    SessionCacheKey = seed.SessionCacheKey,
                    TicketCacheKey = seed.TicketCacheKey
                });

            case MfaMethod.Sms:
            case MfaMethod.WeCom:
            case MfaMethod.TodayCampus:
            case MfaMethod.Email:
            case MfaMethod.DingTalk:
            case MfaMethod.WeLink:
                return await SendDynamicCodeAsync(authServerLoginUrl, jar, page, target, seed, cancellationToken);

            case MfaMethod.WeChatQr:
            case MfaMethod.QqQr:
            case MfaMethod.DingTalkQr:
                return await StartQrAsync(authServerLoginUrl, jar, page, target, seed, cancellationToken);

            default:
                return ReAuthStartResult.Unsupported($"暂不支持二次认证方式 {MfaMethods.ToApiName(target)}");
        }
    }

    /// <summary>用密码/OTP/动态码提交 reAuthSubmit.do；成功后跟随 login?service=... 拿票。</summary>
    public async Task<ReAuthCompletion> CompleteAsync(
        Uri authServerLoginUrl,
        string service,
        int reAuthType,
        bool isMultifactor,
        bool trustDevice,
        XauatCookieJar jar,
        string? code,
        string? password,
        string? encryptSalt,
        HttpClient client,
        Func<Uri, bool>? allowHop,
        CancellationToken cancellationToken)
    {
        var method = MfaMethods.FromReAuthType(reAuthType);
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["service"] = service,
            ["reAuthType"] = reAuthType.ToString(),
            ["isMultifactor"] = isMultifactor ? "true" : "",
            ["password"] = "",
            ["dynamicCode"] = "",
            ["otpCode"] = "",
            ["uuid"] = "",
            ["answer1"] = "",
            ["answer2"] = "",
            ["skipTmpReAuth"] = trustDevice ? "true" : "false"
        };

        switch (method)
        {
            case MfaMethod.Password:
                form["password"] = PasswordEncryptor.Encrypt(password ?? "", encryptSalt, logger);
                break;

            case MfaMethod.Otp:
                if (string.IsNullOrWhiteSpace(code) || !Regex.IsMatch(code.Trim(), @"^\d{6}$"))
                {
                    return ReAuthCompletion.Fail(ReAuthFailure.InvalidCode, "请输入6位有效的安全令牌");
                }
                form["otpCode"] = code.Trim();
                break;

            case MfaMethod.Sms:
            case MfaMethod.WeCom:
            case MfaMethod.TodayCampus:
            case MfaMethod.Email:
            case MfaMethod.DingTalk:
            case MfaMethod.WeLink:
                if (string.IsNullOrWhiteSpace(code))
                {
                    return ReAuthCompletion.Fail(ReAuthFailure.InvalidCode, "请输入验证码");
                }
                form["dynamicCode"] = code.Trim();
                break;

            default:
                return ReAuthCompletion.Fail(ReAuthFailure.Protocol, "该认证方式不支持 reAuthSubmit.do");
        }

        var url = AuthUrl(authServerLoginUrl, ReAuthSubmitPath);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form)
        };
        AddXhrHeaders(request, authServerLoginUrl, url);

        using var response = await XauatHttpSession.SendAsync(client, request, jar, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if ((int)response.StatusCode >= 500)
        {
            return ReAuthCompletion.Fail(ReAuthFailure.Upstream, $"认证服务器返回 HTTP {(int)response.StatusCode}");
        }

        var submit = ParseSubmitResponse(body);
        if (submit is null)
        {
            return ReAuthCompletion.Fail(ReAuthFailure.Protocol, "无法解析 reAuthSubmit.do 响应");
        }

        if (string.Equals(submit.Value.Code, "reAuth_success", StringComparison.OrdinalIgnoreCase))
        {
            return await FollowLoginAsync(authServerLoginUrl, jar, client, allowHop, cancellationToken);
        }

        if (string.Equals(submit.Value.Code, "reAuth_unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return ReAuthCompletion.Fail(ReAuthFailure.Unauthorized, submit.Value.Message);
        }

        return ReAuthCompletion.Fail(ReAuthFailure.InvalidCode, submit.Value.Message);
    }

    /// <summary>轮询微信扫码状态；405 时用 callback + login 完成登录。</summary>
    public async Task<QrPollResult> PollQrAsync(
        Uri authServerLoginUrl,
        string qrUuid,
        string qrState,
        XauatCookieJar jar,
        HttpClient client,
        Func<Uri, bool>? allowHop,
        CancellationToken cancellationToken)
    {
        var qrClient = httpClientFactory.CreateClient(MfaQrClient.Name);
        using var pollRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://{WeChatPollHost}/connect/l/qrconnect?uuid={Uri.EscapeDataString(qrUuid)}&_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        pollRequest.Headers.TryAddWithoutValidation("Accept", "*/*");

        using var pollResponse = await qrClient.SendAsync(pollRequest, cancellationToken);
        var body = await pollResponse.Content.ReadAsStringAsync(cancellationToken);

        var errMatch = WxErrCodeRegex().Match(body);
        var codeMatch = WxCodeRegex().Match(body);
        var errCode = errMatch.Success ? errMatch.Groups[1].Value : "";

        switch (errCode)
        {
            case "408":
                return new QrPollResult(QrPollStatus.Pending, "等待扫码", null, 1);
            case "404":
                return new QrPollResult(QrPollStatus.Scanned, "已扫码，请在手机上确认", null, 1);
            case "403":
                return new QrPollResult(QrPollStatus.Expired, "二维码已过期，请重新登录获取");
            case "405":
            {
                if (!codeMatch.Success || string.IsNullOrWhiteSpace(codeMatch.Groups[1].Value))
                {
                    return new QrPollResult(QrPollStatus.Failed, "微信回调缺少 code");
                }

                var wxCode = codeMatch.Groups[1].Value.Trim();
                var callback = AuthUrl(authServerLoginUrl, CallbackPath)
                               + $"?code={Uri.EscapeDataString(wxCode)}&state={Uri.EscapeDataString(qrState)}";

                using var callbackRequest = new HttpRequestMessage(HttpMethod.Get, callback);
                callbackRequest.Headers.Referrer = authServerLoginUrl;

                using var callbackResponse = await XauatHttpSession.SendAsync(
                    client, callbackRequest, jar, cancellationToken);

                if (!IsRedirect(callbackResponse.StatusCode) || callbackResponse.Headers.Location is null)
                {
                    return new QrPollResult(QrPollStatus.Failed, "微信回调未返回重定向");
                }

                var next = XauatConstants.SecureAuthServer(
                    Resolve(callback, callbackResponse.Headers.Location));
                if (IsReAuthOrImproveInfo(next))
                {
                    return new QrPollResult(QrPollStatus.Failed, "微信回调后仍需二次认证或改密");
                }

                var completion = await FollowFromAsync(next, authServerLoginUrl, jar, client, allowHop, cancellationToken);
                return completion.Success
                    ? new QrPollResult(QrPollStatus.Success, "登录成功", completion)
                    : new QrPollResult(QrPollStatus.Failed, completion.Message);
            }

            default:
                return new QrPollResult(QrPollStatus.Failed, $"微信轮询返回未知状态 {(string.IsNullOrEmpty(errCode) ? "?" : errCode)}");
        }
    }

    // ---------------------------------------------------------------- 发送/切换

    private async Task<ReAuthStartResult> SendDynamicCodeAsync(
        Uri authServerLoginUrl,
        XauatCookieJar jar,
        ReAuthPageParams page,
        MfaMethod method,
        MfaSeed seed,
        CancellationToken cancellationToken)
    {
        var typeName = MfaMethods.GetAuthCodeTypeName(method);
        if (typeName is null)
        {
            return ReAuthStartResult.Unsupported("缺少动态码发送类型");
        }

        var url = AuthUrl(authServerLoginUrl, SendCodePath);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["userName"] = page.ReAuthUserId ?? seed.Username,
                ["authCodeTypeName"] = typeName
            })
        };
        AddXhrHeaders(request, authServerLoginUrl, url);

        using var response = await XauatHttpSession.SendAsync(
            httpClientFactory.CreateClient(XauatSsoClient.ClientName),
            request, jar, cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var parsed = ParseSendResponse(body);
        if (parsed is null)
        {
            return ReAuthStartResult.Fail(ReAuthStartFailure.Upstream, "无法解析验证码发送响应");
        }

        if (parsed.Value.RateLimited)
        {
            return ReAuthStartResult.Fail(
                ReAuthStartFailure.RateLimited, parsed.Value.Message, parsed.Value.CodeTime);
        }

        if (!parsed.Value.Success)
        {
            return ReAuthStartResult.Fail(ReAuthStartFailure.SendFailed, parsed.Value.Message);
        }

        return ReAuthStartResult.Ok(new MfaSeed
        {
            Kind = seed.Kind,
            Username = seed.Username,
            AuthServerLoginUrl = seed.AuthServerLoginUrl,
            Service = seed.Service,
            ReAuthType = seed.ReAuthType,
            Method = seed.Method,
            EncryptSalt = seed.EncryptSalt,
            IsMultifactor = seed.IsMultifactor,
            IsSleepAccount = seed.IsSleepAccount,
            MaskedTarget = parsed.Value.MaskedTarget ?? seed.MaskedTarget,
            Hint = parsed.Value.Message,
            Cookies = seed.Cookies,
            EntryUrl = seed.EntryUrl,
            SessionCacheKey = seed.SessionCacheKey,
            TicketCacheKey = seed.TicketCacheKey
        });
    }

    private async Task<(bool Success, bool Unsupported, string Message, string? MaskedTarget)> ChangeTypeAsync(
        Uri authServerLoginUrl,
        XauatCookieJar jar,
        ReAuthPageParams page,
        MfaMethod target,
        CancellationToken cancellationToken)
    {
        var url = AuthUrl(authServerLoginUrl, ChangeTypePath);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["service"] = page.Service,
                ["reAuthType"] = ((int)target).ToString(),
                ["isMultifactor"] = page.IsMultifactor ? "true" : ""
            })
        };
        AddXhrHeaders(request, authServerLoginUrl, url);

        using var response = await XauatHttpSession.SendAsync(
            httpClientFactory.CreateClient(XauatSsoClient.ClientName),
            request, jar, cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var codeElement) ? codeElement.ToString() : "";
            if (code == "1")
            {
                var data = root.TryGetProperty("data", out var dataElement) ? dataElement : default;
                var actual = data.ValueKind == JsonValueKind.Object &&
                             data.TryGetProperty("reAuthType", out var typeElement)
                    ? typeElement.ToString()
                    : "";
                var masked = data.ValueKind == JsonValueKind.Object &&
                             data.TryGetProperty("reAuthUserNameInput", out var userElement)
                    ? userElement.ToString()
                    : null;

                if (actual == ((int)target).ToString())
                {
                    return (true, false, "ok", masked);
                }
            }

            return (false, true, "当前应用不支持所选的二次认证方式", null);
        }
        catch (JsonException)
        {
            return (false, false, "无法解析 changeReAuthType.do 响应", null);
        }
    }

    // ---------------------------------------------------------------- 微信扫码

    private async Task<ReAuthStartResult> StartQrAsync(
        Uri authServerLoginUrl,
        XauatCookieJar jar,
        ReAuthPageParams page,
        MfaMethod method,
        MfaSeed seed,
        CancellationToken cancellationToken)
    {
        var provider = QrProviderRegistry.For(method);
        if (provider is null || !provider.Enabled || provider.QrHost is null)
        {
            return ReAuthStartResult.Unsupported(
                $"{MfaMethods.ToApiName(method)} 扫码方式尚未实测，暂不可用");
        }

        var url = AuthUrl(authServerLoginUrl, CombinedLoginPath)
                  + $"?type={Uri.EscapeDataString(provider.CombinedLoginType)}"
                  + $"&reAuth={(page.IsMultifactor ? "2" : "1")}"
                  + $"&success={Uri.EscapeDataString(page.Service)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = authServerLoginUrl;
        using var response = await XauatHttpSession.SendAsync(
            httpClientFactory.CreateClient(XauatSsoClient.ClientName),
            request, jar, cancellationToken);

        if (!IsRedirect(response.StatusCode) || response.Headers.Location is null)
        {
            logger.LogWarning(
                "combinedLogin.do 未返回微信扫码跳转：HTTP {Status}，Location={Location}",
                (int)response.StatusCode, response.Headers.Location);
            return ReAuthStartResult.Fail(ReAuthStartFailure.Protocol, "combinedLogin.do 未返回微信扫码跳转");
        }

        var qrConnect = Resolve(url, response.Headers.Location);
        if (!string.Equals(qrConnect.Scheme, "https", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(qrConnect.Host, WeChatQrHost, StringComparison.OrdinalIgnoreCase) ||
            !qrConnect.AbsolutePath.Equals("/connect/qrconnect", StringComparison.OrdinalIgnoreCase))
        {
            return ReAuthStartResult.Fail(ReAuthStartFailure.Protocol, "微信扫码跳转地址不在允许的 host/path 内");
        }

        var query = ParseQuery(qrConnect.Query);
        if (!query.TryGetValue("state", out var qrState) || string.IsNullOrWhiteSpace(qrState))
        {
            return ReAuthStartResult.Fail(ReAuthStartFailure.Protocol, "微信扫码跳转缺少 state");
        }

        var qrClient = httpClientFactory.CreateClient(MfaQrClient.Name);
        using var qrPageRequest = new HttpRequestMessage(HttpMethod.Get, qrConnect);
        qrPageRequest.Headers.TryAddWithoutValidation(
            "Accept", "text/html,application/xhtml+xml,*/*;q=0.8");
        using var qrPageResponse = await qrClient.SendAsync(qrPageRequest, cancellationToken);
        var qrPage = await qrPageResponse.Content.ReadAsStringAsync(cancellationToken);

        var uuidMatch = QrUuidRegex().Match(qrPage);
        var qrUuid = uuidMatch.Success
            ? new[] { uuidMatch.Groups[1].Value, uuidMatch.Groups[2].Value, uuidMatch.Groups[3].Value }
                .FirstOrDefault(value => !string.IsNullOrEmpty(value))
            : null;
        if (qrUuid is null || !QrUuidPattern().IsMatch(qrUuid))
        {
            return ReAuthStartResult.Fail(ReAuthStartFailure.Protocol, "无法从微信扫码页解析二维码 UUID");
        }
        var qrImageUrl = $"https://{WeChatQrHost}/connect/qrcode/{Uri.EscapeDataString(qrUuid)}";
        using var imageRequest = new HttpRequestMessage(HttpMethod.Get, qrImageUrl);
        imageRequest.Headers.TryAddWithoutValidation("Accept", "image/*");
        using var imageResponse = await qrClient.SendAsync(imageRequest, cancellationToken);
        var imageBytes = await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (imageBytes.Length == 0)
        {
            return ReAuthStartResult.Fail(ReAuthStartFailure.Protocol, "二维码图片为空");
        }

        return ReAuthStartResult.Ok(new MfaSeed
        {
            Kind = seed.Kind,
            Username = seed.Username,
            AuthServerLoginUrl = seed.AuthServerLoginUrl,
            Service = seed.Service,
            ReAuthType = seed.ReAuthType,
            Method = MfaMethods.ToApiName(method),
            EncryptSalt = seed.EncryptSalt,
            IsMultifactor = seed.IsMultifactor,
            IsSleepAccount = seed.IsSleepAccount,
            MaskedTarget = null,
            Hint = "请使用微信扫码并在手机上确认",
            Cookies = seed.Cookies,
            EntryUrl = seed.EntryUrl,
            SessionCacheKey = seed.SessionCacheKey,
            TicketCacheKey = seed.TicketCacheKey,
            QrUuid = qrUuid,
            QrState = qrState,
            QrProvider = provider.Name,
            QrCodeUrl = qrImageUrl,
            QrImageBase64 = Convert.ToBase64String(imageBytes)
        });
    }

    // ---------------------------------------------------------------- 跟票

    private async Task<ReAuthCompletion> FollowLoginAsync(
        Uri authServerLoginUrl,
        XauatCookieJar jar,
        HttpClient client,
        Func<Uri, bool>? allowHop,
        CancellationToken cancellationToken)
    {
        var current = authServerLoginUrl;

        for (var hop = 0; hop < 6; hop++)
        {
            // callback / login?service= 可能回跳到 authserver 的 HTTP 地址；生产 80 端口不可达，
            // 统一升级为 HTTPS（对其它 host 无影响）。
            current = XauatConstants.SecureAuthServer(current);

            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                response = await XauatHttpSession.SendAsync(client, request, jar, cancellationToken);
            }
            catch (Exception ex) when (
                (ex is HttpRequestException ||
                 (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)) &&
                HasTicket(current))
            {
                // 已经拿到 ticket，只是落地页（如财务平台 :8080）从生产不可达。
                // 不要把这种情况变成 500：返回带 ticket 的地址，让调用方自行完成落地。
                logger.LogWarning(ex,
                    "二次认证跟票时落地页不可达，返回带 ticket 的地址：{Host}{Path}",
                    current.Host, current.AbsolutePath);
                return new ReAuthCompletion(
                    true,
                    ReAuthFailure.None,
                    "登录成功（落地页不可达，已返回 ticket）",
                    jar.Snapshot(),
                    current,
                    authServerLoginUrl);
            }

            using (response)
            {
                if (IsRedirect(response.StatusCode) && response.Headers.Location is not null)
                {
                    var next = Resolve(current, response.Headers.Location);

                    if (IsReAuthOrImproveInfo(next))
                    {
                        return ReAuthCompletion.Fail(
                            next.AbsolutePath.Contains("/improveInfo/", StringComparison.OrdinalIgnoreCase)
                                ? ReAuthFailure.PasswordExpired
                                : ReAuthFailure.Protocol,
                            "二次认证后仍被要求再次认证");
                    }

                    if (allowHop is not null && !allowHop(next))
                    {
                        return ReAuthCompletion.Fail(ReAuthFailure.Protocol, "二次认证后的跳转目标不在允许范围");
                    }

                    if (next.AbsolutePath.Contains("/authserver/index.do", StringComparison.OrdinalIgnoreCase))
                    {
                        current = authServerLoginUrl;
                        continue;
                    }

                    current = next;
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (ReAuthPageParser.LooksLikePasswordExpiredHtml(body))
                {
                    return ReAuthCompletion.Fail(ReAuthFailure.PasswordExpired, "密码已过期");
                }

                if (ReAuthPageParser.LooksLikeReAuthHtml(body))
                {
                    return ReAuthCompletion.Fail(ReAuthFailure.Protocol, "二次认证后仍返回 re-auth 页");
                }

                return new ReAuthCompletion(
                    true,
                    ReAuthFailure.None,
                    "登录成功",
                    jar.Snapshot(),
                    current,
                    authServerLoginUrl);
            }
        }

        return ReAuthCompletion.Fail(ReAuthFailure.Upstream, "二次认证后的跳转次数过多");
    }

    private static bool HasTicket(Uri uri)
        => uri.Query.Contains("ticket=", StringComparison.OrdinalIgnoreCase);

    private Task<ReAuthCompletion> FollowFromAsync(
        Uri start,
        Uri authServerLoginUrl,
        XauatCookieJar jar,
        HttpClient client,
        Func<Uri, bool>? allowHop,
        CancellationToken cancellationToken)
    {
        // 从 callback 的下一跳开始；FollowLoginAsync 会继续处理 login?service=...
        return FollowLoginAsync(start, jar, client, allowHop, cancellationToken);
    }

    // ---------------------------------------------------------------- 解析辅助

    private static (string Code, string Message)? ParseSubmitResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var c) ? c.ToString() : "";
            var msg = root.TryGetProperty("msg", out var m) ? m.ToString() : "";
            return (code, msg);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (bool Success, bool RateLimited, string Message, string? MaskedTarget, int? CodeTime)? ParseSendResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var res = root.TryGetProperty("res", out var r) ? r.ToString() : "";
            var message = root.TryGetProperty("returnMessage", out var m) ? m.ToString() : "";
            var mobile = root.TryGetProperty("mobile", out var mb) ? mb.ToString() : null;
            int? codeTime = root.TryGetProperty("codeTime", out var ct) && ct.TryGetInt32(out var seconds)
                ? seconds
                : null;

            var success = res is "success" or "wechat_success" or "cpdaily_success";
            var rateLimited = res == "code_time_fail";
            return (success, rateLimited, string.IsNullOrEmpty(message) ? res : message, mobile, codeTime);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MfaCookie ToMfaCookie((string Host, string Name, string Value) cookie) => new()
    {
        Host = cookie.Host,
        Name = cookie.Name,
        Value = cookie.Value
    };

    private static void AddXhrHeaders(HttpRequestMessage request, Uri referer, Uri target)
    {
        request.Headers.Referrer = referer;
        request.Headers.TryAddWithoutValidation("Origin", target.GetLeftPart(UriPartial.Authority));
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
    }

    private static Uri AuthUrl(Uri loginUrl, string path)
    {
        var origin = loginUrl.GetLeftPart(UriPartial.Authority);

        // XAUAT 的登录页是 HTTP，但 combinedLogin.do/callback 等 re-auth 端点会先 302 到 HTTPS。
        // 如果继续用 HTTP origin，StartQrAsync 看到的第一跳是 authserver→HTTPS，而不是
        // 微信 qrconnect，会被误判为“不是微信跳转”。统一走 authserver 的 HTTPS origin。
        if (string.Equals(loginUrl.Host, XauatConstants.AuthServerHost, StringComparison.OrdinalIgnoreCase))
        {
            origin = "https://" + loginUrl.Host;
        }

        return new Uri(origin + path);
    }

    private static bool IsReAuthOrImproveInfo(Uri uri)
        => ReAuthPageParser.IsReAuthUrl(uri) || ReAuthPageParser.IsPasswordExpiredUrl(uri);

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
            or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static Uri Resolve(Uri current, Uri location)
        => location.IsAbsoluteUri ? location : new Uri(current, location);

    private static Uri Resolve(string current, Uri location)
        => Resolve(new Uri(current), location);

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            if (index <= 0) continue;
            result[Uri.UnescapeDataString(pair[..index])] = Uri.UnescapeDataString(pair[(index + 1)..]);
        }
        return result;
    }

    [GeneratedRegex(@"wx_errcode\s*=\s*(\d+)")]
    private static partial Regex WxErrCodeRegex();

    [GeneratedRegex(@"wx_code\s*=\s*['""]?([^'"";]+)")]
    private static partial Regex WxCodeRegex();

    [GeneratedRegex(@"/connect/qrcode/([A-Za-z0-9_-]{8,128})|uuid=([A-Za-z0-9_-]{8,128})|var\s+G=""([A-Za-z0-9_-]{8,128})""")]
    private static partial Regex QrUuidRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{8,128}$")]
    private static partial Regex QrUuidPattern();
}
