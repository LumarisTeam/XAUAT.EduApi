using EduApi.Data.Models;

namespace XAUAT.EduApi.Login.Mfa;

/// <summary>待认证 MFA 会话的存储接缝（Redis）。</summary>
internal interface IMfaSessionStore
{
    bool IsAvailable { get; }

    /// <summary>创建会话并返回给调用方的挑战信息。</summary>
    Task<MfaChallenge> CreateAsync(MfaPendingSession session);

    Task<MfaPendingSession?> GetAsync(string sessionId);

    /// <summary>写回会话；不延长绝对过期时间。</summary>
    Task<bool> UpdateAsync(MfaPendingSession session);

    Task DeleteAsync(string sessionId);

    /// <summary>多实例下防止同一会话并发提交（SET NX，15 秒）。</summary>
    Task<bool> TryAcquireLockAsync(string sessionId, TimeSpan ttl);

    Task ReleaseLockAsync(string sessionId);

    /// <summary>扫码轮询节流（SET NX，1 秒）。</summary>
    Task<bool> TryAcquirePollAsync(string sessionId);
}
