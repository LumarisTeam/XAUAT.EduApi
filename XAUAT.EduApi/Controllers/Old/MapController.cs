using EduApi.Data.Models;
using Microsoft.AspNetCore.Mvc;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Controllers.Old;

/// <summary>
/// 校园地图POI控制器
/// 提供校园地理坐标查询接口，支持按分类、校区筛选和关键词搜索
/// </summary>
[ApiController]
[Route("[controller]")]
[Produces("application/json")]
public class MapController(
    IMapService mapService,
    IMapAdminTokenService mapAdminTokenService,
    ILogger<MapController> logger) : ControllerBase
{
    /// <summary>
    /// 获取所有POI点位
    /// 返回校园内所有可用的地理位置信息
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<MapPoiModel>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<MapPoiModel>>> GetAllPois()
    {
        try
        {
            var pois = await mapService.GetAllPoisAsync();
            return Ok(pois);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取POI列表失败");
            return StatusCode(500, "获取POI数据失败");
        }
    }

    /// <summary>
    /// 按分类获取POI点位
    /// </summary>
    /// <param name="category">分类名称</param>
    [HttpGet("category/{category}")]
    [ProducesResponseType(typeof(List<MapPoiModel>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<MapPoiModel>>> GetPoisByCategory(string category)
    {
        try
        {
            var pois = await mapService.GetPoisByCategoryAsync(category);
            if (pois.Count == 0)
            {
                return NotFound($"未找到分类为 '{category}' 的POI");
            }

            return Ok(pois);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取分类POI失败: {Category}", category);
            return StatusCode(500, $"获取分类 '{category}' 的POI数据失败");
        }
    }

    /// <summary>
    /// 按校区获取POI点位
    /// </summary>
    /// <param name="campus">校区名称</param>
    [HttpGet("campus/{campus}")]
    [ProducesResponseType(typeof(List<MapPoiModel>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<MapPoiModel>>> GetPoisByCampus(string campus)
    {
        try
        {
            var pois = await mapService.GetPoisByCampusAsync(campus);
            if (pois.Count == 0)
            {
                return NotFound($"未找到校区 '{campus}' 的POI");
            }

            return Ok(pois);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取校区POI失败: {Campus}", campus);
            return StatusCode(500, $"获取校区 '{campus}' 的POI数据失败");
        }
    }

    /// <summary>
    /// 根据ID获取单个POI详情
    /// </summary>
    /// <param name="id">POI ID</param>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(MapPoiModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MapPoiModel>> GetPoiById(int id)
    {
        try
        {
            var poi = await mapService.GetPoiByIdAsync(id);
            if (poi == null)
            {
                return NotFound($"未找到ID为 {id} 的POI");
            }

            return Ok(poi);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取POI详情失败: {Id}", id);
            return StatusCode(500, $"获取ID为 {id} 的POI详情失败");
        }
    }

    /// <summary>
    /// 搜索POI点位
    /// </summary>
    /// <param name="keyword">搜索关键词</param>
    [HttpGet("search")]
    [ProducesResponseType(typeof(List<MapPoiModel>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<MapPoiModel>>> SearchPois([FromQuery] string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return BadRequest("搜索关键词不能为空");
        }

        try
        {
            var pois = await mapService.SearchPoisAsync(keyword);
            return Ok(pois);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "搜索POI失败: {Keyword}", keyword);
            return StatusCode(500, "搜索POI数据失败");
        }
    }

    /// <summary>
    /// 获取所有POI分类
    /// </summary>
    [HttpGet("categories")]
    [ProducesResponseType(typeof(List<string>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<string>>> GetCategories()
    {
        try
        {
            var categories = await mapService.GetCategoriesAsync();
            return Ok(categories);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取地图分类失败");
            return StatusCode(500, "获取分类数据失败");
        }
    }

    /// <summary>
    /// 获取所有校区列表
    /// </summary>
    [HttpGet("campuses")]
    [ProducesResponseType(typeof(List<string>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<string>>> GetCampuses()
    {
        try
        {
            var campuses = await mapService.GetCampusesAsync();
            return Ok(campuses);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取校区列表失败");
            return StatusCode(500, "获取校区数据失败");
        }
    }

    /// <summary>
    /// 导入单个POI数据
    /// </summary>
    [HttpPost("import")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> ImportPoi([FromBody] MapPoiModel poi)
    {
        var unauthorized = EnsureMapAdminAuthorized();
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        if (string.IsNullOrWhiteSpace(poi.Name) || string.IsNullOrWhiteSpace(poi.Category))
        {
            return BadRequest("名称和分类不能为空");
        }

        if (poi.Latitude == 0 || poi.Longitude == 0)
        {
            return BadRequest("经纬度不能为0");
        }

        try
        {
            await mapService.AddPoiAsync(poi);

            logger.LogInformation("成功导入POI: {Name} ({Latitude}, {Longitude})", poi.Name, poi.Latitude, poi.Longitude);

            return Ok(new { success = true, message = $"成功导入: {poi.Name}", id = poi.Id });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "导入POI失败: {Name}", poi.Name);
            return StatusCode(500, $"导入失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 批量导入POI数据
    /// </summary>
    [HttpPost("import/batch")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<ActionResult> ImportPoisBatch([FromBody] List<MapPoiModel> pois)
    {
        var unauthorized = EnsureMapAdminAuthorized();
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        if (pois == null! || pois.Count == 0)
        {
            return BadRequest("POI列表不能为空");
        }

        var validPois = new List<MapPoiModel>();
        var errorCount = 0;
        var errors = new List<string>();

        foreach (var poi in pois)
        {
            if (string.IsNullOrWhiteSpace(poi.Name) || string.IsNullOrWhiteSpace(poi.Category))
            {
                errorCount++;
                errors.Add($"{poi.Name}: 名称或分类为空");
                continue;
            }

            if (poi.Latitude == 0 || poi.Longitude == 0)
            {
                errorCount++;
                errors.Add($"{poi.Name}: 经纬度无效");
                continue;
            }

            poi.IsActive = true;
            poi.CreatedAt = DateTime.UtcNow;
            poi.UpdatedAt = DateTime.UtcNow;
            validPois.Add(poi);
        }

        var successCount = validPois.Count;

        if (successCount > 0)
        {
            await mapService.AddPoisBatchAsync(validPois);
            logger.LogInformation("批量导入完成: 成功 {Success} 条, 失败 {Error} 条", successCount, errorCount);
        }

        return Ok(new
        {
            imported_count = successCount,
            error_count = errorCount,
            errors = errors.Take(10),
            message = $"导入完成: 成功 {successCount} 条, 失败 {errorCount} 条"
        });
    }

    /// <summary>
    /// 清空所有POI数据
    /// </summary>
    [HttpDelete("clear")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<ActionResult> ClearAllPois()
    {
        var unauthorized = EnsureMapAdminAuthorized();
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        try
        {
            var count = await mapService.ClearAllPoisAsync();

            logger.LogWarning("已清空所有POI数据，共 {Count} 条", count);

            return Ok(new { success = true, deleted_count = count, message = $"已清空 {count} 条POI数据" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "清空POI数据失败");
            return StatusCode(500, $"清空失败: {ex.Message}");
        }
    }

    private ActionResult? EnsureMapAdminAuthorized()
    {
        if (mapAdminTokenService.IsAuthorized(Request))
        {
            return null;
        }

        return Unauthorized("Map 管理操作需要有效的 Token");
    }
}
