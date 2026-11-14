namespace XAUAT.EduApi.Login.Mfa;

/// <summary>
/// 从一次认证结果里带出来的 MFA 种子。只存在于内存/请求上下文，
/// 不落 Redis；需要跨请求时由 <see cref="MfaPendingSession"/> 承载。
/// </summary>
internal sealed class MfaSeed
{
    public MfaFlowKind Kind { get; init; }
    public string Username { get; init; } = "";
    public string AuthServerLoginUrl { get; init; } = "";
    public string Service { get; init; } = "";
    public int ReAuthType { get; init; }
    public string Method { get; init; } = "";
    public string? EncryptSalt { get; init; }
    public bool IsMultifactor { get; init; }
    public bool IsSleepAccount { get; init; }
    public string? MaskedTarget { get; init; }
    public string? Hint { get; init; }
    public List<MfaCookie> Cookies { get; init; } = [];
    public string? EntryUrl { get; init; }
    public string? SessionCacheKey { get; init; }
    public string? TicketCacheKey { get; init; }
    public string? PasswordHash { get; set; }
    public string? QrUuid { get; init; }
    public string? QrState { get; init; }
    public string? QrProvider { get; init; }
    public string? QrCodeUrl { get; init; }
    public string? QrImageBase64 { get; init; }

    public MfaPendingSession ToPendingSession() => new()
    {
        Kind = Kind == MfaFlowKind.Unified ? "unified" : "standard",
        Username = Username,
        AuthServerLoginUrl = AuthServerLoginUrl,
        Service = Service,
        EntryUrl = EntryUrl,
        ReAuthType = ReAuthType,
        Method = Method,
        EncryptSalt = EncryptSalt,
        IsMultifactor = IsMultifactor,
        IsSleepAccount = IsSleepAccount,
        MaskedTarget = MaskedTarget,
        Hint = Hint,
        Cookies = Cookies,
        PasswordHash = PasswordHash,
        SessionCacheKey = SessionCacheKey,
        TicketCacheKey = TicketCacheKey,
        QrUuid = QrUuid,
        QrState = QrState,
        QrProvider = QrProvider,
        QrCodeUrl = QrCodeUrl,
        QrImageBase64 = QrImageBase64
    };
}
