using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog.Events;

namespace XAUAT.EduApi.Logging;

public sealed class InMemoryLogStore : ILogStore
{
    private readonly int _capacity;
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public InMemoryLogStore(int capacity = 2000)
    {
        _capacity = capacity;
        LoadExistingFiles();
    }

    public void Add(LogEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > _capacity && _entries.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<LogEntry> Query(int page, int pageSize, LogEventLevel? minimumLevel = null, string? search = null)
    {
        return Filter(minimumLevel, search)
            .OrderByDescending(x => x.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArray();
    }

    public int Count(LogEventLevel? minimumLevel = null, string? search = null) => Filter(minimumLevel, search).Count();

    private IEnumerable<LogEntry> Filter(LogEventLevel? minimumLevel, string? search)
    {
        var query = _entries.AsEnumerable();
        if (minimumLevel is not null)
        {
            query = query.Where(x => Enum.TryParse<LogEventLevel>(x.Level, true, out var level) && level >= minimumLevel.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(x => x.Message.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                     (x.Source?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                     (x.Exception?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return query;
    }

    private void LoadExistingFiles()
    {
        var directories = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "logs"),
            Path.Combine(Directory.GetCurrentDirectory(), "XAUAT.EduApi", "logs")
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var file in directories.SelectMany(x => Directory.EnumerateFiles(x, "log-*.txt"))
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x))
        {
            foreach (var line in File.ReadLines(file))
            {
                var match = Regex.Match(line, "^\\[(?<time>[^ ]+) (?<level>TRC|DBG|INF|WRN|ERR|FTL)\\] (?<message>.*)$");
                if (!match.Success || !DateTimeOffset.TryParse(match.Groups["time"].Value, out var timestamp)) continue;

                var message = match.Groups["message"].Value;
                string? source = null;
                var propertiesStart = message.LastIndexOf(" {", StringComparison.Ordinal);
                if (propertiesStart >= 0)
                {
                    var properties = message[(propertiesStart + 1)..];
                    try
                    {
                        using var json = JsonDocument.Parse(properties);
                        if (json.RootElement.TryGetProperty("SourceContext", out var sourceProperty))
                            source = sourceProperty.GetString();
                        message = message[..propertiesStart];
                    }
                    catch (JsonException) { }
                }

                Add(new LogEntry(timestamp, match.Groups["level"].Value switch
                {
                    "TRC" => "Verbose", "DBG" => "Debug", "INF" => "Information",
                    "WRN" => "Warning", "ERR" => "Error", "FTL" => "Fatal", _ => "Information"
                }, message, source, null));
            }
        }
    }
}
