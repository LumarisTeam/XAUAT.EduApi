using EduApi.Data.Models;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Login.Unified;
using XAUAT.EduApi.Login.Xauat;

namespace XAUAT.EduApi.Login.Mfa;

/// <summary>
/// 二次认证第二步编排：加载 pending session、恢复 jar、提交验证码或轮询扫码、
/// 成功后映射回 <see cref="LoginResponse"/> / <see cref="UnifiedAuthResponse"/> 并写既有缓存。
/// </summary>
internal sealed class MfaLoginService(
    IMfaSessionStore sessionStore,
    ReAuthHandler reAuthHandler,
    IHttpClientFactory httpClientFactory,
    HostAllowList allowList,
    ICookieCodeService cookieCode,
    ILoginRedisStore redis,
    BanService banService,
    ILogger<MfaLoginService> logger) : IMfaLoginService
{
    public async Task<object> CompleteAsync(MfaSubmitRequest request, CancellationToken cancellationToken)
    {
        var session = await RequireSessionAsync(request.MfaSessionId);

        if (!string.IsNullOrEmpty(session.QrUuid))
        {
            throw new MfaMethodUnsupportedException("扫码方式请使用 /v1/login/mfa/status");
        }

        if (session.ReAuthType == 2)
        {
            throw new MfaMethodUnsupportedException("密码认证应在首次登录内完成");
        }

        if (!await sessionStore.TryAcquireLockAsync(session.SessionId, MfaCacheKeys.LockTtl))
        {
            throw new MfaBusyException();
        }

        try
        {
            var jar = RestoreJar(session);
            var client = CreateClient(session);
            var completion = await reAuthHandler.CompleteAsync(
                new Uri(session.AuthServerLoginUrl),
                session.Service,
                session.ReAuthType,
                session.IsMultifactor,
                request.TrustDevice ?? session.TrustDevice,
                jar,
                request.Code,
                password: null,
                encryptSalt: session.EncryptSalt,
                client: client,
                allowHop: AllowHopFor(session),
                cancellationToken: cancellationToken);

            if (completion.Success)
            {
                await sessionStore.DeleteAsync(session.SessionId);
                return await MapSuccessAsync(session, completion);
            }

            return await HandleFailureAsync(session, completion);
        }
        finally
        {
            await sessionStore.ReleaseLockAsync(session.SessionId);
        }
    }

    public async Task<MfaStatusResponse> PollQrAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = await RequireSessionAsync(sessionId);

        if (string.IsNullOrEmpty(session.QrUuid))
        {
            return new MfaStatusResponse
            {
                SessionId = session.SessionId,
                Status = "pending",
                Message = session.Hint,
                Mfa = ToChallenge(session)
            };
        }

        if (!await sessionStore.TryAcquirePollAsync(session.SessionId))
        {
            throw new MfaBusyException();
        }

        var jar = RestoreJar(session);
        var client = CreateClient(session);
        var poll = await reAuthHandler.PollQrAsync(
            new Uri(session.AuthServerLoginUrl),
            session.QrUuid,
            session.QrState ?? "",
            jar,
            client,
            AllowHopFor(session),
            cancellationToken);

        switch (poll.Status)
        {
            case QrPollStatus.Success when poll.Completion is not null:
                await sessionStore.DeleteAsync(session.SessionId);
                var mapped = await MapSuccessAsync(session, poll.Completion);
                return new MfaStatusResponse
                {
                    SessionId = session.SessionId,
                    Status = "success",
                    Message = "登录成功",
                    Login = mapped as LoginResponse,
                    Unified = mapped as UnifiedAuthResponse
                };

            case QrPollStatus.Pending:
            case QrPollStatus.Scanned:
                session.QrStatus = poll.Status == QrPollStatus.Scanned ? "scanned" : "pending";
                await sessionStore.UpdateAsync(session);
                return new MfaStatusResponse
                {
                    SessionId = session.SessionId,
                    Status = session.QrStatus,
                    Message = poll.Message,
                    Mfa = ToChallenge(session)
                };

            case QrPollStatus.Expired:
                await sessionStore.DeleteAsync(session.SessionId);
                throw new MfaSessionExpiredException();

            default:
                await IncrementAttemptsAsync(session);
                throw new MfaFailedException(poll.Message);
        }
    }

    private async Task<MfaPendingSession> RequireSessionAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new MfaSessionExpiredException();
        }

        return await sessionStore.GetAsync(sessionId) ?? throw new MfaSessionExpiredException();
    }

    private static XauatCookieJar RestoreJar(MfaPendingSession session)
    {
        var jar = new XauatCookieJar();
        jar.Restore(session.Cookies.Select(cookie => (cookie.Host, cookie.Name, cookie.Value)));
        return jar;
    }

    private HttpClient CreateClient(MfaPendingSession session)
    {
        var name = session.Kind == "unified"
            ? UnifiedSsoClient.ClientName
            : XauatSsoClient.ClientName;
        return httpClientFactory.CreateClient(name);
    }

    private Func<Uri, bool> AllowHopFor(MfaPendingSession session)
        => session.Kind == "unified"
            ? uri => allowList.IsAllowed(uri.Host)
            : uri => uri.Host.Equals(XauatConstants.AuthServerHost, StringComparison.OrdinalIgnoreCase)
                     || uri.Host.Equals(XauatConstants.StudentHost, StringComparison.OrdinalIgnoreCase);

    private async Task<object> MapSuccessAsync(
        MfaPendingSession session, ReAuthCompletion completion)
    {
        if (session.Kind == "unified")
        {
            var landing = completion.LandingUrl
                          ?? throw new MfaFailedException("二次认证完成但未获取落地页");

            var landingCookie = string.Join("; ", completion.Cookies
                .Where(cookie => HostMatches(cookie.Host, landing.Host))
                .Select(cookie => $"{cookie.Name}={cookie.Value}"));

            await UnifiedSsoClient.CacheReAuthOutcomeAsync(
                redis,
                session.SessionCacheKey,
                session.TicketCacheKey,
                completion.AuthServerUrl?.ToString() ?? session.AuthServerLoginUrl,
                landing.ToString(),
                landingCookie,
                completion.Cookies);

            return new UnifiedAuthResponse
            {
                Username = session.Username,
                StudentId = session.Username,
                Url = session.EntryUrl ?? "",
                AuthServerUrl = completion.AuthServerUrl?.ToString() ?? session.AuthServerLoginUrl,
                LandingUrl = landing.ToString(),
                Cookie = landingCookie,
                Cookies = completion.Cookies.Select(cookie => new UnifiedAuthCookie
                {
                    Host = cookie.Host,
                    Name = cookie.Name,
                    Value = cookie.Value
                }).ToList()
            };
        }

        var eduCookie = string.Join("; ", completion.Cookies
            .Where(cookie => string.Equals(cookie.Host, XauatConstants.StudentHost, StringComparison.OrdinalIgnoreCase))
            .Select(cookie => $"{cookie.Name}={cookie.Value}"));

        if (string.IsNullOrEmpty(eduCookie))
        {
            throw new LoginFailedException("二次认证完成但未获取教务会话");
        }

        var studentId = await cookieCode.GetCode(eduCookie);
        if (string.IsNullOrEmpty(studentId) || studentId == "/student/login")
        {
            throw new LoginFailedException();
        }

        await CacheStandardAsync(session, completion, eduCookie);
        return new LoginResponse
        {
            Success = true,
            StudentId = studentId,
            Cookie = eduCookie
        };
    }

    private async Task CacheStandardAsync(
        MfaPendingSession session, ReAuthCompletion completion, string eduCookie)
    {
        if (!redis.IsAvailable || string.IsNullOrWhiteSpace(session.PasswordHash))
        {
            return;
        }

        await redis.SetStringAsync(
            LoginCacheKeys.EduCookies(session.Username, session.PasswordHash),
            eduCookie,
            LoginCacheTtl.EduCookies);

        var ssoCookie = completion.Cookies
            .LastOrDefault(cookie =>
                string.Equals(cookie.Host, XauatConstants.AuthServerHost, StringComparison.OrdinalIgnoreCase) &&
                cookie.Name == "CASTGC")
            .Value;

        if (!string.IsNullOrWhiteSpace(ssoCookie))
        {
            await redis.SetStringAsync(
                LoginCacheKeys.SsoCookies(session.Username, session.PasswordHash),
                ssoCookie,
                LoginCacheTtl.SsoCookies);
        }
    }

    private async Task<object> HandleFailureAsync(
        MfaPendingSession session, ReAuthCompletion completion)
    {
        switch (completion.Failure)
        {
            case ReAuthFailure.PasswordExpired:
                await HandlePasswordExpiredAsync(session);
                throw new PasswordExpiredException(ReAuthPageParser.PasswordChangeUrl);

            case ReAuthFailure.InvalidCode:
            case ReAuthFailure.Unauthorized:
                await IncrementAttemptsAsync(session);
                throw new MfaFailedException(completion.Message);

            case ReAuthFailure.Protocol:
                throw new MfaProtocolException(completion.Message);

            default:
                throw new HttpRequestException(completion.Message);
        }

        throw new HttpRequestException(completion.Message);
    }

    private async Task HandlePasswordExpiredAsync(MfaPendingSession session)
    {
        if (!redis.IsAvailable) return;

        if (!string.IsNullOrWhiteSpace(session.PasswordHash))
        {
            await redis.SetStringAsync(
                LoginCacheKeys.PasswordExpired(session.Username, session.PasswordHash),
                "1",
                LoginCacheTtl.PasswordExpired);
            await banService.ClearUserCacheAsync(session.Username, session.PasswordHash);
        }

        if (!string.IsNullOrWhiteSpace(session.SessionCacheKey))
        {
            await redis.DeleteAsync(session.SessionCacheKey);
        }

        if (!string.IsNullOrWhiteSpace(session.TicketCacheKey))
        {
            await redis.DeleteAsync(session.TicketCacheKey);
        }
    }

    private async Task IncrementAttemptsAsync(MfaPendingSession session)
    {
        session.Attempts++;
        if (session.Attempts >= session.MaxAttempts)
        {
            await sessionStore.DeleteAsync(session.SessionId);
            throw new MfaSessionExpiredException();
        }

        await sessionStore.UpdateAsync(session);
    }

    private static MfaChallenge ToChallenge(MfaPendingSession session) => new()
    {
        SessionId = session.SessionId,
        Method = session.Method,
        ReAuthType = session.ReAuthType,
        MaskedTarget = session.MaskedTarget,
        QrCodeUrl = session.QrCodeUrl,
        QrImageBase64 = session.QrImageBase64,
        ExpiresAt = session.ExpiresAt,
        Hint = session.Hint,
        IsQr = !string.IsNullOrEmpty(session.QrUuid)
    };

    private static bool HostMatches(string cookieHost, string host)
        => string.Equals(cookieHost, host, StringComparison.OrdinalIgnoreCase) ||
           (cookieHost.StartsWith('.') && host.EndsWith(cookieHost, StringComparison.OrdinalIgnoreCase));
}
