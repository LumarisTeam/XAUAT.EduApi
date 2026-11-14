namespace XAUAT.EduApi.Exceptions;

/// <summary>当前应用不支持所选的二次认证方式。</summary>
public class MfaMethodUnsupportedException(string message) : Exception(message);
