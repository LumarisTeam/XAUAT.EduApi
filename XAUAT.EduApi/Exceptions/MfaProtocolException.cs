namespace XAUAT.EduApi.Exceptions;

/// <summary>二次认证页面/上游协议解析失败（应按上游错误 502 处理，不能伪装成密码错误）。</summary>
public class MfaProtocolException(string message) : Exception(message);
