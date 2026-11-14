using EduApi.Data.Models;
using Microsoft.Extensions.Logging.Abstractions;
using XAUAT.EduApi.Login;
using XAUAT.EduApi.Login.Mfa;
using XAUAT.EduApi.Login.Xauat;
using XAUAT.EduApi.Tests.Login.TestSupport;

namespace XAUAT.EduApi.Tests.Login.Mfa;

public class SsoAuthMfaTests
{
    private sealed class FakeMfaChallengeCoordinator : IMfaChallengeCoordinator
    {
        public MfaStartOutcome Outcome { get; set; } =
            MfaStartOutcome.Fail(ReAuthStartFailure.Upstream, "not configured");

        public Task<MfaStartOutcome> StartAsync(
            MfaSeed seed, MfaRequestOptions? options, string? password,
            string? passwordHash, CancellationToken cancellationToken)
            => Task.FromResult(Outcome);
    }

    private static MfaSeed PasswordSeed() => new()
    {
        Kind = MfaFlowKind.Standard,
        Username = "20240001",
        AuthServerLoginUrl = "http://authserver.xauat.edu.cn/authserver/login?service=...",
        Service = "https://swjw.xauat.edu.cn/student/sso/login",
        ReAuthType = 2,
        Method = "password",
        EncryptSalt = "0123456789abcdef",
        Cookies = []
    };

    private static SsoAuthService CreateService(
        FakeLoginRedisStore store, FakeSsoClient sso, IMfaChallengeCoordinator? coordinator = null)
        => new(
            sso,
            new BanService(store, NullLogger<BanService>.Instance),
            store,
            NullLogger<SsoAuthService>.Instance,
            mfaCoordinator: coordinator);

    [Fact]
    public async Task Login_Type2Completed_CachesEduCookieAndSsoCookie()
    {
        var store = new FakeLoginRedisStore();
        var sso = new FakeSsoClient { LoginResult = LoginToken.ReAuth(PasswordSeed()) };
        var coordinator = new FakeMfaChallengeCoordinator
        {
            Outcome = MfaStartOutcome.Done(new ReAuthCompletion(
                true,
                ReAuthFailure.None,
                "ok",
                [
                    ("swjw.xauat.edu.cn", "__pstsid__", "abc"),
                    ("authserver.xauat.edu.cn", "CASTGC", "TGT-1")
                ],
                new Uri("https://swjw.xauat.edu.cn/student/home"),
                new Uri("http://authserver.xauat.edu.cn/authserver/login?service=...")))
        };

        var service = CreateService(store, sso, coordinator);
        var result = await service.LoginAsync("xauat", "20240001", "pwd", default);

        Assert.True(result.Success);
        Assert.Contains("__pstsid__=abc", result.Cookies);

        var hash = PasswordEncryptor.Sha256Hex("pwd");
        Assert.True(store.ContainsKey(LoginCacheKeys.EduCookies("20240001", hash)));
        Assert.True(store.ContainsKey(LoginCacheKeys.SsoCookies("20240001", hash)));
    }

    [Fact]
    public async Task Login_Challenge_ReturnsMfaRequired()
    {
        var store = new FakeLoginRedisStore();
        var sso = new FakeSsoClient { LoginResult = LoginToken.ReAuth(PasswordSeed()) };
        var challenge = new MfaChallenge
        {
            SessionId = "s",
            Method = "otp",
            ReAuthType = 10,
            ExpiresAt = 123
        };

        var service = CreateService(store, sso, new FakeMfaChallengeCoordinator
        {
            Outcome = MfaStartOutcome.ChallengeOutcome(challenge)
        });

        var result = await service.LoginAsync("xauat", "20240001", "pwd", default);

        Assert.False(result.Success);
        Assert.Equal(SsoAuthFailureKind.MfaRequired, result.Failure);
        Assert.Same(challenge, result.Mfa);
    }

    [Fact]
    public async Task Login_PasswordExpiredFromFullLogin_SetsFlag()
    {
        var store = new FakeLoginRedisStore();
        var sso = new FakeSsoClient { LoginResult = LoginToken.PasswordExpired("expired") };
        var service = CreateService(store, sso);

        var result = await service.LoginAsync("xauat", "20240001", "pwd", default);

        Assert.Equal(SsoAuthFailureKind.PasswordExpired, result.Failure);
        Assert.True(store.ContainsKey(
            LoginCacheKeys.PasswordExpired("20240001", PasswordEncryptor.Sha256Hex("pwd"))));
    }

    [Fact]
    public async Task Login_PasswordExpiredFromCachedTicket_ClearsCacheAndSetsFlag()
    {
        var store = new FakeLoginRedisStore();
        var hash = PasswordEncryptor.Sha256Hex("pwd");
        store.SeedString(LoginCacheKeys.SsoCookies("20240001", hash), "old", LoginCacheTtl.SsoCookies);

        var sso = new FakeSsoClient { ExchangeResult = LoginToken.PasswordExpired("expired") };
        var service = CreateService(store, sso);

        var result = await service.LoginAsync("xauat", "20240001", "pwd", default);

        Assert.Equal(SsoAuthFailureKind.PasswordExpired, result.Failure);
        Assert.False(store.ContainsKey(LoginCacheKeys.SsoCookies("20240001", hash)));
        Assert.True(store.ContainsKey(LoginCacheKeys.PasswordExpired("20240001", hash)));
    }
}
