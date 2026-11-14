using XAUAT.EduApi.Login.Mfa;

namespace XAUAT.EduApi.Login.Xauat;

internal enum LoginFailureKind
{
    None,
    InvalidCredentials,
    MfaRequired,
    PasswordExpired,
    Upstream
}

/// <summary>
/// 一次登录/换票的结果。对应 Flask 的 <c>LoginTokenModel</c>。
/// <para>
/// <see cref="EduCookie"/> 才是能访问教务系统的会话 cookie（<c>__pstsid__</c>/<c>SESSION</c>）；
/// <see cref="SsoCookie"/> 是 CAS 票据（<c>CASTGC</c>），教务系统**不认**它，
/// 必须再经 <c>authserver</c> 换一次票才能拿到 eduCookie。
/// </para>
/// </summary>
internal sealed record LoginToken(
    bool Success,
    string Message,
    string EduCookie,
    string SsoCookie,
    LoginFailureKind Failure = LoginFailureKind.None,
    MfaSeed? Mfa = null)
{
    public static LoginToken Failed(string message)
        => new(false, message, "", "", LoginFailureKind.InvalidCredentials);

    public static LoginToken ReAuth(MfaSeed seed, string message = "需要二次认证")
        => new(false, message, "", "", LoginFailureKind.MfaRequired, seed);

    public static LoginToken PasswordExpired(string message)
        => new(false, message, "", "", LoginFailureKind.PasswordExpired);

    /// <summary>返回给调用方的 cookie 串：优先 eduCookie，退化到 ssoCookie。</summary>
    public string EffectiveCookie => !string.IsNullOrEmpty(EduCookie) ? EduCookie : SsoCookie;
}
