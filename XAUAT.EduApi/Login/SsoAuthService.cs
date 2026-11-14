using EduApi.Data.Models;
using XAUAT.EduApi.Login.Mfa;
using XAUAT.EduApi.Login.Xauat;

namespace XAUAT.EduApi.Login;

internal enum SsoAuthFailureKind
{
    None,
    InvalidCredentials,
    MfaRequired,
    MfaUnavailable,
    MfaMethodUnsupported,
    PasswordExpired,
    Banned,
    RateLimited
}

/// <summary>登录结果，对应 Flask <c>xauat_login.login</c> 返回的那个 dict。</summary>
/// <param name="Success">是否成功。</param>
/// <param name="Message">失败时的中文提示（成功时为空）。</param>
/// <param name="Cookies">成功时的 cookie 串。</param>
/// <param name="Banned">是否因封禁而失败。</param>
/// <param name="BanReason">封禁理由。</param>
/// <param name="BanUntil">解封时刻（unix 秒）。</param>
internal sealed record SsoAuthResult(
    bool Success,
    string Message,
    string? Cookies = null,
    bool Banned = false,
    string? BanReason = null,
    long? BanUntil = null,
    SsoAuthFailureKind Failure = SsoAuthFailureKind.None,
    MfaChallenge? Mfa = null)
{
    public static SsoAuthResult Ok(string cookies) => new(true, "", cookies);
    public static SsoAuthResult Fail(string message)
        => new(false, message, Failure: SsoAuthFailureKind.InvalidCredentials);

    public static SsoAuthResult Ban(string message, string? reason, long? until)
        => new(false, message, Banned: true, BanReason: reason, BanUntil: until,
            Failure: SsoAuthFailureKind.Banned);

    public static SsoAuthResult MfaRequired(MfaChallenge challenge)
        => new(false, challenge.Message, Failure: SsoAuthFailureKind.MfaRequired, Mfa: challenge);

    public static SsoAuthResult MfaUnavailable(string message)
        => new(false, message, Failure: SsoAuthFailureKind.MfaUnavailable);

    public static SsoAuthResult MfaMethodUnsupported(string message)
        => new(false, message, Failure: SsoAuthFailureKind.MfaMethodUnsupported);

    public static SsoAuthResult PasswordExpired(string message)
        => new(false, message, Failure: SsoAuthFailureKind.PasswordExpired);
}

