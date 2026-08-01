using EduApi.Data.Models;

namespace XAUAT.EduApi.Repos;

public interface ITeacherCommentRepository : IRepository<TeacherCommentModel>
{
    /// <summary>
    /// 分页查询评价列表
    /// </summary>
    Task<(List<TeacherCommentModel> Items, int Total)> QueryAsync(
        string? teacherName,
        string? courseName,
        CommentReviewStatus? status,
        int? minStar,
        int page,
        int pageSize);

    /// <summary>
    /// 根据学生ID获取评价列表
    /// </summary>
    Task<IEnumerable<TeacherCommentModel>> GetByStudentIdAsync(string studentId);

    /// <summary>
    /// 根据教师姓名获取已通过的评价
    /// </summary>
    Task<IEnumerable<TeacherCommentModel>> GetApprovedByTeacherNameAsync(string teacherName);

    /// <summary>
    /// 获取待审核的评价列表
    /// </summary>
    Task<IEnumerable<TeacherCommentModel>> GetPendingAsync();
}