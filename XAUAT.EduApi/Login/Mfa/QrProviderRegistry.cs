namespace XAUAT.EduApi.Login.Mfa;

internal sealed record QrProvider(
    string Name,
    string CombinedLoginType,
    string? QrHost,
    bool Enabled);

/// <summary>
/// 扫码提供方。type 8（微信）已实测；type 9/14 架构已留好，
/// 但未实测，先返回不支持，绝不跟随未知 host。
/// </summary>
internal static class QrProviderRegistry
{
    public static QrProvider? For(MfaMethod method) => method switch
    {
        MfaMethod.WeChatQr => new QrProvider("weixin", "weixin", "open.weixin.qq.com", true),
        MfaMethod.QqQr => new QrProvider("qq", "qq", null, false),
        MfaMethod.DingTalkQr => new QrProvider("dingtalk", "dingtalk", null, false),
        _ => null
    };
}
