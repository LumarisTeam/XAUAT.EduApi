using EduApi.Data.Models;
using Microsoft.AspNetCore.Mvc;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Controllers.Old;

/// <summary>
/// 校车时刻表控制器
/// 提供校车运行时刻表查询功能，支持从新旧两个数据平台获取数据
/// </summary>
[ApiController]
[Route("[controller]")]
[Produces("application/json")]
public class BusController(
    IBusService busService,
    ILogger<BusController> logger,
    ILanguageResolver languageResolver,
    IApiMessageLocalizer messageLocalizer) : LanguageAwareControllerBase(languageResolver, messageLocalizer)
{
    /// <summary>
    /// 获取校车时刻表
    /// 从旧数据平台获取指定日期的校车运行时刻表
    /// </summary>
    /// <param name="time">可选，查询日期，格式为YYYY-MM-DD，默认为当天</param>
    /// <returns>校车时刻表数据，包含所有线路的发车时间、站点等信息</returns>
    /// <response code="200">成功获取校车时刻表</response>
    /// <response code="500">服务器内部错误，无法获取校车数据</response>
    /// <remarks>
    /// 示例请求：
    /// GET /Bus
    /// Header:
    /// x-language: zh-CN (also accepts legacy alias: zh)
    ///
    /// 获取指定日期的校车时刻表：
    /// GET /Bus/2024-12-01
    /// </remarks>
    [HttpGet("{time?}")]
    [ProducesResponseType(typeof(BusModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<BusModel>> GetBus(string? time)
    {
        try
        {
            return Ok(await busService.GetBusFromOldDataAsync(time, Language));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取校车时刻表失败");
            return StatusCode(500, Message(ApiMessageKey.BusFetchFailed));
        }
    }

    /// <summary>
    /// 从新数据平台获取校车时刻表
    /// 从新的校车数据平台获取指定日期和校区的校车运行时刻表
    /// </summary>
    /// <param name="time">可选，查询日期，格式为YYYY-MM-DD，默认为当天</param>
    /// <param name="loc">校区过滤条件，可选值：ALL（所有校区）、雁塔、草堂，默认为ALL</param>
    /// <returns>校车时刻表数据，包含所有线路的发车时间、站点等信息</returns>
    /// <response code="200">成功获取校车时刻表</response>
    /// <response code="500">服务器内部错误，无法获取校车数据</response>
    /// <remarks>
    /// 示例请求：
    /// GET /Bus/NewData
    /// Header:
    /// x-language: zh-CN (also accepts legacy alias: zh)
    ///
    /// 获取指定日期和校区的校车时刻表：
    /// GET /Bus/NewData/2024-12-01?loc=雁塔
    /// </remarks>
    [HttpGet("NewData/{time?}")]
    [ProducesResponseType(typeof(BusModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<BusModel>> GetBusFromNewData(string? time, string loc = "ALL")
    {
        try
        {
            return Ok(await busService.GetBusFromNewDataAsync(time, loc, Language));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取新平台校车时刻表失败");
            return StatusCode(500, Message(ApiMessageKey.BusFetchFailed));
        }
    }

    /// <summary>
    /// 从旧数据平台获取校车时刻表
    /// 从旧的校车数据平台获取指定日期的校车运行时刻表
    /// </summary>
    /// <param name="time">可选，查询日期，格式为YYYY-MM-DD，默认为当天</param>
    /// <param name="isShow">可选，是否显示数据来源标识，默认为false</param>
    /// <returns>校车时刻表数据，包含所有线路的发车时间、站点等信息</returns>
    [HttpGet("OldData/{time?}")]
    [ProducesResponseType(typeof(BusModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<BusModel>> GetBusFromOldData(string? time, bool isShow = false)
    {
        try
        {
            return Ok(await busService.GetBusFromOldDataAsync(time, Language, isShow));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取旧平台校车时刻表失败");
            return StatusCode(500, Message(ApiMessageKey.BusFetchFailed));
        }
    }
}
