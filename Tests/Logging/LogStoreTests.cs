using Serilog.Events;
using XAUAT.EduApi.Logging;

namespace XAUAT.EduApi.Tests.Logging;

public class LogStoreTests
{
    [Fact]
    public void Query_ShouldSortAndPageEntries()
    {
        var store = new InMemoryLogStore(3);
        store.Add(new LogEntry(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), "Information", "first", "A", null));
        store.Add(new LogEntry(DateTimeOffset.Parse("2026-01-01T00:00:01Z"), "Warning", "second", "B", null));
        store.Add(new LogEntry(DateTimeOffset.Parse("2026-01-01T00:00:02Z"), "Error", "third", "A", null));

        var page = store.Query(2, 1);

        Assert.Single(page);
        Assert.Equal("second", page[0].Message);
        Assert.Equal(3, store.Count());
    }

    [Fact]
    public void Add_ShouldEvictOldestEntryAndFilterByLevelAndSearch()
    {
        var store = new InMemoryLogStore(2);
        store.Add(new LogEntry(DateTimeOffset.UtcNow, "Information", "old", "A", null));
        store.Add(new LogEntry(DateTimeOffset.UtcNow, "Warning", "keep", "B", null));
        store.Add(new LogEntry(DateTimeOffset.UtcNow, "Error", "failure", "C", null));

        Assert.Equal(2, store.Count());
        Assert.Equal(1, store.Count(LogEventLevel.Error));
        Assert.Equal(1, store.Count(search: "fail"));
        Assert.DoesNotContain(store.Query(1, 10), x => x.Message == "old");
    }
}
