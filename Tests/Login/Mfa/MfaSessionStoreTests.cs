using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using XAUAT.EduApi.Login;
using XAUAT.EduApi.Login.Mfa;
using XAUAT.EduApi.Tests.Login.TestSupport;

namespace XAUAT.EduApi.Tests.Login.Mfa;

public class MfaSessionStoreTests
{
    [Fact]
    public async Task Create_StoresSessionWithFiveMinuteTtl()
    {
        var redis = new FakeLoginRedisStore();
        var store = new MfaSessionStore(redis, NullLogger<MfaSessionStore>.Instance);

        var challenge = await store.CreateAsync(new MfaPendingSession
        {
            Username = "u",
            Method = "otp",
            ReAuthType = 10,
            Hint = "请输入6位有效的安全令牌"
        });

        Assert.False(string.IsNullOrWhiteSpace(challenge.SessionId));
        var key = MfaCacheKeys.Session(challenge.SessionId);
        Assert.True(redis.ContainsKey(key));
        Assert.Equal(TimeSpan.FromMinutes(5), redis.TtlOf(key));

        var loaded = await store.GetAsync(challenge.SessionId);
        Assert.NotNull(loaded);
        Assert.Equal("u", loaded!.Username);
        Assert.Equal(10, loaded.ReAuthType);
    }

    [Fact]
    public async Task Get_Expired_DeletesAndReturnsNull()
    {
        var redis = new FakeLoginRedisStore();
        var store = new MfaSessionStore(redis, NullLogger<MfaSessionStore>.Instance);
        var session = new MfaPendingSession
        {
            SessionId = "expired",
            Username = "u",
            ExpiresAt = 1
        };

        redis.SeedString(
            MfaCacheKeys.Session("expired"),
            JsonSerializer.Serialize(session, RedisJson.Options));

        Assert.Null(await store.GetAsync("expired"));
        Assert.False(redis.ContainsKey(MfaCacheKeys.Session("expired")));
    }

    [Fact]
    public async Task Lock_IsExclusiveUntilReleased()
    {
        var redis = new FakeLoginRedisStore();
        var store = new MfaSessionStore(redis, NullLogger<MfaSessionStore>.Instance);

        Assert.True(await store.TryAcquireLockAsync("s", TimeSpan.FromSeconds(15)));
        Assert.False(await store.TryAcquireLockAsync("s", TimeSpan.FromSeconds(15)));
        await store.ReleaseLockAsync("s");
        Assert.True(await store.TryAcquireLockAsync("s", TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task Create_WhenRedisUnavailable_Throws()
    {
        var redis = new FakeLoginRedisStore { Available = false };
        var store = new MfaSessionStore(redis, NullLogger<MfaSessionStore>.Instance);

        Assert.False(store.IsAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateAsync(new MfaPendingSession { Username = "u" }));
    }
}
