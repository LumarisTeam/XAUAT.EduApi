namespace XAUAT.EduApi.Controllers.V1;

public static class ApiCodes
{
    /// <summary>成功</summary>
    public const int Success = 0;

    /// <summary>参数缺失或格式错误</summary>
    public const int ParamError = 10001;

    /// <summary>认证失败（Cookie过期或无效）</summary>
    public const int AuthFailed = 20001;

    /// <summary>用户名或密码错误</summary>
    public const int WrongCredentials = 20002;

    /// <summary>需要二次认证（返回 200 + 挑战信息）</summary>
    public const int MfaRequired = 20003;

    /// <summary>统一身份认证密码已过期，需先改密</summary>
    public const int PasswordExpired = 20004;

    /// <summary>MFA 会话不存在、已过期或已消费</summary>
    public const int MfaSessionExpired = 20005;

    /// <summary>二次认证验证码错误或上游拒绝</summary>
    public const int MfaInvalidCode = 20006;

    /// <summary>当前应用不支持所选的二次认证方式</summary>
    public const int MfaMethodUnsupported = 20007;

    /// <summary>资源未找到</summary>
    public const int NotFound = 30001;

    /// <summary>请求频率限制</summary>
    public const int RateLimited = 40001;

    /// <summary>服务器内部错误</summary>
    public const int InternalError = 50001;

    /// <summary>上游服务访问失败</summary>
    public const int UpstreamError = 50002;

    /// <summary>数据获取失败</summary>
    public const int DataFetchFailed = 50003;

    /// <summary>二次认证暂不可用（Redis 不可用）</summary>
    public const int MfaUnavailable = 50004;
}