/// <summary>
/// 登录编排。逐字移植自 Flask 的 <c>xauat_login.login</c>。
/// <para>
/// 三层优先级：<b>缓存的 eduCookie → 缓存的 SSO 票据换票 → 完整登录</b>。
/// 几个容易写错、但必须保留的点：
/// </para>
/// <list type="bullet">
/// <item>命中 eduCookie 缓存时**不消耗限流额度**（否则正常用户会被自己的缓存请求限流）。</item>
/// <item>限流额度是**预占制**：先占名额再打网络，成功后归还。失败不会重复计数。</item>
/// <item>完整登录（不跟随重定向）通常只能拿到 SSO 票据，需要再换一次票才有教务会话。</item>
/// <item>失败次数达到 5 次时清除该用户的 cookie 缓存。</item>
/// </list>
/// <para>
/// <b>与 LoginApi 的一处差别</b>：这里的入参不再有 <c>ITestAccountResolver</c>，
/// 测试账号短路改由上层 <see cref="SsoLoginService"/> 承担（它也正是 EduApi 原本做这件事的
/// 地方）。原 LoginApi 的 AuthService 里那份短路分支属于重复——留着会让测试账号的伪造 cookie
/// 被送进 <c>CookieCodeService.GetCode</c> 去打真教务系统。
/// </para>
/// </summary>
internal sealed class SsoAuthService(
    IXauatSsoClient ssoClient,
    BanService banService,
    ILoginRedisStore redis,
    ILogger<SsoAuthService> logger,
    CasAttemptGuard? casGuard = null,
    IMfaChallengeCoordinator? mfaCoordinator = null)
{
    /// <summary>唯一支持的学校。NWAFU 未随本次迁移，仍留在 Flask。</summary>
    private const string SupportedSchool = "xauat";

    // 与 Flask 完全一致的中文文案。改动会直接改变客户端看到的内容，勿"优化"。
    private const string BannedMessage = "账户已被暂时封禁，请稍后重试或联系管理员";
    private const string BannedNowMessage = "账户已被暂时封禁，请在24小时后重试或联系管理员";
    private const string RateLimitedMessage = "登录失败次数过多，请在20分钟后重试";
    private const string AutoBanReason = "连续触发登录限流";
    private const string PasswordExpiredMessage = "当前密码已过期，请先修改统一身份认证密码";

    public async Task<SsoAuthResult> LoginAsync(
        string school, string username, string password, CancellationToken cancellationToken,
        MfaRequestOptions? mfaOptions = null)
    {
        var schoolKey = school.ToLowerInvariant();
        logger.LogInformation("登录请求: school={School}, username={Username}", schoolKey, username);

        if (schoolKey != SupportedSchool)
        {
            logger.LogWarning("不支持的学校: {School}", school);
            return SsoAuthResult.Fail($"不支持的学校: {school}");
        }

        // ---- Redis 不可用：降级为"无缓存、无限流"的完整登录
        if (!redis.IsAvailable)
        {
            logger.LogWarning("Redis 不可用，将执行完整登录流程（无缓存）");
            await GuardCasAsync(username);
            return await FullLoginWithoutCacheAsync(username, password, mfaOptions, cancellationToken);
        }

        var passwordHash = PasswordEncryptor.Sha256Hex(password);
        var accountKey = LoginCacheKeys.AccountKey(schoolKey, username);

        // ---- 第 0 步：封禁检查（在任何缓存之前）
        var existingBan = await banService.GetBanStatusAsync(accountKey);
        if (existingBan is not null)
        {
            return SsoAuthResult.Ban(BannedMessage, existingBan.Value.Info.Reason, existingBan.Value.Info.UnbanAt);
        }

        if (await redis.GetStringAsync(LoginCacheKeys.PasswordExpired(username, passwordHash)) is { Length: > 0 })
        {
            return SsoAuthResult.PasswordExpired(PasswordExpiredMessage);
        }

        var eduCookieKey = LoginCacheKeys.EduCookies(username, passwordHash);
        var ssoCookieKey = LoginCacheKeys.SsoCookies(username, passwordHash);
        var rateLimitKey = LoginCacheKeys.LoginFailed(username, passwordHash);

        // ---- 优先级 1：缓存的 eduCookie
        var cachedCookies = await redis.GetStringAsync(eduCookieKey);
        if (!string.IsNullOrEmpty(cachedCookies))
        {
            logger.LogInformation("用户 {Username} 从缓存中获取登录 cookies", username);
            return SsoAuthResult.Ok(cachedCookies);
        }

        // ---- 优先级 2：缓存的 SSO 票据，换一次票
        var cachedSso = await redis.GetStringAsync(ssoCookieKey);
        if (!string.IsNullOrEmpty(cachedSso))
        {
            logger.LogInformation("用户 {Username} 从缓存中获取 sso 登录 cookies", username);

            // 复刻 Flask 的 cookies 语义：按调用新建 jar，不跨请求共享
            var jar = new XauatCookieJar();
            var ssoResult = await ssoClient.ExchangeSsoTicketAsync(cachedSso, jar, cancellationToken, username);

            if (ssoResult.Mfa is not null)
            {
                var mfa = await StartMfaAsync(
                    ssoResult.Mfa, password, passwordHash, mfaOptions, cancellationToken);
                if (mfa.Completed && mfa.Completion is not null)
                {
                    var mapped = MapCompletion(mfa.Completion);
                    if (mapped is not null)
                    {
                        await redis.SetStringAsync(eduCookieKey, mapped.Value.EduCookie, LoginCacheTtl.EduCookies);
                        if (!string.IsNullOrWhiteSpace(mapped.Value.SsoCookie))
                            await redis.SetStringAsync(ssoCookieKey, mapped.Value.SsoCookie, LoginCacheTtl.SsoCookies);
                        logger.LogInformation("用户 {Username} 完成二次认证，已缓存 cookies", username);
                        return SsoAuthResult.Ok(mapped.Value.EduCookie);
                    }
                }

                if (mfa.Challenge is not null) return SsoAuthResult.MfaRequired(mfa.Challenge);
                return await MapMfaStartFailureAsync(mfa, username, passwordHash);
            }

            if (ssoResult.Failure == LoginFailureKind.PasswordExpired)
            {
                await HandlePasswordExpiredAsync(username, passwordHash);
                return SsoAuthResult.PasswordExpired(PasswordExpiredMessage);
            }

            if (ssoResult.Success && !string.IsNullOrEmpty(ssoResult.EduCookie))
            {
                await redis.SetStringAsync(eduCookieKey, ssoResult.EduCookie, LoginCacheTtl.EduCookies);
                logger.LogInformation("用户 {Username} 登录成功，已缓存 cookies", username);
                return SsoAuthResult.Ok(ssoResult.EduCookie);
            }

            if (!ssoResult.Success)
            {
                // 票据失效是正常现象（7 天 TTL 内也可能被上游吊销），落回完整登录即可
                logger.LogWarning("用户 {Username} SSO 登录失败: {Message}", username, ssoResult.Message);
            }
        }

        await GuardCasAsync(username);

        // ---- 限流检查：预占名额
        var (isRateLimited, failedCount) = await banService.AcquireRateLimitSlotAsync(rateLimitKey);
        if (isRateLimited)
        {
            logger.LogWarning("用户 {Username} 登录失败次数过多（{Count} 次），已被限流", username, failedCount);

            var (bannedNow, banInfo) = await banService.RecordRateLimitAndMaybeBanAsync(accountKey, AutoBanReason);
            if (bannedNow)
            {
                return SsoAuthResult.Ban(BannedNowMessage, banInfo?.Reason, banInfo?.UnbanAt);
            }

            return SsoAuthResult.Fail(RateLimitedMessage);
        }

        // ---- 优先级 3：完整登录
        logger.LogInformation("用户 {Username} 执行完整登录流程", username);

        var loginJar = new XauatCookieJar();
        var result = await ssoClient.LoginAsync(username, password, loginJar, cancellationToken, mfaOptions);

        if (result.Mfa is not null)
        {
            await banService.ReleaseRateLimitSlotAsync(rateLimitKey);
            var mfa = await StartMfaAsync(
                result.Mfa, password, passwordHash, mfaOptions, cancellationToken);

            if (mfa.Completed && mfa.Completion is not null)
            {
                var mapped = MapCompletion(mfa.Completion);
                if (mapped is not null)
                {
                    await redis.SetStringAsync(eduCookieKey, mapped.Value.EduCookie, LoginCacheTtl.EduCookies);
                    if (!string.IsNullOrWhiteSpace(mapped.Value.SsoCookie))
                        await redis.SetStringAsync(ssoCookieKey, mapped.Value.SsoCookie, LoginCacheTtl.SsoCookies);
                    logger.LogInformation("用户 {Username} 完成二次认证，已缓存 cookies", username);
                    return SsoAuthResult.Ok(mapped.Value.EduCookie);
                }
            }

            if (mfa.Challenge is not null) return SsoAuthResult.MfaRequired(mfa.Challenge);
            return await MapMfaStartFailureAsync(mfa, username, passwordHash);
        }

        if (result.Failure == LoginFailureKind.PasswordExpired)
        {
            await banService.ReleaseRateLimitSlotAsync(rateLimitKey);
            await HandlePasswordExpiredAsync(username, passwordHash);
            return SsoAuthResult.PasswordExpired(PasswordExpiredMessage);
        }

        if (!result.Success)
        {
            logger.LogWarning("用户 {Username} 登录失败: {Message}", username, result.Message);

            // 名额已在登录前预占，无需再递增
            if (failedCount >= BanService.MaxAttempts)
            {
                await banService.ClearUserCacheAsync(username, passwordHash);
                logger.LogWarning("用户 {Username} 登录失败次数达到阈值，已清除缓存", username);
            }

            return SsoAuthResult.Fail(result.Message);
        }

        // 完整登录通常只拿到 SSO 票据（CASTGC），教务系统不认它，必须再换一次票
        if (string.IsNullOrWhiteSpace(result.EduCookie) && !string.IsNullOrWhiteSpace(result.SsoCookie))
        {
            logger.LogInformation("用户 {Username} 完整登录仅获得 ssoCookie，尝试换取 eduCookie", username);

            var exchangeJar = new XauatCookieJar();
            var exchangeResult = await ssoClient.ExchangeSsoTicketAsync(
                result.SsoCookie, exchangeJar, cancellationToken, username);

            if (exchangeResult.Success && !string.IsNullOrWhiteSpace(exchangeResult.EduCookie))
            {
                result = exchangeResult;
                logger.LogInformation("用户 {Username} 成功换取 eduCookie", username);
            }
            else
            {
                logger.LogWarning("用户 {Username} 换取 eduCookie 失败: {Message}", username, exchangeResult.Message);
            }
        }

        // 登录成功：归还预占的名额
        await banService.ReleaseRateLimitSlotAsync(rateLimitKey);

        if (!string.IsNullOrWhiteSpace(result.EduCookie))
        {
            await redis.SetStringAsync(eduCookieKey, result.EduCookie, LoginCacheTtl.EduCookies);
            logger.LogInformation("用户 {Username} 成功缓存 eduCookie", username);
        }

        if (!string.IsNullOrWhiteSpace(result.SsoCookie))
        {
            await redis.SetStringAsync(ssoCookieKey, result.SsoCookie, LoginCacheTtl.SsoCookies);
            logger.LogInformation("用户 {Username} 成功缓存 ssoCookie", username);
        }

        logger.LogInformation("用户 {Username} 登录成功", username);

        return SsoAuthResult.Ok(string.IsNullOrEmpty(result.EduCookie) ? result.SsoCookie : result.EduCookie);
    }

    private async Task<MfaStartOutcome> StartMfaAsync(
        MfaSeed seed, string password, string passwordHash,
        MfaRequestOptions? mfaOptions, CancellationToken cancellationToken)
    {
        if (mfaCoordinator is null)
        {
            return MfaStartOutcome.Fail(ReAuthStartFailure.Upstream, "二次认证暂不可用");
        }

        return await mfaCoordinator.StartAsync(seed, mfaOptions, password, passwordHash, cancellationToken);
    }

    private static (string EduCookie, string SsoCookie)? MapCompletion(ReAuthCompletion completion)
    {
        var eduCookie = string.Join("; ", completion.Cookies
            .Where(cookie => string.Equals(cookie.Host, XauatConstants.StudentHost, StringComparison.OrdinalIgnoreCase))
            .Select(cookie => $"{cookie.Name}={cookie.Value}"));
        if (string.IsNullOrEmpty(eduCookie)) return null;

        var ssoCookie = completion.Cookies
            .LastOrDefault(cookie =>
                string.Equals(cookie.Host, XauatConstants.AuthServerHost, StringComparison.OrdinalIgnoreCase) &&
                cookie.Name == "CASTGC")
            .Value ?? "";

        return (eduCookie, ssoCookie);
    }

    private async Task HandlePasswordExpiredAsync(string username, string passwordHash)
    {
        if (!redis.IsAvailable) return;
        await redis.SetStringAsync(
            LoginCacheKeys.PasswordExpired(username, passwordHash), "1", LoginCacheTtl.PasswordExpired);
        await banService.ClearUserCacheAsync(username, passwordHash);
    }

    private async Task<SsoAuthResult> MapMfaStartFailureAsync(
        MfaStartOutcome outcome, string username, string passwordHash)
    {
        if (outcome.Failure == ReAuthStartFailure.PasswordExpired)
        {
            await HandlePasswordExpiredAsync(username, passwordHash);
            return SsoAuthResult.PasswordExpired(PasswordExpiredMessage);
        }

        return outcome.Failure switch
        {
            ReAuthStartFailure.MethodUnsupported =>
                SsoAuthResult.MfaMethodUnsupported(outcome.Message ?? "不支持的二次认证方式"),
            _ => SsoAuthResult.MfaUnavailable(outcome.Message ?? "二次认证暂不可用")
        };
    }

    private async Task GuardCasAsync(string username)
    {
        if (casGuard is not null && await casGuard.AcquireAsync(CasAttemptGuard.XauatHost, username) is { } retry)
            throw new XAUAT.EduApi.Exceptions.RateLimitException(retry);
    }

    /// <summary>
    /// Redis 不可用时的降级登录。
    /// <para>
    /// <b>与 Flask 的一处刻意差异</b>：Flask 在这条路径上直接用 <c>login()</c> 的结果，
    /// <b>不做</b> SSO 票据换教务会话的那一步，于是通常会把教务系统不认的 <c>CASTGC</c>
    /// 当成功结果返回（调用方随后必然 401）。这里补上了换票——降级路径的目标就是"仍然能登录"，
    /// 返回一个用不了的 cookie 没有意义。除此之外（无缓存、无限流、无封禁检查）与 Flask 一致。
    /// </para>
    /// </summary>
    private async Task<SsoAuthResult> FullLoginWithoutCacheAsync(
        string username, string password, MfaRequestOptions? mfaOptions, CancellationToken cancellationToken)
    {
        var jar = new XauatCookieJar();
        var result = await ssoClient.LoginAsync(username, password, jar, cancellationToken, mfaOptions);

        if (result.Mfa is not null)
        {
            var passwordHash = PasswordEncryptor.Sha256Hex(password);
            var mfa = await StartMfaAsync(result.Mfa, password, passwordHash, mfaOptions, cancellationToken);
            if (mfa.Completed && mfa.Completion is not null)
            {
                var mapped = MapCompletion(mfa.Completion);
                if (mapped is not null) return SsoAuthResult.Ok(mapped.Value.EduCookie);
            }

            if (mfa.Challenge is not null) return SsoAuthResult.MfaRequired(mfa.Challenge);
            return await MapMfaStartFailureAsync(mfa, username, passwordHash);
        }

        if (result.Failure == LoginFailureKind.PasswordExpired)
        {
            return SsoAuthResult.PasswordExpired(PasswordExpiredMessage);
        }

        if (!result.Success)
        {
            logger.LogWarning("用户 {Username} 登录失败: {Message}", username, result.Message);
            return SsoAuthResult.Fail(result.Message);
        }

        if (string.IsNullOrWhiteSpace(result.EduCookie) && !string.IsNullOrWhiteSpace(result.SsoCookie))
        {
            var exchangeJar = new XauatCookieJar();
            var exchangeResult = await ssoClient.ExchangeSsoTicketAsync(
                result.SsoCookie, exchangeJar, cancellationToken, username);
            if (exchangeResult.Success && !string.IsNullOrWhiteSpace(exchangeResult.EduCookie))
            {
                result = exchangeResult;
            }
        }

        return SsoAuthResult.Ok(string.IsNullOrEmpty(result.EduCookie) ? result.SsoCookie : result.EduCookie);
    }
}
