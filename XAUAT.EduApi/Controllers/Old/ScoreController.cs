using EduApi.Data.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using XAUAT.EduApi.Extensions;
using XAUAT.EduApi.Filters;
using XAUAT.EduApi.Localization;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Controllers.Old
{
    /// <summary>
    /// 成绩查询控制器
    /// 提供学生成绩查询、学期数据解析等功能
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    [Produces("application/json")]
    [Consumes("application/json")]
    [ServiceFilter(typeof(EduCrawlerRateLimitFilter))]
    [EnableRateLimiting("EduCrawler")]
    public class ScoreController(
        ILogger<ScoreController> logger,
        IScoreService scoreService,
        ILanguageResolver languageResolver,
        IApiMessageLocalizer messageLocalizer)
        : LanguageAwareControllerBase(languageResolver, messageLocalizer)
    {
        /// <summary>
        /// 解析学期数据
        /// 获取学生可查询的学期列表
        /// </summary>
        /// <param name="studentId">学生ID，多个ID用逗号分隔</param>
        /// <returns>学期数据列表</returns>
        /// <response code="200">成功获取学期数据</response>
        /// <response code="500">服务器内部错误</response>
        /// <remarks>
        /// 示例请求：
        /// GET /Score/Semester?studentId=123456
        /// Header:
        /// x-language: zh-CN (also accepts legacy alias: zh)
        /// </remarks>
        [HttpGet("Semester")]
        [ProducesResponseType(typeof(SemesterResult), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<SemesterResult>> ParseSemester(string? studentId)
        {
            try
            {
                logger.LogInformation("开始解析学期数据");
                var cookie = Request.GetEduAuthCookie();

                var result = await scoreService.ParseSemesterAsync(studentId, cookie, Language);
                return result;
            }
            catch (Exceptions.StudentCooldownException)
            {
                return RateLimited(ApiMessageKey.EduSystemRateLimited);
            }
            catch (Exceptions.UnAuthenticationError)
            {
                return Unauthorized(Message(ApiMessageKey.AuthenticationFailed));
            }
            catch (Exceptions.RateLimitException)
            {
                return RateLimited(ApiMessageKey.EduSystemRateLimited);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "解析学期数据时出错");
                return StatusCode(500, new ErrorResponse { error = ex.Message });
            }
        }

        /// <summary>
        /// 获取当前学期
        /// 获取学生当前所在的学期信息
        /// </summary>
        /// <returns>当前学期信息</returns>
        /// <response code="200">成功获取当前学期</response>
        /// <response code="500">服务器内部错误</response>
        /// <remarks>
        /// 示例请求：
        /// GET /Score/ThisSemester
        /// Header:
        /// x-language: zh-CN (also accepts legacy alias: zh)
        /// </remarks>
        [HttpGet("ThisSemester")]
        [ProducesResponseType(typeof(SemesterItem), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<SemesterItem>> GetThisSemester()
        {
            try
            {
                var cookie = Request.GetEduAuthCookie();
                var resolvedStudentId = HttpContext.GetResolvedStudentIds()
                    .FirstOrDefault(x => !x.StartsWith("cookie:", StringComparison.Ordinal));

                return await scoreService.GetThisSemesterAsync(cookie, Language, resolvedStudentId);
            }
            catch (Exceptions.StudentCooldownException)
            {
                return RateLimited(ApiMessageKey.EduSystemRateLimited);
            }
            catch (Exceptions.UnAuthenticationError)
            {
                return Unauthorized(Message(ApiMessageKey.AuthenticationFailed));
            }
            catch (Exceptions.RateLimitException)
            {
                return RateLimited(ApiMessageKey.EduSystemRateLimited);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "获取当前学期时出错");
                return StatusCode(500, new ErrorResponse { error = ex.Message });
            }
        }

        /// <summary>
        /// 获取学生成绩
        /// 根据学生ID和学期获取学生的成绩列表
        /// </summary>
        /// <param name="studentId">学生ID，多个ID用逗号分隔</param>
        /// <param name="semester">学期代码，如2024-2025-2</param>
        /// <returns>成绩列表</returns>
        /// <response code="200">成功获取成绩列表</response>
        /// <response code="400">参数错误</response>
        /// <response code="500">服务器内部错误</response>
        /// <remarks>
        /// 示例请求：
        /// GET /Score?studentId=123456 & semester=2024-2025-2
        /// Header:
        /// x-language: zh-CN (also accepts legacy alias: zh)
        /// </remarks>
        [HttpGet]
        [ProducesResponseType(typeof(List<ScoreResponse>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<List<ScoreResponse>>> GetScore(string studentId, string semester)
        {
            try
            {
                logger.LogInformation("开始获取考试分数");
                var cookie = Request.GetEduAuthCookie();

                var scores = await scoreService.GetScoresAsync(studentId, semester, cookie, Language);
                return scores;
            }
            catch (Exceptions.StudentCooldownException)
            {
                return RateLimited(ApiMessageKey.EduSystemRateLimited);
            }
            catch (ArgumentNullException ex)
            {
                logger.LogWarning(ex, "参数错误");
                return BadRequest(new ErrorResponse { error = ex.Message });
            }
            catch (Exceptions.UnAuthenticationError)
            {
                return Unauthorized(Message(ApiMessageKey.AuthenticationFailed));
            }
            catch (Exceptions.RateLimitException)
            {
                return RateLimited(ApiMessageKey.EduSystemRateLimited);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "获取成绩时出错");
                return StatusCode(500, new ErrorResponse { error = ex.Message });
            }
        }
    }
}
