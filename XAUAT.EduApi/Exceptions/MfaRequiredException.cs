using EduApi.Data.Models;

namespace XAUAT.EduApi.Exceptions;

/// <summary>
/// 需要二次认证。控制器捕获后返回 HTTP 200 + ApiCodes.MfaRequired，
/// 并把 <see cref="Challenge"/> 放进 data.mfa。
/// </summary>
public class MfaRequiredException(MfaChallenge challenge) : Exception(challenge.Message)
{
    public MfaChallenge Challenge { get; } = challenge;
}
