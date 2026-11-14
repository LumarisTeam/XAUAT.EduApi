using EduApi.Data.Models;

namespace XAUAT.EduApi.Login.Mfa;

/// <summary>第一次登录遇到 re-auth 时的统一编排接缝（便于服务层单测）。</summary>
internal interface IMfaChallengeCoordinator
{
    Task<MfaStartOutcome> StartAsync(
        MfaSeed seed,
        MfaRequestOptions? options,
        string? password,
        string? passwordHash,
        CancellationToken cancellationToken);
}
