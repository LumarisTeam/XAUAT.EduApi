using EduApi.Data;
using EduApi.Data.Models;
using Microsoft.EntityFrameworkCore;
using XAUAT.EduApi.Repos;

namespace XAUAT.EduApi.Tests.Repos;

public class MapPoiRepositoryTests
{
    [Fact]
    public async Task UpsertAsync_ShouldReplaceExistingPoi_WhenNameMatches()
    {
        var factory = CreateContextFactory();
        var createdAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await using (var context = await factory.CreateDbContextAsync())
        {
            context.MapPois.Add(new MapPoiModel
            {
                Name = "图书馆",
                Category = "旧分类",
                Latitude = 34.1m,
                Longitude = 108.9m,
                CreatedAt = createdAt
            });
            await context.SaveChangesAsync();
        }

        var imported = new MapPoiModel
        {
            Name = "图书馆",
            Category = "学习",
            Latitude = 34.2m,
            Longitude = 109.0m,
            Description = "新描述",
            UpdatedAt = DateTime.UtcNow
        };

        await new MapPoiRepository(factory).UpsertAsync(imported);

        await using var verificationContext = await factory.CreateDbContextAsync();
        var poi = await verificationContext.MapPois.SingleAsync();
        Assert.Equal("学习", poi.Category);
        Assert.Equal(34.2m, poi.Latitude);
        Assert.Equal("新描述", poi.Description);
        Assert.Equal(createdAt, poi.CreatedAt);
        Assert.Equal(poi.Id, imported.Id);
    }

    [Fact]
    public async Task UpsertRangeAsync_ShouldKeepOnlyLastEntry_ForDuplicateNamesInImport()
    {
        var factory = CreateContextFactory();
        var pois = new[]
        {
            CreatePoi("教学楼", "旧数据"),
            CreatePoi("教学楼", "新数据")
        };

        await new MapPoiRepository(factory).UpsertRangeAsync(pois);

        await using var context = await factory.CreateDbContextAsync();
        var poi = await context.MapPois.SingleAsync();
        Assert.Equal("新数据", poi.Description);
    }

    private static IDbContextFactory<EduContext> CreateContextFactory()
    {
        var options = new DbContextOptionsBuilder<EduContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestDbContextFactory(options);
    }

    private static MapPoiModel CreatePoi(string name, string description) => new()
    {
        Name = name,
        Category = "教学",
        Latitude = 34.1m,
        Longitude = 108.9m,
        Description = description,
        UpdatedAt = DateTime.UtcNow
    };

    private sealed class TestDbContextFactory(DbContextOptions<EduContext> options) : IDbContextFactory<EduContext>
    {
        public EduContext CreateDbContext() => new(options);

        public Task<EduContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
