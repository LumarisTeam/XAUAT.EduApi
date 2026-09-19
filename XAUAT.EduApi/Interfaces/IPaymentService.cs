using EduApi.Data.Models;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Interfaces;

/// <summary>
/// 校园卡支付数据访问。
/// <para>
/// 支付逻辑本身已迁出到独立的 XAUAT.PaymentAPI，EduApi 不再内置任何实现——
/// 唯一实现是 <see cref="HttpPaymentService"/>。本接口保留是为了让控制器与
/// 代理实现解耦，便于测试中 mock。
/// </para>
/// </summary>
public interface IPaymentService
{
    Task<string> Login(string cardNum, string password = "202411", string language = "zh");
    Task<PaymentData> GetTurnoverAsync(string cardNum, string password = "202411", string language = "zh");
}
