namespace XAUAT.EduApi.Login.Mfa;

/// <summary>
/// 访问微信扫码页/二维码/长轮询的独立命名客户端。
/// 只用于硬编码的微信域名，不接受调用方传入的 URL，也不携带 authserver cookie。
/// </summary>
internal static class MfaQrClient
{
    public const string Name = "MfaQrClient";
}
