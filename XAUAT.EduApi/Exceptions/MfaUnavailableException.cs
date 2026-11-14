namespace XAUAT.EduApi.Exceptions;

/// <summary>Redis 不可用，交互式二次认证暂不可用。</summary>
public class MfaUnavailableException() : Exception("二次认证暂不可用，请稍后重试");
