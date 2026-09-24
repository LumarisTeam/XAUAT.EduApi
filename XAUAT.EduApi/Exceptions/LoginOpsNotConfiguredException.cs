namespace XAUAT.EduApi.Exceptions;

/// <summary>
/// 请求的数据只有 XAUAT.LoginApi 提供，但本部署没有配置 <c>LOGIN_API_BASE_URL</c>
/// （即仍回落到 Flask）时抛出。
/// </summary>
/// <remarks>
/// <b>为什么不能在启动时直接失败</b>：与 <c>PAYMENT_API_BASE_URL</c> 不同，回落到 Flask 是
/// 登录链路刻意保留的灰度能力（见 <c>ServiceConfiguration.ResolvedLoginApiBaseUrl</c>），
/// 启动即失败会把"没配登录地址也能正常登录"这条退路一起砍掉。
/// 但封禁日志的 JSON 接口 <b>只有</b> LoginApi 有——Flask 那边只有一个 HTML 页
/// （<c>/admin/ban_logs</c>），打过去只会 404。所以在**这一条路径上**显式守住，
/// 回一句可操作的说明，而不是让 404 冒泡成一句含糊的 500。
/// </remarks>
public class LoginOpsNotConfiguredException(string message) : Exception(message);
