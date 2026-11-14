namespace EduApi.Data.Models;

/// <summary>登录请求里的 MFA 选项。</summary>
public sealed class MfaRequestOptions
{
    /// <summary>期望的二次认证方式；null/auto 使用 authserver 页面默认值。</summary>
    public string? Method { get; set; }

    /// <summary>是否信任此设备，对应 <c>skipTmpReAuth</c>；默认 true。</summary>
    public bool TrustDevice { get; set; } = true;

    public static MfaRequestOptions Default { get; } = new();
}
