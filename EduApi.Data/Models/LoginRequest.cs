namespace EduApi.Data.Models;

public class LoginRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>期望的二次认证方式；null/auto 使用 authserver 页面默认值。</summary>
    public string? MfaMethod { get; set; }

    /// <summary>是否信任此设备（skipTmpReAuth）；默认 true。</summary>
    public bool TrustDevice { get; set; } = true;
}