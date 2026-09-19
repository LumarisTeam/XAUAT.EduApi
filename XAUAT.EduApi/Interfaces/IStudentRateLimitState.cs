namespace XAUAT.EduApi.Interfaces;

public interface IStudentRateLimitState
{
    bool TryGetBlockedUntil(string identityKey, out DateTimeOffset blockedUntil);
    TimeSpan MarkRateLimited(string identityKey);
    void MarkSuccess(string identityKey);
}
