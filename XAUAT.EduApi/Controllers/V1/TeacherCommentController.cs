using EduApi.Data.Models;
using Microsoft.AspNetCore.Mvc;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Controllers.V1;

/// <summary>
/// 教师评价接口 —— 校园集市
/// </summary>
[ApiController]
[Route("v1/[controller]")]
[Produces("application/json")]
[Consumes("application/json")]
public class TeacherCommentController(
    ITeacherCommentService commentService,
    ILogger<TeacherCommentController> logger,
    ILanguageResolver languageResolver,
    IApiMessageLocalizer messageLocalizer)
    : V1ControllerBase(languageResolver, messageLocalizer)
{
    /// <summary>
    /// 提交教师评价
    /// </summary>
    /// <param name="request">评价内容</param>
    /// <param name="studentId">学生ID（可选，优先使用已认证的身份）</param>
    /// <param name="studentName">学生昵称（可选）</param>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<TeacherCommentItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse<TeacherCommentItem>>> Create(
        [FromBody] CreateTeacherCommentRequest request,
        string? studentId = null,
        string? studentName = null)
    {
        try
        {
            var resolvedIds = HttpContext.GetResolvedStudentIds();
            var effectiveStudentId = studentId ?? resolvedIds.FirstOrDefault();

            if (string.IsNullOrEmpty(effectiveStudentId))
            {
                return BadRequest(ErrorResponse(ApiCodes.ParamError, "缺少学生身份标识"));
            }

            var result = await commentService.CreateAsync(effectiveStudentId, studentName, request);
            return Ok(SuccessResponse(result));
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "提交评价参数错误");
            return BadRequest(ErrorResponse(ApiCodes.ParamError, ex.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "提交评价失败");
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse(ApiCodes.InternalError, ex.Message));
        }
    }

    /// <summary>
    /// 分页查询评价列表（管理员接口）
    /// </summary>
    /// <param name="request">查询参数</param>
    [HttpGet("admin/list")]
    [ProducesResponseType(typeof(ApiResponse<TeacherCommentListResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse<TeacherCommentListResponse>>> AdminList(
        [FromQuery] TeacherCommentQueryRequest request)
    {
        try
        {
            var result = await commentService.QueryAsync(request);
            return Ok(SuccessResponse(result));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "查询评价列表失败");
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse(ApiCodes.InternalError, ex.Message));
        }
    }

    /// <summary>
    /// 获取待审核评价列表（管理员接口）
    /// </summary>
    [HttpGet("admin/pending")]
    [ProducesResponseType(typeof(ApiResponse<List<TeacherCommentItem>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse<List<TeacherCommentItem>>>> AdminPending()
    {
        try
        {
            var result = await commentService.GetPendingListAsync();
            return Ok(SuccessListResponse(result));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取待审核列表失败");
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse(ApiCodes.InternalError, ex.Message));
        }
    }

    /// <summary>
    /// 审核评价（管理员接口）
    /// </summary>
    /// <param name="key">评价主键</param>
    /// <param name="request">审核请求</param>
    [HttpPut("admin/review/{key}")]
    [ProducesResponseType(typeof(ApiResponse<TeacherCommentItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse<TeacherCommentItem>>> AdminReview(
        string key,
        [FromBody] ReviewTeacherCommentRequest request)
    {
        try
        {
            var adminId = HttpContext.GetResolvedStudentIds().FirstOrDefault() ?? "admin";
            var result = await commentService.ReviewAsync(key, adminId, request);
            return Ok(SuccessResponse(result));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ErrorResponse(ApiCodes.NotFound, ex.Message));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ErrorResponse(ApiCodes.ParamError, ex.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "审核评价失败");
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse(ApiCodes.InternalError, ex.Message));
        }
    }

    /// <summary>
    /// 获取我的评价列表
    /// </summary>
    /// <param name="studentId">学生ID（可选）</param>
    [HttpGet("mine")]
    [ProducesResponseType(typeof(ApiResponse<List<TeacherCommentItem>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse<List<TeacherCommentItem>>>> MyComments(string? studentId = null)
    {
        try
        {
            var resolvedIds = HttpContext.GetResolvedStudentIds();
            var effectiveStudentId = studentId ?? resolvedIds.FirstOrDefault();

            if (string.IsNullOrEmpty(effectiveStudentId))
            {
                return BadRequest(ErrorResponse(ApiCodes.ParamError, "缺少学生身份标识"));
            }

            var result = await commentService.GetMyCommentsAsync(effectiveStudentId);
            return Ok(SuccessListResponse(result));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取我的评价失败");
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse(ApiCodes.InternalError, ex.Message));
        }
    }

    /// <summary>
    /// 删除评价
    /// </summary>
    /// <param name="key">评价主键</param>
    /// <param name="studentId">学生ID（可选，优先使用已认证的身份）</param>
    [HttpDelete("{key}")]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse<bool>>> Delete(string key, string? studentId = null)
    {
        try
        {
            var resolvedIds = HttpContext.GetResolvedStudentIds();
            var effectiveStudentId = studentId ?? resolvedIds.FirstOrDefault();

            if (string.IsNullOrEmpty(effectiveStudentId))
            {
                return BadRequest(ErrorResponse(ApiCodes.ParamError, "缺少学生身份标识"));
            }

            var result = await commentService.DeleteAsync(key, effectiveStudentId);
            if (!result)
            {
                return NotFound(ErrorResponse(ApiCodes.NotFound, "评价不存在或无权删除"));
            }

            return Ok(SuccessResponse(true));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "删除评价失败");
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse(ApiCodes.InternalError, ex.Message));
        }
    }

    /// <summary>
    /// 查询指定教师的已通过评价（公开接口）
    /// </summary>
    /// <param name="teacherName">教师姓名</param>
    [HttpGet("teacher/{teacherName}")]
    [ProducesResponseType(typeof(ApiResponse<List<TeacherCommentItem>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse<List<TeacherCommentItem>>>> GetByTeacher(string teacherName)
    {
        try
        {
            var result = await commentService.GetApprovedByTeacherAsync(teacherName);
            return Ok(SuccessListResponse(result));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "查询教师评价失败");
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse(ApiCodes.InternalError, ex.Message));
        }
    }

    /// <summary>
    /// 获取教师评价汇总（公开接口）
    /// </summary>
    /// <param name="teacherName">教师姓名</param>
    /// <param name="teacherId">教师工号（可选）</param>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(ApiResponse<TeacherCommentSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse<TeacherCommentSummary>>> GetSummary(
        string teacherName, string? teacherId = null)
    {
        try
        {
            var result = await commentService.GetSummaryAsync(teacherName, teacherId);
            return Ok(SuccessResponse(result));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取教师评价汇总失败");
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorResponse(ApiCodes.InternalError, ex.Message));
        }
    }
}