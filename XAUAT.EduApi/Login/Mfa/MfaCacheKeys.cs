namespace XAUAT.EduApi.Login.Mfa;

/// <summary>
/// MFA 会话的 Redis 键。独立命名空间，不要放进 <see cref="LoginCacheKeys"/>
/// （后者是 Flask 互通契约）。
/// </summary>
internal static class MfaCacheKeys
{
    public const string Prefix = "security:mfa:";

    public static string Session(string id) => $"{Prefix}{id}";

    public static string Lock(string id) => $"{Prefix}lock:{id}";

    public static string Poll(string id) => $"{Prefix}poll:{id}";

    public static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan PollTtl = TimeSpan.FromSeconds(1);
}
