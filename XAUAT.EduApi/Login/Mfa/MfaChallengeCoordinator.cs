using EduApi.Data.Models;
using XAUAT.EduApi.Login.Unified;
using XAUAT.EduApi.Login.Xauat;

namespace XAUAT.EduApi.Login.Mfa;

internal sealed record MfaStartOutcome(
    bool Completed,
    MfaChallenge? Challenge,
    ReAuthCompletion? Completion,
    string? Message,
    ReAuthStartFailure Failure = ReAuthStartFailure.None,
    int? RetryAfterSeconds = null)
{
    public static MfaStartOutcome Done(ReAuthCompletion completion)
        => new(true, null, completion, null);

    public static MfaStartOutcome ChallengeOutcome(MfaChallenge challenge)
        => new(false, challenge, null, null);

    public static MfaStartOutcome Fail(
        ReAuthStartFailure failure, string message, int? retryAfterSeconds = null)
        => new(false, null, null, message, failure, retryAfterSeconds);
}

/// <summary>
/// 第一次登录遇到 re-auth 时的统一编排：恢复 jar、切换/发送/启动扫码、
/// type 2 直接完成，其余创建 pending session 并返回 challenge。
/// 三条登录链路共用，避免在 <c>SsoAuthService</c>/<c>UnifiedAuthService</c> 中重复协议细节。
/// </summary>
internal sealed class MfaChallengeCoordinator(
    ReAuthHandler reAuthHandler,
    IMfaSessionStore sessionStore,
    IHttpClientFactory httpClientFactory,
    HostAllowList allowList,
    ILogger<MfaChallengeCoordinator> logger) : IMfaChallengeCoordinator
{
    public async Task<MfaStartOutcome> StartAsync(
        MfaSeed seed,
        MfaRequestOptions? options,
        string? password,
        string? passwordHash,
        CancellationToken cancellationToken)
    {
        var requested = MfaMethod.Auto;
        if (!string.IsNullOrWhiteSpace(options?.Method) &&
            !MfaMethods.TryParse(options.Method, out requested))
        {
            return MfaStartOutcome.Fail(
                ReAuthStartFailure.MethodUnsupported, "无效的二次认证方式");
        }

        var trustDevice = options?.TrustDevice ?? true;
        var jar = new XauatCookieJar();
        jar.Restore(seed.Cookies.Select(cookie => (cookie.Host, cookie.Name, cookie.Value)));

        var clientName = seed.Kind == MfaFlowKind.Unified
            ? UnifiedSsoClient.ClientName
            : XauatSsoClient.ClientName;
        var client = httpClientFactory.CreateClient(clientName);
        var loginUrl = new Uri(seed.AuthServerLoginUrl);

        var page = new ReAuthPageParams(
            seed.ReAuthType,
            seed.Service,
            seed.EncryptSalt,
            seed.IsMultifactor,
            seed.IsSleepAccount,
            0,
            seed.Hint,
            null,
            null,
            null);

        logger.LogInformation(
            "MFA 启动：kind={Kind} requested={Requested} pageType={PageType} service={Service}",
            seed.Kind, requested, page.ReAuthType, page.Service);

        var start = await reAuthHandler.StartAsync(
            seed.Kind,
            seed.Username,
            loginUrl,
            jar,
            page,
            requested,
            trustDevice,
            seed.EntryUrl,
            seed.SessionCacheKey,
            seed.TicketCacheKey,
            cancellationToken);

        if (!start.Success)
        {
            logger.LogWarning(
                "MFA 启动失败：failure={Failure} message={Message} retryAfter={RetryAfter}",
                start.Failure, start.Message, start.RetryAfterSeconds);
            return MfaStartOutcome.Fail(start.Failure, start.Message, start.RetryAfterSeconds);
        }

        var effective = start.Seed!;
        if (effective.Method == "password")
        {
            var completion = await reAuthHandler.CompleteAsync(
                loginUrl,
                effective.Service,
                effective.ReAuthType,
                effective.IsMultifactor,
                trustDevice,
                jar,
                code: null,
                password: password,
                encryptSalt: effective.EncryptSalt,
                client: client,
                allowHop: AllowHopFor(effective.Kind),
                cancellationToken: cancellationToken);

            if (!completion.Success)
            {
                logger.LogWarning(
                    "MFA 完成失败（type 2）：failure={Failure} message={Message}",
                    completion.Failure, completion.Message);
            }

            return completion.Success
                ? MfaStartOutcome.Done(completion)
                : MfaStartOutcome.Fail(
                    completion.Failure == ReAuthFailure.PasswordExpired
                        ? ReAuthStartFailure.PasswordExpired
                        : ReAuthStartFailure.Upstream,
                    completion.Message);
        }

        if (!sessionStore.IsAvailable)
        {
            return MfaStartOutcome.Fail(
                ReAuthStartFailure.Upstream, "二次认证暂不可用（Redis 不可用）");
        }

        var pending = effective.ToPendingSession();
        pending.PasswordHash = passwordHash;
        pending.TrustDevice = trustDevice;
        var challenge = await sessionStore.CreateAsync(pending);
        return MfaStartOutcome.ChallengeOutcome(challenge);
    }

    private Func<Uri, bool> AllowHopFor(MfaFlowKind kind)
        => kind == MfaFlowKind.Standard
            ? uri => uri.Host.Equals(XauatConstants.AuthServerHost, StringComparison.OrdinalIgnoreCase)
                     || uri.Host.Equals(XauatConstants.StudentHost, StringComparison.OrdinalIgnoreCase)
            : uri => allowList.IsAllowed(uri.Host);
}
