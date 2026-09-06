using EduApi.Data.Models;
using XAUAT.EduApi.Caching;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Repos;

namespace XAUAT.EduApi.Services;

public interface IMapService
{
    Task<List<MapPoiModel>> GetAllPoisAsync();
    Task<List<MapPoiModel>> GetPoisByCategoryAsync(string category);
    Task<List<MapPoiModel>> GetPoisByCampusAsync(string campus);
    Task<MapPoiModel?> GetPoiByIdAsync(int id);
    Task<List<MapPoiModel>> SearchPoisAsync(string keyword);
    Task<List<string>> GetCategoriesAsync();
    Task<List<string>> GetCampusesAsync();
    Task AddPoiAsync(MapPoiModel poi);
    Task AddPoisBatchAsync(IEnumerable<MapPoiModel> pois);
    Task UpdatePoiAsync(MapPoiModel poi);
    Task<int> ClearAllPoisAsync();
}

public class MapService(IMapPoiRepository repository, ICacheService cacheService) : IMapService
{
    public async Task<List<MapPoiModel>> GetAllPoisAsync()
    {
        return await cacheService.GetOrCreateAsync(
            CacheKeys.MapPois(),
            async () => await repository.GetAllActiveAsync(),
            TimeSpan.FromHours(24));
    }

    public async Task<List<MapPoiModel>> GetPoisByCategoryAsync(string category)
    {
        return await cacheService.GetOrCreateAsync(
            CacheKeys.MapPoiCategory(category),
            async () => await repository.GetByCategoryAsync(category),
            TimeSpan.FromHours(24));
    }

    public async Task<List<MapPoiModel>> GetPoisByCampusAsync(string campus)
    {
        return await cacheService.GetOrCreateAsync(
            CacheKeys.MapPoiCampus(campus),
            async () => await repository.GetByCampusAsync(campus),
            TimeSpan.FromHours(24));
    }

    public async Task<MapPoiModel?> GetPoiByIdAsync(int id)
    {
        return await cacheService.GetOrCreateAsync(
            CacheKeys.MapPoiDetail(id),
            async () => await repository.GetByIdAsync(id),
            TimeSpan.FromHours(24));
    }

    public async Task<List<MapPoiModel>> SearchPoisAsync(string keyword)
    {
        keyword = keyword.ToLower().Trim();
        return await repository.SearchAsync(keyword);
    }

    public async Task<List<string>> GetCategoriesAsync()
    {
        return await cacheService.GetOrCreateAsync(
            CacheKeys.MapCategories(),
            async () => await repository.GetCategoriesAsync(),
            TimeSpan.FromHours(24));
    }

    public async Task<List<string>> GetCampusesAsync()
    {
        return await cacheService.GetOrCreateAsync(
            CacheKeys.MapCampuses(),
            async () => await repository.GetCampusesAsync(),
            TimeSpan.FromHours(24));
    }

    public async Task AddPoiAsync(MapPoiModel poi)
    {
        poi.IsActive = true;
        poi.CreatedAt = DateTime.UtcNow;
        poi.UpdatedAt = DateTime.UtcNow;

        await repository.UpsertAsync(poi);
        await InvalidateCacheAsync();
    }

    public async Task AddPoisBatchAsync(IEnumerable<MapPoiModel> pois)
    {
        var poiList = pois.ToList();
        foreach (var poi in poiList)
        {
            poi.IsActive = true;
            poi.UpdatedAt = DateTime.UtcNow;
        }

        await repository.UpsertRangeAsync(poiList);
        await InvalidateCacheAsync();
    }

    public async Task UpdatePoiAsync(MapPoiModel poi)
    {
        var existing = await repository.GetByIdAsync(poi.Id);
        if (existing == null)
        {
            throw new KeyNotFoundException($"POI with ID {poi.Id} not found");
        }

        poi.UpdatedAt = DateTime.UtcNow;
        await repository.UpdateAsync(poi);
        await InvalidateCacheAsync();
    }

    public async Task<int> ClearAllPoisAsync()
    {
        var count = await repository.RemoveAllAsync();
        await InvalidateCacheAsync();
        return count;
    }

    private async Task InvalidateCacheAsync()
    {
        await cacheService.RemoveAsync(CacheKeys.MapPois());
        await cacheService.RemoveAsync(CacheKeys.MapCategories());
        await cacheService.RemoveAsync(CacheKeys.MapCampuses());
    }
}
