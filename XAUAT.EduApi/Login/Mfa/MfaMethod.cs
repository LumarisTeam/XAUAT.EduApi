namespace XAUAT.EduApi.Login.Mfa;

/// <summary>二次认证方式。数值与 authserver 的 reAuthType 对齐。</summary>
internal enum MfaMethod
{
    Auto = 0,
    Password = 2,
    Sms = 3,
    WeCom = 4,
    TodayCampus = 5,
    WeChatQr = 8,
    QqQr = 9,
    Otp = 10,
    Email = 11,
    DingTalk = 12,
    WeLink = 13,
    DingTalkQr = 14
}

internal static class MfaMethods
{
    /// <summary>解析 API 传入的 method；空/null 视为 auto。</summary>
    public static bool TryParse(string? raw, out MfaMethod method)
    {
        method = MfaMethod.Auto;
        if (string.IsNullOrWhiteSpace(raw)) return true;

        var value = raw.Trim().ToLowerInvariant().Replace('-', '_');
        method = value switch
        {
            "auto" => MfaMethod.Auto,
            "password" or "pwd" or "2" => MfaMethod.Password,
            "sms" or "3" => MfaMethod.Sms,
            "wecom" or "wechat_work" or "qy" or "4" => MfaMethod.WeCom,
            "today_campus" or "cpdaily" or "5" => MfaMethod.TodayCampus,
            "wechat_qr" or "wechat" or "weixin" or "wx" or "8" => MfaMethod.WeChatQr,
            "qq_qr" or "qq" or "9" => MfaMethod.QqQr,
            "otp" or "token" or "10" => MfaMethod.Otp,
            "email" or "11" => MfaMethod.Email,
            "dingtalk" or "12" => MfaMethod.DingTalk,
            "welink" or "13" => MfaMethod.WeLink,
            "dingtalk_qr" or "qr14" or "14" => MfaMethod.DingTalkQr,
            _ => (MfaMethod)(-1)
        };

        return (int)method >= 0;
    }

    public static MfaMethod FromReAuthType(int reAuthType) => reAuthType switch
    {
        2 => MfaMethod.Password,
        3 => MfaMethod.Sms,
        4 => MfaMethod.WeCom,
        5 => MfaMethod.TodayCampus,
        8 => MfaMethod.WeChatQr,
        9 => MfaMethod.QqQr,
        10 => MfaMethod.Otp,
        11 => MfaMethod.Email,
        12 => MfaMethod.DingTalk,
        13 => MfaMethod.WeLink,
        14 => MfaMethod.DingTalkQr,
        _ => MfaMethod.Auto
    };

    public static string ToApiName(MfaMethod method) => method switch
    {
        MfaMethod.Password => "password",
        MfaMethod.Sms => "sms",
        MfaMethod.WeCom => "wecom",
        MfaMethod.TodayCampus => "today_campus",
        MfaMethod.WeChatQr => "wechat_qr",
        MfaMethod.QqQr => "qq_qr",
        MfaMethod.Otp => "otp",
        MfaMethod.Email => "email",
        MfaMethod.DingTalk => "dingtalk",
        MfaMethod.WeLink => "welink",
        MfaMethod.DingTalkQr => "dingtalk_qr",
        _ => "auto"
    };

    /// <summary>动态码方式的发送接口 authCodeTypeName。</summary>
    public static string? GetAuthCodeTypeName(MfaMethod method) => method switch
    {
        MfaMethod.Sms => "reAuthDynamicCodeType",
        MfaMethod.WeCom => "reAuthWChatDynamicCodeType",
        MfaMethod.TodayCampus => "reAuthCpdailyDynamicCodeType",
        MfaMethod.Email => "reAuthEmailDynamicCodeType",
        MfaMethod.DingTalk => "reAuthDingTalkDynamicCodeType",
        MfaMethod.WeLink => "reAuthWeLinkDynamicCodeType",
        _ => null
    };

    public static bool IsDynamicCode(MfaMethod method)
        => GetAuthCodeTypeName(method) is not null;

    public static bool IsQr(MfaMethod method)
        => method is MfaMethod.WeChatQr or MfaMethod.QqQr or MfaMethod.DingTalkQr;
}
