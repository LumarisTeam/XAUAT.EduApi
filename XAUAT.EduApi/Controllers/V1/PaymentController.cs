using EduApi.Data.Models;
using Microsoft.AspNetCore.Mvc;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Controllers.V1;

[ApiController]
[Route("v1/payment")]
[Produces("application/json")]
[Consumes("application/json")]
public class PaymentController(
    IPaymentService paymentService,
    ILogger<PaymentController> logger,
    ILanguageResolver languageResolver,
    IApiMessageLocalizer messageLocalizer)
    : V1ControllerBase(languageResolver, messageLocalizer)
{
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ApiResponse<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ApiResponse<string>>> Login(string id, [FromQuery] string password = "202411")
    {
        if (string.IsNullOrEmpty(password))
        {
            password = "202411";
        }

        try
        {
            logger.LogInformation("Login with card number {id}", id);
            var result = await paymentService.Login(id, password, Language);
            logger.LogInformation("Login result: {result}", result);
            return Ok(SuccessResponse(result));
        }
        catch (PaymentServiceException ex)
        {
            logger.LogError(ex, "Payment service error during login for card {id}", id);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ErrorResponse(ApiCodes.UpstreamError, ex.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during login for card {id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse(ApiCodes.InternalError, Message(ApiMessageKey.PaymentLoginUnknownError)));
        }
    }

    [HttpGet("{id}/turnover")]
    [ProducesResponseType(typeof(ApiResponse<PaymentTurnoverResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ApiResponse<PaymentTurnoverResult>>> GetTurnover(string id,
        [FromQuery] string password = "202411")
    {
        if (string.IsNullOrEmpty(password))
        {
            password = "202411";
        }

        try
        {
            logger.LogInformation("Get turnover with card number {id}", id);
            var result = await paymentService.GetTurnoverAsync(id, password, Language);
            logger.LogInformation("Get turnover result: {result}", result);
            return Ok(SuccessResponse(new PaymentTurnoverResult
            {
                Records = result.Records,
                Balance = result.Total
            }));
        }
        catch (PaymentServiceException ex)
        {
            logger.LogError(ex, "Payment service error during fetching turnover for card {id}", id);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ErrorResponse(ApiCodes.UpstreamError, ex.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during fetching turnover for card {id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse(ApiCodes.InternalError, Message(ApiMessageKey.PaymentTurnoverUnknownError)));
        }
    }

    /// <summary>
    /// 获取校园卡支付码内容。
    /// </summary>
    /// <remarks>
    /// 返回 <c>payload</c>（二维码原始字节的 Base64）与 <c>mode</c>（offline/online），
    /// 二维码由客户端自行渲染（避免服务端引入图像编码依赖）。
    /// 支付码有时效，因此响应带 <c>Cache-Control: no-store</c>，客户端需按约定节奏重新获取。
    /// </remarks>
    /// <param name="id">校园卡号。</param>
    /// <param name="password">密码。</param>
    /// <response code="200">支付码内容</response>
    /// <response code="500">服务器内部错误</response>
    /// <response code="503">校园卡上游失败</response>
    [HttpGet("{id}/qrcode")]
    [ProducesResponseType(typeof(ApiResponse<PaymentQrCodeResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ApiResponse<PaymentQrCodeResult>>> GetQrCode(
        string id, [FromQuery] string password = "202411")
    {
        if (string.IsNullOrEmpty(password))
        {
            password = "202411";
        }

        try
        {
            logger.LogInformation("Get payment QR code with card number {id}", id);
            var qrCode = await paymentService.GetQrCodePayloadAsync(id, password);

            // 离线支付码有时效，任何中间层都不得缓存
            Response.Headers.CacheControl = "no-store";
            return Ok(SuccessResponse(qrCode));
        }
        catch (PaymentServiceException ex)
        {
            logger.LogError(ex, "Payment service error during fetching QR code for card {id}", id);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ErrorResponse(ApiCodes.UpstreamError, ex.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during fetching QR code for card {id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse(ApiCodes.InternalError, Message(ApiMessageKey.PaymentQrCodeUnknownError)));
        }
    }
}