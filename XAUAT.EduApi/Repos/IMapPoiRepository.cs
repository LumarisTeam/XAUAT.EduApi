using EduApi.Data.Models;

namespace XAUAT.EduApi.Repos;

public interface IMapPoiRepository
{
    Task<List<MapPoiModel>> GetAllActiveAsync();
    Task<List<MapPoiModel>> GetByCategoryAsync(string category);
    Task<List<MapPoiModel>> GetByCampusAsync(string campus);
    Task<MapPoiModel?> GetByIdAsync(int id);
    Task<List<MapPoiModel>> SearchAsync(string keyword);
    Task<List<string>> GetCategoriesAsync();
    Task<List<string>> GetCampusesAsync();
    Task UpsertAsync(MapPoiModel poi);
    Task UpsertRangeAsync(IEnumerable<MapPoiModel> pois);
    Task UpdateAsync(MapPoiModel poi);
    Task<int> RemoveAllAsync();
}
