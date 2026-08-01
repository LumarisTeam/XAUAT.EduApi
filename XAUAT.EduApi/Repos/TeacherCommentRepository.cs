using EduApi.Data;
using EduApi.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace XAUAT.EduApi.Repos;

public class TeacherCommentRepository(IDbContextFactory<EduContext> contextFactory)
    : RepositoryBase<TeacherCommentModel>(contextFactory), ITeacherCommentRepository
{
    public async Task<(List<TeacherCommentModel> Items, int Total)> QueryAsync(
        string? teacherName,
        string? courseName,
        CommentReviewStatus? status,
        int? minStar,
        int page,
        int pageSize)
    {
        await using var context = CreateContext();
        var query = context.TeacherComments.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(teacherName))
        {
            query = query.Where(c => c.TeacherName.Contains(teacherName));
        }

        if (!string.IsNullOrWhiteSpace(courseName))
        {
            query = query.Where(c => c.CourseName.Contains(courseName));
        }

        if (status.HasValue)
        {
            query = query.Where(c => c.Status == status.Value);
        }

        if (minStar.HasValue)
        {
            query = query.Where(c => c.Star >= minStar.Value);
        }

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(c => c.CreatedOn)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (items, total);
    }

    public async Task<IEnumerable<TeacherCommentModel>> GetByStudentIdAsync(string studentId)
    {
        await using var context = CreateContext();
        return await context.TeacherComments.AsNoTracking()
            .Where(c => c.StudentId == studentId)
            .OrderByDescending(c => c.CreatedOn)
            .ToListAsync();
    }

    public async Task<IEnumerable<TeacherCommentModel>> GetApprovedByTeacherNameAsync(string teacherName)
    {
        await using var context = CreateContext();
        return await context.TeacherComments.AsNoTracking()
            .Where(c => c.TeacherName.Contains(teacherName) && c.Status == CommentReviewStatus.Approved)
            .OrderByDescending(c => c.CreatedOn)
            .ToListAsync();
    }

    public async Task<IEnumerable<TeacherCommentModel>> GetPendingAsync()
    {
        await using var context = CreateContext();
        return await context.TeacherComments.AsNoTracking()
            .Where(c => c.Status == CommentReviewStatus.Pending)
            .OrderByDescending(c => c.CreatedOn)
            .ToListAsync();
    }
}