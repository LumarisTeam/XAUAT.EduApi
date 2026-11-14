using EduApi.Data.Models;

namespace XAUAT.EduApi.Login.Mfa;

/// <summary>二次认证第二步的公开接缝（控制器依赖此接口，实现保持 internal）。</summary>
public interface IMfaLoginService
{
    Task<object> CompleteAsync(MfaSubmitRequest request, CancellationToken cancellationToken);

    Task<MfaStatusResponse> PollQrAsync(string sessionId, CancellationToken cancellationToken);
}
