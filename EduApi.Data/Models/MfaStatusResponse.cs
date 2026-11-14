namespace EduApi.Data.Models;

/// <summary>扫码二次认证的状态查询响应。</summary>
public sealed class MfaStatusResponse
{
    public string SessionId { get; set; } = "";

    /// <summary>pending / scanned / success / failed / expired / locked。</summary>
    public string Status { get; set; } = "";

    public string? Message { get; set; }

    /// <summary>刷新后的挑战信息（二维码/剩余时间）。</summary>
    public MfaChallenge? Mfa { get; set; }

    /// <summary>标准登录完成后的结果。</summary>
    public LoginResponse? Login { get; set; }

    /// <summary>通用统一认证完成后的结果。</summary>
    public UnifiedAuthResponse? Unified { get; set; }
}
