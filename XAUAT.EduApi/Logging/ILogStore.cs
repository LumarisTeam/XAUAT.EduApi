using Serilog.Events;

namespace XAUAT.EduApi.Logging;

public interface ILogStore
{
    void Add(LogEntry entry);

    IReadOnlyList<LogEntry> Query(
        int page,
        int pageSize,
        LogEventLevel? minimumLevel = null,
        string? search = null);

    int Count(LogEventLevel? minimumLevel = null, string? search = null);
}
