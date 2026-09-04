using System.ComponentModel.DataAnnotations;
using EduApi.Data.Models;
using Microsoft.AspNetCore.Mvc;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Controllers.Old;

/// <summary>
/// 电费查询控制器
/// 提供电费余额、周用电明细和充值页面地址查询功能
/// </summary>
[ApiController]
[Route("[controller]")]
[Produces("application/json")]
public class ElectricityController(
    IElectricityService service,
    IElectricitySubscriptionService subscriptionService,
    ILogger<ElectricityController> logger) : ControllerBase
{
    /// <summary>
    /// 获取当前电费余额
    /// </summary>
    /// <param name="url">可选，电费页面地址；不传时优先使用服务内已缓存的地址</param>
    /// <returns>当前电费余额</returns>
    /// <response code="200">成功获取电费余额</response>
    /// <response code="404">未找到电费余额数据</response>
    /// <response code="500">服务器内部错误</response>
    /// <remarks>
    /// 示例请求：
    /// GET /Electricity
    ///
    /// 或指定电费页面地址：
    /// GET /Electricity?url=https://xxx/wxAccount/...
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(double), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<double>> GetCurrentBalance([FromQuery] string? url = null)
    {
        try
        {
            logger.LogInformation("开始获取当前电费余额");
            var balance = await service.FetchCurrentBalanceAsync(url);

            if (!balance.HasValue)
            {
                return NotFound(new ErrorResponse { error = "未找到电费余额数据" });
            }

            return Ok(balance.Value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取当前电费余额时出错");
            return StatusCode(500, new ErrorResponse { error = "获取当前电费余额失败" });
        }
    }

    /// <summary>
    /// 获取按小时聚合的周用电明细
    /// </summary>
    /// <returns>电费明细列表</returns>
    /// <response code="200">成功获取电费明细</response>
    /// <response code="500">服务器内部错误</response>
    /// <remarks>
    /// 示例请求：
    /// GET /Electricity/WeeklyData
    /// </remarks>
    [HttpGet("WeeklyData")]
    [ProducesResponseType(typeof(List<ElectricData>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<List<ElectricData>>> GetWeeklyData([FromQuery] string? url = null)
    {
        try
        {
            logger.LogInformation("开始获取电费周明细");
            var data = await service.FetchWeeklyDataAsync(url);
            return Ok(data);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取电费周明细时出错");
            return StatusCode(500, new ErrorResponse { error = "获取电费周明细失败" });
        }
    }

    /// <summary>
    /// 获取电费充值页面地址
    /// </summary>
    /// <returns>充值页面地址</returns>
    /// <response code="200">成功获取充值地址</response>
    /// <response code="404">未找到充值地址</response>
    /// <response code="500">服务器内部错误</response>
    /// <remarks>
    /// 示例请求：
    /// GET /Electricity/RechargeUrl
    /// </remarks>
    [HttpGet("RechargeUrl")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<string>> GetRechargeUrl([FromQuery] string? url = null)
    {
        try
        {
            logger.LogInformation("开始获取电费充值地址");
            var urlData = await service.GetRechargeUrlAsync(url);

            if (string.IsNullOrWhiteSpace(url))
            {
                return NotFound(new ErrorResponse { error = "未找到电费充值地址" });
            }

            return Ok(urlData);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取电费充值地址时出错");
            return StatusCode(500, new ErrorResponse { error = "获取电费充值地址失败" });
        }
    }

    /// <summary>
    /// 创建或更新电费订阅
    /// </summary>
    /// <param name="request">订阅请求</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>订阅结果</returns>
    [HttpPost("Subscriptions")]
    [ProducesResponseType(typeof(ElectricitySubscriptionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ElectricitySubscriptionResponse>> UpsertSubscription(
        [FromBody] CreateElectricitySubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("开始创建或更新电费订阅，Email: {Email}", request.Email);
            var subscription = await subscriptionService.UpsertAsync(request, cancellationToken);
            return Ok(subscription);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "创建或更新电费订阅时出错，Email: {Email}", request.Email);
            return StatusCode(500, new ErrorResponse { error = "创建或更新电费订阅失败" });
        }
    }

    /// <summary>
    /// 根据邮箱查询电费订阅
    /// </summary>
    /// <param name="email">订阅邮箱</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>是否存在订阅及对应订阅 ID</returns>
    [HttpGet("Subscriptions")]
    [ProducesResponseType(typeof(ElectricitySubscriptionQueryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ElectricitySubscriptionQueryResponse>> QuerySubscriptionByEmail(
        [FromQuery] [Required(ErrorMessage = "邮箱不能为空")] [EmailAddress(ErrorMessage = "邮箱格式不正确")]
        string email,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("开始根据邮箱查询电费订阅，Email: {Email}", email);
            var result = await subscriptionService.QueryByEmailAsync(email, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "根据邮箱查询电费订阅时出错，Email: {Email}", email);
            return StatusCode(500, new ErrorResponse { error = "查询电费订阅失败" });
        }
    }

    /// <summary>
    /// 删除电费订阅
    /// </summary>
    /// <param name="id">订阅 ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>删除结果</returns>
    [HttpDelete("Subscriptions/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> DeleteSubscription(string id, CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await subscriptionService.DeleteAsync(id, cancellationToken);
            if (!deleted)
            {
                return NotFound(new ErrorResponse { error = "未找到对应的电费订阅" });
            }

            return NoContent();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "删除电费订阅时出错，SubscriptionId: {SubscriptionId}", id);
            return StatusCode(500, new ErrorResponse { error = "删除电费订阅失败" });
        }
    }
}