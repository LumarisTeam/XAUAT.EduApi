namespace EduApi.Data.Models;

/// <summary>
/// 校园卡支付码内容。
/// </summary>
public class PaymentQrCodeResult
{
    public const string OfflineMode = "offline";
    public const string OnlineMode = "online";

    /// <summary>
    /// 二维码原始字节的 Base64 编码。离线码是二进制载荷，在线码是 20 位条码。
    /// </summary>
    public string Payload { get; set; } = "";

    /// <summary>
    /// <c>offline</c> 或 <c>online</c>。
    /// </summary>
    public string Mode { get; set; } = OfflineMode;
}
