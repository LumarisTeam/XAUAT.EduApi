namespace EduApi.Data.Models;

/// <summary>
/// 二次认证挑战信息。首次登录需要二次认证时随 20003 返回给调用方；
/// 调用方用 <see cref="SessionId"/> 调用 <c>POST /v1/login/mfa</c> 或
/// <c>GET /v1/login/mfa/status</c> 完成认证。
/// </summary>
public sealed class MfaChallenge
{
    /// <summary>不透明的一次性会话 ID；不包含账号、密码或验证码。</summary>
    public string SessionId { get; set; } = "";

    /// <summary>方式标识：password/otp/sms/wecom/today_campus/email/dingtalk/welink/wechat_qr/qq_qr/dingtalk_qr。</summary>
    public string Method { get; set; } = "auto";

    /// <summary>authserver 的 reAuthType 数值。</summary>
    public int ReAuthType { get; set; }

    /// <summary>脱敏后的接收目标，如手机号、邮箱或姓名。</summary>
    public string? MaskedTarget { get; set; }

    /// <summary>扫码登录的二维码 URL（微信 qrconnect 或二维码图片 URL）。</summary>
    public string? QrCodeUrl { get; set; }

    /// <summary>二维码图片的原始 base64（不含 data: 前缀）。</summary>
    public string? QrImageBase64 { get; set; }

    /// <summary>会话过期时间（Unix 秒）。</summary>
    public long ExpiresAt { get; set; }

    /// <summary>本地化提示。</summary>
    public string Message { get; set; } = "";

    /// <summary>上游页面的输入提示，如“请输入6位有效的安全令牌”。</summary>
    public string? Hint { get; set; }

    /// <summary>是否扫码方式；扫码请走状态轮询接口。</summary>
    public bool IsQr { get; set; }
}
