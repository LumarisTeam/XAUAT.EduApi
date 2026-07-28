using EduApi.Data.Models;
using Microsoft.AspNetCore.Mvc;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Controllers;

/// <summary>
/// 课程时间表控制器
/// 提供课程时间段查询功能，后端不做校区判断，直接返回两个校区的时间数据供前端缓存使用
/// </summary>
[ApiController]
[Route("[controller]")]
[Produces("application/json")]
public class ScheduleTimeController(
    IScheduleTimeService scheduleTimeService,
    ILogger<ScheduleTimeController> logger) : ControllerBase
{
    /// <summary>
    /// 获取所有校区和季节的课程时间表
    /// </summary>
    /// <returns>所有时间表数据</returns>
    /// <response code="200">成功获取时间表数据</response>
    /// <remarks>
    /// 示例请求：
    /// GET /ScheduleTime
    /// 
    /// 返回数据包含：
    /// - 草堂校区：通用时间表（不分季节），第0节为早自习（8:00-8:20）
    /// - 雁塔校区：冬季时间表（10月15日-次年5月1日）
    /// - 雁塔校区：夏季时间表（5月1日-10月15日）
    /// 
    /// 后端不做校区判断，前端根据课程的校区字段自行选择使用哪个时间表
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(ScheduleTimeResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ScheduleTimeResponse>> GetAllScheduleTables()
    {
        try
        {
            logger.LogInformation("开始获取所有校区和季节的课程时间表");
            var response = await scheduleTimeService.GetScheduleTimeResponseAsync();
            return Ok(response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取课程时间表时发生错误");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>
    /// 获取指定校区和季节的课程时间表
    /// </summary>
    /// <param name="campus">校区（CanTang=草堂，YanTa=雁塔）</param>
    /// <param name="season">季节（Winter=冬季，Summer=夏季）</param>
    /// <returns>对应校区和季节的时间表</returns>
    /// <response code="200">成功获取时间表</response>
    /// <response code="400">参数错误</response>
    /// <response code="404">未找到对应的时间表</response>
    /// <remarks>
    /// 示例请求：
    /// GET /ScheduleTime/GetByCampusAndSeason?campus=CanTang&season=Summer
    /// GET /ScheduleTime/GetByCampusAndSeason?campus=YanTa&season=Winter
    /// </remarks>
    [HttpGet("GetByCampusAndSeason")]
    [ProducesResponseType(typeof(ScheduleTimeTable), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ScheduleTimeTable>> GetByCampusAndSeason(Campus campus, Season season)
    {
        try
        {
            logger.LogInformation("获取校区 {Campus} 季节 {Season} 的课程时间表", campus, season);
            var table = await scheduleTimeService.GetScheduleTableAsync(campus, season);
            
            if (table == null)
            {
                return NotFound(new { success = false, message = "未找到对应的时间表" });
            }
            
            return Ok(table);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取课程时间表时发生错误");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }

    /// <summary>
    /// 获取指定节次的具体时间
    /// </summary>
    /// <param name="campus">校区（CanTang=草堂，YanTa=雁塔）</param>
    /// <param name="season">季节（Winter=冬季，Summer=夏季）</param>
    /// <param name="unitIndex">节次索引（从0开始，第0节为早自习）</param>
    /// <returns>该节次的时间信息</returns>
    /// <response code="200">成功获取节次时间</response>
    /// <response code="400">参数错误</response>
    /// <response code="404">未找到对应的节次</response>
    /// <remarks>
    /// 示例请求：
    /// GET /ScheduleTime/GetPeriod?campus=CanTang&season=Summer&unitIndex=0
    /// GET /ScheduleTime/GetPeriod?campus=YanTa&season=Winter&unitIndex=1
    /// </remarks>
    [HttpGet("GetPeriod")]
    [ProducesResponseType(typeof(SchedulePeriod), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SchedulePeriod>> GetPeriod(Campus campus, Season season, int unitIndex)
    {
        try
        {
            if (unitIndex < 0)
            {
                return BadRequest(new { success = false, message = "节次索引必须大于等于0" });
            }
            
            logger.LogInformation("获取校区 {Campus} 季节 {Season} 第 {UnitIndex} 节的课程时间", campus, season, unitIndex);
            var period = await scheduleTimeService.GetPeriodAsync(campus, season, unitIndex);
            
            if (period == null)
            {
                return NotFound(new { success = false, message = "未找到对应的节次" });
            }
            
            return Ok(period);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取课程节次时间时发生错误");
            return StatusCode(500, new { success = false, message = "服务器内部错误" });
        }
    }
}