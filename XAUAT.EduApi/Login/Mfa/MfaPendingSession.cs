namespace XAUAT.EduApi.Login.Mfa;

/// <summary>MFA 会话所属的登录链路。</summary>
internal enum MfaFlowKind
{
    Standard,
    Unified
}

/// <summary>待认证会话里保存的单个 cookie（按 host 归属）。</summary>
internal sealed class MfaCookie
{
    public string Host { get; set; } = "";
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>
/// 等待二次认证的会话，存 Redis（<c>security:mfa:{id}</c>），TTL 5 分钟。
/// <para>
/// <b>绝不存明文密码、OTP、短信码、企业微信码或二维码扫码 code。</b>
/// <see cref="PasswordHash"/> 是 SHA-256，仅用于复刻既有
/// <c>login-cookies-*</c> / <c>sso-cookies-*</c> 缓存键。
/// </para>
/// </summary>
internal sealed class MfaPendingSession
{
    public string SessionId { get; set; } = "";
    public string Kind { get; set; } = "standard";
    public string Username { get; set; } = "";
    public string AuthServerLoginUrl { get; set; } = "";
    public string Service { get; set; } = "";
    public string? EntryUrl { get; set; }
    public int ReAuthType { get; set; }
    public string Method { get; set; } = "";
    public string? EncryptSalt { get; set; }
    public bool IsMultifactor { get; set; }
    public bool IsSleepAccount { get; set; }
    public bool TrustDevice { get; set; } = true;
    public string? MaskedTarget { get; set; }
    public string? Hint { get; set; }
    public List<MfaCookie> Cookies { get; set; } = [];
    public string? PasswordHash { get; set; }
    public string? SessionCacheKey { get; set; }
    public string? TicketCacheKey { get; set; }
    public string? QrUuid { get; set; }
    public string? QrState { get; set; }
    public string? QrProvider { get; set; }
    public string? QrCodeUrl { get; set; }
    public string? QrImageBase64 { get; set; }
    public string QrStatus { get; set; } = "pending";
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public long CreatedAt { get; set; }
    public long ExpiresAt { get; set; }
}
