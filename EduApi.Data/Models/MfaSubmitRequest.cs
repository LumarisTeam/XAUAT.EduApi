namespace EduApi.Data.Models;

/// <summary>二次认证第二步请求。</summary>
public sealed class MfaSubmitRequest
{
    /// <summary>首次登录返回的 MFA 会话 ID。</summary>
    public string MfaSessionId { get; set; } = "";

    /// <summary>OTP 或短信/企业微信/邮箱等动态验证码；扫码方式不需要。</summary>
    public string? Code { get; set; }

    /// <summary>是否信任此设备；null 时沿用会话创建时的值（默认 true）。</summary>
    public bool? TrustDevice { get; set; }
}
