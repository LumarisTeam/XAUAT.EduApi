namespace EduApi.Data.Models;

/// <summary>
/// 通用统一身份认证登录结果。
/// <para>
/// <see cref="Cookie"/> 是**落地站点**那一份会话 cookie（按落地 host 及其父域过滤），
/// 直接塞进后续抓取请求的 <c>Cookie</c> 头即可；
/// <see cref="Cookies"/> 则是整条跳转链上的全部 cookie（含认证服务器的 CASTGC 票据），
/// 供需要自己轮换／复用票据的场景使用。
/// </para>
/// </summary>
public class UnifiedAuthResponse
{
    /// <summary>登录使用学号（原样回显）。</summary>
    public string Username { get; set; } = "";

    /// <summary>学号。通用接口无法反解目标系统的用户 ID，这里与 <see cref="Username"/> 同值。</summary>
    public string StudentId { get; set; } = "";

    /// <summary>请求里传入的目标地址（原样回显，便于调用方区分并发请求）。</summary>
    public string Url { get; set; } = "";

    /// <summary>自动发现的认证服务器登录页地址（含 <c>service</c> 参数）。</summary>
    public string AuthServerUrl { get; set; } = "";

    /// <summary>登录成功后的落地页地址。</summary>
    public string LandingUrl { get; set; } = "";

    /// <summary>落地站点的会话 cookie，形如 <c>k=v; k=v</c>。</summary>
    public string Cookie { get; set; } = "";

    /// <summary>整条跳转链上的全部 cookie（按写入顺序，跨 host）。</summary>
    public List<UnifiedAuthCookie> Cookies { get; set; } = [];
}

/// <summary>单个 cookie 及其归属 host。</summary>
public class UnifiedAuthCookie
{
    /// <summary>种下该 cookie 的 host（带点前缀表示域 cookie，如 <c>.xauat.edu.cn</c>）。</summary>
    public string Host { get; set; } = "";

    public string Name { get; set; } = "";

    public string Value { get; set; } = "";
}
