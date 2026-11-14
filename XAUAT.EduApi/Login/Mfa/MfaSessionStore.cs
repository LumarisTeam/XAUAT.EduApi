using System.Security.Cryptography;
using System.Text.Json;
using EduApi.Data.Models;

namespace XAUAT.EduApi.Login.Mfa;

/// <summary>
/// Redis 版待认证会话存储。序列化使用 <see cref="RedisJson.Options"/>（snake_case），
/// 与登录子系统的内部键风格一致。
/// </summary>
internal sealed class MfaSessionStore(
    ILoginRedisStore redis,
    ILogger<MfaSessionStore> logger) : IMfaSessionStore
{
    public bool IsAvailable => redis.IsAvailable;

    public async Task<MfaChallenge> CreateAsync(MfaPendingSession session)
    {
        if (!redis.IsAvailable)
        {
            throw new InvalidOperationException("Redis 不可用，无法创建 MFA 会话");
        }

        if (string.IsNullOrWhiteSpace(session.SessionId))
        {
            session.SessionId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        session.CreatedAt = now;
        session.ExpiresAt = now + (long)MfaCacheKeys.SessionTtl.TotalSeconds;
        if (session.MaxAttempts <= 0) session.MaxAttempts = 5;

        var json = JsonSerializer.Serialize(session, RedisJson.Options);
        var stored = await redis.SetStringAsync(
            MfaCacheKeys.Session(session.SessionId), json, MfaCacheKeys.SessionTtl);

        if (!stored)
        {
            throw new InvalidOperationException("写入 MFA 会话失败（Redis 不可用或写入被拒绝）");
        }

        return ToChallenge(session);
    }

    public async Task<MfaPendingSession?> GetAsync(string sessionId)
    {
        var json = await redis.GetStringAsync(MfaCacheKeys.Session(sessionId));
        if (string.IsNullOrEmpty(json)) return null;

        try
        {
            var session = JsonSerializer.Deserialize<MfaPendingSession>(json, RedisJson.Options);
            if (session is null) return null;

            if (session.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                await DeleteAsync(sessionId);
                return null;
            }

            return session;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "MFA 会话 {SessionId} 反序列化失败", sessionId);
            await DeleteAsync(sessionId);
            return null;
        }
    }

    public async Task<bool> UpdateAsync(MfaPendingSession session)
    {
        var remaining = session.ExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (remaining <= 0)
        {
            await DeleteAsync(session.SessionId);
            return false;
        }

        var json = JsonSerializer.Serialize(session, RedisJson.Options);
        return await redis.SetStringAsync(
            MfaCacheKeys.Session(session.SessionId), json, TimeSpan.FromSeconds(remaining));
    }

    public Task DeleteAsync(string sessionId)
        => redis.DeleteAsync(
            MfaCacheKeys.Session(sessionId),
            MfaCacheKeys.Lock(sessionId),
            MfaCacheKeys.Poll(sessionId));

    public Task<bool> TryAcquireLockAsync(string sessionId, TimeSpan ttl)
        => redis.SetIfNotExistsAsync(MfaCacheKeys.Lock(sessionId), "1", ttl);

    public Task ReleaseLockAsync(string sessionId)
        => redis.DeleteAsync(MfaCacheKeys.Lock(sessionId));

    public Task<bool> TryAcquirePollAsync(string sessionId)
        => redis.SetIfNotExistsAsync(MfaCacheKeys.Poll(sessionId), "1", MfaCacheKeys.PollTtl);

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
        IsQr = !string.IsNullOrEmpty(session.QrCodeUrl) || !string.IsNullOrEmpty(session.QrImageBase64)
    };
}
