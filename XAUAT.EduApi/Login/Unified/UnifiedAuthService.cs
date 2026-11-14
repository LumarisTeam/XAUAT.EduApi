using EduApi.Data.Models;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Login.Mfa;
using XAUAT.EduApi.Login.Xauat;

namespace XAUAT.EduApi.Login.Unified;

/// <summary>
/// 通用统一身份认证的编排层：前置校验（URL 形态、域名白名单）→ 交给
/// <see cref="IUnifiedSsoClient"/> 跑完整发现/登录/回跳 → 把结果映射成
/// "要么响应实体，要么一个带状态码语义的异常"。
/// <para>
/// 异常映射就是对外契约（与 <c>SsoLoginService</c> 同样的设计）：
/// 请求不合法 → <see cref="UnifiedAuthRequestException"/>（400）；
/// 凭据错 → <see cref="LoginFailedException"/>（401）；
/// 上游不可达 → <see cref="HttpRequestException"/>（502）。
/// </para>
/// </summary>
internal sealed class UnifiedAuthService(
    IUnifiedSsoClient ssoClient,
    HostAllowList allowList,
    ILogger<UnifiedAuthService> logger,
    IMfaChallengeCoordinator? mfaCoordinator = null,
    ILoginRedisStore? redis = null) : IUnifiedAuthService
{
    /// <summary>白名单被配成"全部放开"只警告一次，避免每个请求刷一行日志。</summary>
    private static int _allowAnyWarned;

    public async Task<UnifiedAuthResponse> LoginAsync(
        UnifiedAuthRequest request, CancellationToken cancellationToken = default)
    {
        WarnIfAllowAny();

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var target))
        {
            throw new UnifiedAuthRequestException(
                ApiMessageKey.UnifiedAuthInvalidTarget, $"目标地址不是合法的绝对 URL：{request.Url}");
        }

        if (target.Scheme is not ("http" or "https"))
        {
            throw new UnifiedAuthRequestException(
                ApiMessageKey.UnifiedAuthInvalidTarget, $"仅支持 http/https 地址，实际为 {target.Scheme}");
        }

        if (!allowList.IsAllowed(target.Host))
        {
            throw new UnifiedAuthRequestException(
                ApiMessageKey.UnifiedAuthHostNotAllowed, $"目标域名 {target.Host} 不在白名单内");
        }

        logger.LogInformation("通用统一认证请求：目标 {Host}，学号 {Username}", target.Host, request.Username);

        var outcome = await ssoClient.LoginAsync(
            new UnifiedAuthCommand(request.Url, request.Username, request.Password)
            {
                MfaMethod = request.MfaMethod,
                TrustDevice = request.TrustDevice
            }, cancellationToken);

        if (!outcome.Success)
        {
            logger.LogWarning("通用统一认证失败（{Failure}）：{Message}", outcome.Failure, outcome.Message);
            switch (outcome.Failure)
            {
                case UnifiedAuthFailure.MfaRequired:
                    return await HandleMfaAsync(outcome, request, cancellationToken);
                case UnifiedAuthFailure.PasswordExpired:
                    await MarkPasswordExpiredAsync(request);
                    throw new PasswordExpiredException(ReAuthPageParser.PasswordChangeUrl);
                case UnifiedAuthFailure.MfaUnavailable:
                    throw new MfaUnavailableException();
                case UnifiedAuthFailure.NotCasProtected:
                    throw new UnifiedAuthRequestException(
                        ApiMessageKey.UnifiedAuthNotCasProtected, outcome.Message);
                case UnifiedAuthFailure.BlockedTarget:
                    throw new UnifiedAuthRequestException(
                        ApiMessageKey.UnifiedAuthBlockedTarget, outcome.Message);
                case UnifiedAuthFailure.InvalidCredentials:
                    throw new LoginFailedException(outcome.Message);
                default:
                    throw new HttpRequestException(outcome.Message);
            }
        }

        return new UnifiedAuthResponse
        {
            Username = request.Username,
            // 通用接口不可能知道目标系统的学号列怎么取，原样回显调用方给的用户名
            StudentId = request.Username,
            Url = request.Url,
            AuthServerUrl = outcome.AuthServerUrl ?? "",
            LandingUrl = outcome.LandingUrl ?? "",
            Cookie = outcome.LandingCookie ?? "",
            Cookies = (outcome.Cookies ?? []).Select(cookie => new UnifiedAuthCookie
            {
                Host = cookie.Host,
                Name = cookie.Name,
                Value = cookie.Value
            }).ToList()
        };
    }

    private async Task<UnifiedAuthResponse> HandleMfaAsync(
        UnifiedAuthOutcome outcome, UnifiedAuthRequest request, CancellationToken cancellationToken)
    {
        if (outcome.Mfa is null || mfaCoordinator is null)
        {
            throw new MfaUnavailableException();
        }

        var options = new MfaRequestOptions
        {
            Method = request.MfaMethod,
            TrustDevice = request.TrustDevice
        };

        var mfa = await mfaCoordinator.StartAsync(
            outcome.Mfa,
            options,
            request.Password,
            PasswordEncryptor.Sha256Hex(request.Password),
            cancellationToken);

        if (mfa.Completed && mfa.Completion is not null)
        {
            var completion = mfa.Completion;
            var landing = completion.LandingUrl
                          ?? throw new MfaFailedException("二次认证完成但未获取落地页");

            var landingCookie = string.Join("; ", completion.Cookies
                .Where(cookie => HostMatches(cookie.Host, landing.Host))
                .Select(cookie => $"{cookie.Name}={cookie.Value}"));

            return new UnifiedAuthResponse
            {
                Username = request.Username,
                StudentId = request.Username,
                Url = request.Url,
                AuthServerUrl = completion.AuthServerUrl?.ToString() ?? outcome.AuthServerUrl ?? "",
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

        if (mfa.Challenge is not null)
        {
            throw new MfaRequiredException(mfa.Challenge);
        }

        if (mfa.Failure == ReAuthStartFailure.PasswordExpired)
        {
            await MarkPasswordExpiredAsync(request);
            throw new PasswordExpiredException(ReAuthPageParser.PasswordChangeUrl);
        }

        if (mfa.Failure == ReAuthStartFailure.MethodUnsupported)
        {
            throw new MfaMethodUnsupportedException(mfa.Message ?? "不支持的二次认证方式");
        }

        throw new MfaUnavailableException();
    }

    private async Task MarkPasswordExpiredAsync(UnifiedAuthRequest request)
    {
        if (redis?.IsAvailable != true) return;
        await redis.SetStringAsync(
            LoginCacheKeys.PasswordExpired(
                request.Username, PasswordEncryptor.Sha256Hex(request.Password)),
            "1",
            LoginCacheTtl.PasswordExpired);
    }

    private static bool HostMatches(string cookieHost, string host)
        => string.Equals(cookieHost, host, StringComparison.OrdinalIgnoreCase) ||
           (cookieHost.StartsWith('.') && host.EndsWith(cookieHost, StringComparison.OrdinalIgnoreCase));

    private void WarnIfAllowAny()
    {
        if (!allowList.AllowAny) return;
        if (Interlocked.Exchange(ref _allowAnyWarned, 1) == 1) return;

        logger.LogWarning(
            "UNIFIED_AUTH_ALLOWED_HOSTS 含 {Any}：通用统一认证接口已放开任意域名，仅剩内网地址闸门",
            HostAllowList.AnyPattern);
    }
}
