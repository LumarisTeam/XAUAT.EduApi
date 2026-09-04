using Serilog.Core;
using Serilog.Events;

namespace XAUAT.EduApi.Logging;

public sealed class InMemoryLogSink(ILogStore store) : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        var source = logEvent.Properties.TryGetValue("SourceContext", out var value)
            ? value.ToString().Trim('"')
            : null;

        store.Add(new LogEntry(
            logEvent.Timestamp,
            logEvent.Level.ToString(),
            logEvent.RenderMessage(),
            source,
            logEvent.Exception?.ToString()));
    }
}
