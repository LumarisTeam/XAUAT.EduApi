namespace XAUAT.EduApi.Login.Unified;

/// <summary>一次通用统一身份认证的入参。</summary>
/// <param name="EntryUrl">受认证保护的目标地址（调用方给的原始字符串）。</param>
internal sealed record UnifiedAuthCommand(string EntryUrl, string Username, string Password);

/// <summary>失败归类，决定控制器回哪个状态码。</summary>
internal enum UnifiedAuthFailure
{
    /// <summary>成功。</summary>
    None,

    /// <summary>用户名或密码错（含验证码拦截等认证服务器明确拒绝的情况）→ 401。</summary>
    InvalidCredentials,

    /// <summary>目标地址根本不是统一身份认证保护的页面 → 400。</summary>
    NotCasProtected,

    /// <summary>目标地址指向内网/保留地址，被建连闸门拦下 → 400。</summary>
    BlockedTarget,

    /// <summary>网络不通、上游 5xx、响应无法解析 → 502。</summary>
    Upstream
}

/// <summary>
/// 通用统一身份认证的结果。
/// </summary>
/// <param name="Success">是否成功。</param>
/// <param name="Message">面向调用方的中文说明。</param>
/// <param name="Failure">失败归类。</param>
/// <param name="AuthServerUrl">自动发现的认证服务器登录页（含 <c>service</c> 参数）。</param>
/// <param name="LandingUrl">登录后的落地页。</param>
/// <param name="LandingCookie">落地站点 host 的会话 cookie 串。</param>
/// <param name="Cookies">整条跳转链上的全部 cookie。</param>
internal sealed record UnifiedAuthOutcome(
    bool Success,
    string Message,
    UnifiedAuthFailure Failure,
    string? AuthServerUrl = null,
    string? LandingUrl = null,
    string? LandingCookie = null,
    IReadOnlyList<(string Host, string Name, string Value)>? Cookies = null)
{
    public static UnifiedAuthOutcome Fail(UnifiedAuthFailure failure, string message)
        => new(false, message, failure);
}

/// <summary>
/// 通用统一身份认证客户端的接缝。存在的理由与 <c>IXauatSsoClient</c> 相同：
/// 编排层的分支（成功、密码错、不是 CAS 页面、白名单拒绝）要能被独立驱动验证，
/// 而真实 HTTP 只能覆盖到"能连通"的那一条。
/// </summary>
internal interface IUnifiedSsoClient
{
    Task<UnifiedAuthOutcome> LoginAsync(UnifiedAuthCommand command, CancellationToken cancellationToken);
}
