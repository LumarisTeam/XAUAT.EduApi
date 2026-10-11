using EduApi.Data.Models;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Localization;

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
    ILogger<UnifiedAuthService> logger) : IUnifiedAuthService
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
            new UnifiedAuthCommand(request.Url, request.Username, request.Password), cancellationToken);

        if (!outcome.Success)
        {
            logger.LogWarning("通用统一认证失败（{Failure}）：{Message}", outcome.Failure, outcome.Message);
            throw outcome.Failure switch
            {
                UnifiedAuthFailure.NotCasProtected => new UnifiedAuthRequestException(
                    ApiMessageKey.UnifiedAuthNotCasProtected, outcome.Message),
                UnifiedAuthFailure.BlockedTarget => new UnifiedAuthRequestException(
                    ApiMessageKey.UnifiedAuthBlockedTarget, outcome.Message),
                UnifiedAuthFailure.InvalidCredentials => new LoginFailedException(outcome.Message),
                _ => new HttpRequestException(outcome.Message)
            };
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

    private void WarnIfAllowAny()
    {
        if (!allowList.AllowAny) return;
        if (Interlocked.Exchange(ref _allowAnyWarned, 1) == 1) return;

        logger.LogWarning(
            "UNIFIED_AUTH_ALLOWED_HOSTS 含 {Any}：通用统一认证接口已放开任意域名，仅剩内网地址闸门",
            HostAllowList.AnyPattern);
    }
}
