namespace XAUAT.EduApi.Exceptions;

/// <summary>二次认证验证码错误或上游拒绝。</summary>
public class MfaFailedException(string message) : Exception(message);
