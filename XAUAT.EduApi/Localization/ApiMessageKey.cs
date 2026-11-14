namespace XAUAT.EduApi.Localization;

public static class ApiMessageKey
{
    public const string UsernameOrPasswordRequired = nameof(UsernameOrPasswordRequired);
    public const string InvalidUsernameOrPassword = nameof(InvalidUsernameOrPassword);
    public const string EduSystemAccessFailed = nameof(EduSystemAccessFailed);
    public const string AuthenticationFailed = nameof(AuthenticationFailed);
    public const string InternalServerError = nameof(InternalServerError);
    public const string ExamFetchFailed = nameof(ExamFetchFailed);
    public const string ProgramFetchFailed = nameof(ProgramFetchFailed);
    public const string CompletionFetchFailed = nameof(CompletionFetchFailed);
    public const string BusFetchFailed = nameof(BusFetchFailed);
    public const string BusOldPlatformSuffix = nameof(BusOldPlatformSuffix);
    public const string ServiceUnavailable = nameof(ServiceUnavailable);
    public const string EduSystemRateLimited = nameof(EduSystemRateLimited);
    public const string AccountBanned = nameof(AccountBanned);
    public const string PaymentLoginUnknownError = nameof(PaymentLoginUnknownError);
    public const string PaymentTurnoverUnknownError = nameof(PaymentTurnoverUnknownError);
    public const string PaymentQrCodeUnknownError = nameof(PaymentQrCodeUnknownError);
    public const string LoginOpsTokenRequired = nameof(LoginOpsTokenRequired);
    public const string LoginOpsUsernameRequired = nameof(LoginOpsUsernameRequired);

    // 系统类型头（x-loginMode）
    public const string InvalidLoginMode = nameof(InvalidLoginMode);

    // 通用统一身份认证（/v1/login/unified）
    public const string UnifiedAuthParamRequired = nameof(UnifiedAuthParamRequired);
    public const string UnifiedAuthInvalidTarget = nameof(UnifiedAuthInvalidTarget);
    public const string UnifiedAuthHostNotAllowed = nameof(UnifiedAuthHostNotAllowed);
    public const string UnifiedAuthNotCasProtected = nameof(UnifiedAuthNotCasProtected);
    public const string UnifiedAuthBlockedTarget = nameof(UnifiedAuthBlockedTarget);

    // 二次认证（MFA / re-auth）
    public const string MfaRequired = nameof(MfaRequired);
    public const string MfaCodeRequired = nameof(MfaCodeRequired);
    public const string MfaInvalidCode = nameof(MfaInvalidCode);
    public const string MfaSessionExpired = nameof(MfaSessionExpired);
    public const string MfaMethodUnsupported = nameof(MfaMethodUnsupported);
    public const string MfaUnavailable = nameof(MfaUnavailable);
    public const string MfaAttemptsExceeded = nameof(MfaAttemptsExceeded);
    public const string MfaBusy = nameof(MfaBusy);
    public const string MfaQrPending = nameof(MfaQrPending);
    public const string MfaQrScanned = nameof(MfaQrScanned);
    public const string MfaQrExpired = nameof(MfaQrExpired);
    public const string MfaUseStatusEndpoint = nameof(MfaUseStatusEndpoint);
    public const string MfaSendFailed = nameof(MfaSendFailed);
    public const string MfaProtocolError = nameof(MfaProtocolError);
    public const string PasswordExpired = nameof(PasswordExpired);
}
