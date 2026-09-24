namespace XAUAT.EduApi.Exceptions;

/// <summary>
/// 连不上 XAUAT.LoginApi（连接被拒、超时）时抛出，语义是"登录服务的运维数据暂时读不到"。
/// </summary>
/// <remarks>
/// 与"上游返回了非 2xx"刻意区分开：后者表达的是登录服务自己内部出错，属于本服务的
/// 内部错误（控制器回 500）；而连不上表达的是这份数据此刻不可用，回 503 更准确，
/// 也让管理端能把它和真正的 bug 区分开。与 <c>HttpPaymentService</c> 的取舍一致。
/// </remarks>
public class LoginOpsUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
