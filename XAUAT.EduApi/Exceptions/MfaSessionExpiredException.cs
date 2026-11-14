namespace XAUAT.EduApi.Exceptions;

/// <summary>MFA 会话不存在、已过期或已被消费。</summary>
public class MfaSessionExpiredException() : Exception("二次认证会话已过期，请重新登录");
