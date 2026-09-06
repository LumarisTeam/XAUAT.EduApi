using EduApi.Data.Models;
using Moq;
using XAUAT.EduApi.Caching;
using XAUAT.EduApi.Repos;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Tests.Services;

public class MapServiceTests
{
    private readonly Mock<IMapPoiRepository> _repositoryMock = new();
    private readonly Mock<ICacheService> _cacheServiceMock = new();
    private readonly MapService _service;

    public MapServiceTests()
    {
        _cacheServiceMock
            .Setup(x => x.RemoveAsync(It.IsAny<string>(), It.IsAny<CacheLevel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _service = new MapService(_repositoryMock.Object, _cacheServiceMock.Object);
    }

    [Fact]
    public async Task AddPoiAsync_ShouldUpsertPoiByName()
    {
        var poi = CreatePoi("图书馆");

        await _service.AddPoiAsync(poi);

        _repositoryMock.Verify(x => x.UpsertAsync(poi), Times.Once);
        Assert.True(poi.IsActive);
        Assert.NotEqual(default, poi.UpdatedAt);
    }

    [Fact]
    public async Task AddPoisBatchAsync_ShouldUseBatchUpsert()
    {
        var pois = new[] { CreatePoi("图书馆"), CreatePoi("教学楼") };

        await _service.AddPoisBatchAsync(pois);

        _repositoryMock.Verify(x => x.UpsertRangeAsync(It.IsAny<IEnumerable<MapPoiModel>>()), Times.Once);
        Assert.All(pois, poi => Assert.True(poi.IsActive));
    }

    private static MapPoiModel CreatePoi(string name) => new()
    {
        Name = name,
        Category = "教学",
        Latitude = 34.1m,
        Longitude = 108.9m
    };
}
