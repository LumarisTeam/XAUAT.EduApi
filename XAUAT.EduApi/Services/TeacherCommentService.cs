using EduApi.Data.Models;
using XAUAT.EduApi.Repos;

namespace XAUAT.EduApi.Services;

public interface ITeacherCommentService
{
    /// <summary>
    /// 提交教师评价
    /// </summary>
    Task<TeacherCommentItem> CreateAsync(string studentId, string? studentName, CreateTeacherCommentRequest request);

    /// <summary>
    /// 分页查询评价列表（管理员）
    /// </summary>
    Task<TeacherCommentListResponse> QueryAsync(TeacherCommentQueryRequest request);

    /// <summary>
    /// 获取我的评价列表
    /// </summary>
    Task<List<TeacherCommentItem>> GetMyCommentsAsync(string studentId);

    /// <summary>
    /// 审核评价
    /// </summary>
    Task<TeacherCommentItem> ReviewAsync(string key, string adminId, ReviewTeacherCommentRequest request);

    /// <summary>
    /// 获取待审核列表
    /// </summary>
    Task<List<TeacherCommentItem>> GetPendingListAsync();

    /// <summary>
    /// 获取指定教师的已通过评价
    /// </summary>
    Task<List<TeacherCommentItem>> GetApprovedByTeacherAsync(string teacherName);

    /// <summary>
    /// 获取教师评价汇总
    /// </summary>
    Task<TeacherCommentSummary> GetSummaryAsync(string teacherName, string? teacherId = null);

    /// <summary>
    /// 删除评价
    /// </summary>
    Task<bool> DeleteAsync(string key, string studentId);
}

public class TeacherCommentService(
    ITeacherCommentRepository repository,
    ILogger<TeacherCommentService> logger)
    : ITeacherCommentService
{
    public async Task<TeacherCommentItem> CreateAsync(string studentId, string? studentName, CreateTeacherCommentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TeacherName))
        {
            throw new ArgumentException("教师姓名不能为空", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.CourseName))
        {
            throw new ArgumentException("课程名称不能为空", nameof(request));
        }

        if (request.Star < 1 || request.Star > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(request.Star), "评分必须在 1-5 之间");
        }

        if (!string.IsNullOrEmpty(request.Comment) && request.Comment.Length > 2000)
        {
            throw new ArgumentOutOfRangeException(nameof(request.Comment), "评价内容不能超过 2000 字");
        }

        var model = new TeacherCommentModel
        {
            TeacherName = request.TeacherName.Trim(),
            TeacherId = request.TeacherId,
            CourseName = request.CourseName.Trim(),
            StudentId = studentId,
            StudentName = studentName,
            Comment = request.Comment?.Trim(),
            Star = request.Star,
            Status = CommentReviewStatus.Pending,
            AiVerdict = AiReviewVerdict.None,
            CreatedOn = DateTime.UtcNow
        };

        // TODO: 调用 AI 审核服务，填充 AiVerdict / AiReason / AiScore / AiReviewedOn

        await repository.AddAsync(model);
        logger.LogInformation("教师评价已提交，Key: {Key}, 教师: {TeacherName}, 学生: {StudentId}",
            model.Key, model.TeacherName, studentId);

        return ToItem(model);
    }

    public async Task<TeacherCommentListResponse> QueryAsync(TeacherCommentQueryRequest request)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);

        var (items, total) = await repository.QueryAsync(
            request.TeacherName,
            request.CourseName,
            request.Status,
            request.MinStar,
            page,
            pageSize);

        return new TeacherCommentListResponse
        {
            Items = items.Select(ToItem).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<List<TeacherCommentItem>> GetMyCommentsAsync(string studentId)
    {
        var comments = await repository.GetByStudentIdAsync(studentId);
        return comments.Select(ToItem).ToList();
    }

    public async Task<TeacherCommentItem> ReviewAsync(string key, string adminId, ReviewTeacherCommentRequest request)
    {
        if (request.Status != CommentReviewStatus.Approved && request.Status != CommentReviewStatus.Rejected)
        {
            throw new ArgumentException("审核状态必须是 Approved 或 Rejected", nameof(request));
        }

        var model = await repository.GetByIdAsync(key);
        if (model == null)
        {
            throw new KeyNotFoundException($"评价不存在，Key: {key}");
        }

        model.Status = request.Status;
        model.ReviewedOn = DateTime.UtcNow;
        model.ReviewedBy = adminId;

        // Note: ReviewNote 可扩展为单独字段或日志表，此处仅记录日志
        logger.LogInformation("评价已人工审核，Key: {Key}, 状态: {Status}, 审核人: {AdminId}, 备注: {Note}",
            key, request.Status, adminId, request.ReviewNote);

        await repository.UpdateAsync(model);

        return ToItem(model);
    }

    public async Task<List<TeacherCommentItem>> GetPendingListAsync()
    {
        var pending = await repository.GetPendingAsync();
        return pending.Select(ToItem).ToList();
    }

    public async Task<List<TeacherCommentItem>> GetApprovedByTeacherAsync(string teacherName)
    {
        var comments = await repository.GetApprovedByTeacherNameAsync(teacherName);
        return comments.Select(ToItem).ToList();
    }

    public async Task<TeacherCommentSummary> GetSummaryAsync(string teacherName, string? teacherId = null)
    {
        // 获取该教师的所有已通过评价
        var allComments = await repository.GetApprovedByTeacherNameAsync(teacherName);
        var list = allComments.ToList();

        // 如果指定了工号，进一步过滤
        if (!string.IsNullOrWhiteSpace(teacherId))
        {
            list = list.Where(c => c.TeacherId == teacherId).ToList();
        }

        var courses = list.Select(c => c.CourseName).Distinct().ToList();
        var totalCount = list.Count;
        var averageStar = totalCount > 0 ? list.Average(c => c.Star) : 0;

        return new TeacherCommentSummary
        {
            TeacherName = teacherName,
            TeacherId = teacherId,
            TotalCount = totalCount,
            AverageStar = Math.Round(averageStar, 1),
            Courses = courses
        };
    }

    public async Task<bool> DeleteAsync(string key, string studentId)
    {
        var model = await repository.GetByIdAsync(key);
        if (model == null)
        {
            return false;
        }

        if (model.StudentId != studentId)
        {
            logger.LogWarning("删除评价权限不足，Key: {Key}, 请求学生: {Requester}, 所属学生: {Owner}",
                key, studentId, model.StudentId);
            return false;
        }

        await repository.DeleteAsync(model);
        logger.LogInformation("评价已删除，Key: {Key}, 学生: {StudentId}", key, studentId);
        return true;
    }

    private static TeacherCommentItem ToItem(TeacherCommentModel model)
    {
        return new TeacherCommentItem
        {
            Key = model.Key,
            TeacherName = model.TeacherName,
            TeacherId = model.TeacherId,
            CourseName = model.CourseName,
            StudentName = model.StudentName,
            Comment = model.Comment,
            Star = model.Star,
            Status = model.Status,
            AiVerdict = model.AiVerdict,
            AiReason = model.AiReason,
            AiScore = model.AiScore,
            AiReviewedOn = model.AiReviewedOn,
            ReviewedOn = model.ReviewedOn,
            ReviewedBy = model.ReviewedBy,
            CreatedOn = model.CreatedOn
        };
    }
}