using System.Collections.Concurrent;
using XAUAT.EduApi.Login.Xauat;

namespace XAUAT.EduApi.Login;

/// <summary>限制同一认证服务器、同一账号的凭据提交次数；成功也计入，防止客户端反复登录。</summary>
internal sealed class CasAttemptGuard(ILoginRedisStore redis)
{
    internal const int MaxAttempts = 5;
    internal const int WindowSeconds = 1200;
    internal const string XauatHost = "authserver.xauat.edu.cn";
    private readonly ConcurrentDictionary<string, (long Window, int Count)> _local = new();

    public async Task<int?> AcquireAsync(string host, string username)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var window = now / WindowSeconds;
        var retryAfter = (int)(WindowSeconds - now % WindowSeconds);
        // 哈希账号，避免在新增 Redis 键中暴露用户名；不按密码区分，防止更换密码绕过。
        var identity = PasswordEncryptor.Sha256Hex($"{host.ToLowerInvariant()}\n{username.Trim().ToLowerInvariant()}");
        var local = _local.AddOrUpdate(identity, _ => (window, 1),
            (_, old) => old.Window == window ? (window, old.Count + 1) : (window, 1));
        if (_local.Count > 10000)
            foreach (var item in _local)
                if (item.Value.Window < window) _local.TryRemove(item.Key, out _);

        // 键包含窗口编号：即使设置 TTL 失败，也不会产生永久限流。
        var key = $"security:cas_attempt:{identity}:{window}";
        var count = redis.IsAvailable ? await redis.IncrementAsync(key) : 0;
        if (count == 1) await redis.ExpireAsync(key, TimeSpan.FromSeconds(WindowSeconds * 2));
        return local.Count > MaxAttempts || count > MaxAttempts ? retryAfter : null;
    }
}
