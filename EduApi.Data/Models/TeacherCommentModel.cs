using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace EduApi.Data.Models;

/// <summary>
/// 教师评价审核状态
/// </summary>
public enum CommentReviewStatus
{
    /// <summary>待审核</summary>
    Pending = 0,
    /// <summary>已通过</summary>
    Approved = 1,
    /// <summary>已拒绝</summary>
    Rejected = 2
}

/// <summary>
/// 教师评价 AI 审核结论
/// </summary>
public enum AiReviewVerdict
{
    /// <summary>未审核</summary>
    None = 0,
    /// <summary>AI 判定通过</summary>
    Pass = 1,
    /// <summary>AI 判定有风险（建议人工审核）</summary>
    Risk = 2,
    /// <summary>AI 判定违规</summary>
    Block = 3
}

/// <summary>
/// 教师评价模型 —— 校园集市教师评价系统
/// </summary>
[Table("teacher_comments")]
public class TeacherCommentModel : DataModel
{
    /// <summary>主键</summary>
    [JsonIgnore]
    [Key]
    [MaxLength(64)]
    public string Key { get; set; } = Guid.NewGuid().ToString();

    /// <summary>教师姓名</summary>
    [MaxLength(128)]
    public string TeacherName { get; set; } = "";

    /// <summary>教师工号（可选，用于精确关联）</summary>
    [MaxLength(64)]
    public string? TeacherId { get; set; }

    /// <summary>课程名称</summary>
    [MaxLength(256)]
    public string CourseName { get; set; } = "";

    /// <summary>评价学生ID</summary>
    [JsonIgnore]
    [MaxLength(64)]
    public string StudentId { get; set; } = "";

    /// <summary>学生昵称（可选，展示用）</summary>
    [MaxLength(128)]
    public string? StudentName { get; set; }

    /// <summary>评价内容</summary>
    [MaxLength(2000)]
    public string? Comment { get; set; }

    /// <summary>评分（1-5 星，默认 5）</summary>
    public int Star { get; set; } = 5;

    /// <summary>审核状态</summary>
    public CommentReviewStatus Status { get; set; } = CommentReviewStatus.Pending;

    /// <summary>AI 审核结论</summary>
    public AiReviewVerdict AiVerdict { get; set; } = AiReviewVerdict.None;

    /// <summary>AI 审核原因/说明</summary>
    [MaxLength(1024)]
    public string? AiReason { get; set; }

    /// <summary>AI 审核置信度 (0-100)</summary>
    public int? AiScore { get; set; }

    /// <summary>AI 审核时间</summary>
    public DateTime? AiReviewedOn { get; set; }

    /// <summary>人工审核时间</summary>
    public DateTime? ReviewedOn { get; set; }

    /// <summary>审核人ID</summary>
    [MaxLength(64)]
    public string? ReviewedBy { get; set; }

    /// <summary>创建时间</summary>
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 提交评价请求
/// </summary>
public class CreateTeacherCommentRequest
{
    /// <summary>教师姓名</summary>
    public string TeacherName { get; set; } = "";

    /// <summary>教师工号（可选）</summary>
    public string? TeacherId { get; set; }

    /// <summary>课程名称</summary>
    public string CourseName { get; set; } = "";

    /// <summary>评价内容</summary>
    public string? Comment { get; set; }

    /// <summary>评分（1-5）</summary>
    public int Star { get; set; } = 5;
}

/// <summary>
/// 管理员审核请求
/// </summary>
public class ReviewTeacherCommentRequest
{
    /// <summary>审核结论：Approved / Rejected</summary>
    public CommentReviewStatus Status { get; set; }

    /// <summary>审核备注</summary>
    public string? ReviewNote { get; set; }
}

/// <summary>
/// 评价查询参数
/// </summary>
public class TeacherCommentQueryRequest
{
    /// <summary>教师姓名（模糊匹配）</summary>
    public string? TeacherName { get; set; }

    /// <summary>课程名称（模糊匹配）</summary>
    public string? CourseName { get; set; }

    /// <summary>审核状态筛选</summary>
    public CommentReviewStatus? Status { get; set; }

    /// <summary>最低评分</summary>
    public int? MinStar { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页数量</summary>
    public int PageSize { get; set; } = 20;
}

/// <summary>
/// 评价列表响应
/// </summary>
public class TeacherCommentListResponse
{
    /// <summary>数据列表</summary>
    public List<TeacherCommentItem> Items { get; set; } = [];

    /// <summary>总数</summary>
    public int Total { get; set; }

    /// <summary>当前页</summary>
    public int Page { get; set; }

    /// <summary>每页数量</summary>
    public int PageSize { get; set; }
}

/// <summary>
/// 评价项
/// </summary>
public class TeacherCommentItem
{
    /// <summary>主键</summary>
    public string Key { get; set; } = "";

    /// <summary>教师姓名</summary>
    public string TeacherName { get; set; } = "";

    /// <summary>教师工号</summary>
    public string? TeacherId { get; set; }

    /// <summary>课程名称</summary>
    public string CourseName { get; set; } = "";

    /// <summary>学生昵称</summary>
    public string? StudentName { get; set; }

    /// <summary>评价内容</summary>
    public string? Comment { get; set; }

    /// <summary>评分</summary>
    public int Star { get; set; }

    /// <summary>审核状态</summary>
    public CommentReviewStatus Status { get; set; }

    /// <summary>AI 审核结论</summary>
    public AiReviewVerdict AiVerdict { get; set; }

    /// <summary>AI 审核原因</summary>
    public string? AiReason { get; set; }

    /// <summary>AI 审核置信度</summary>
    public int? AiScore { get; set; }

    /// <summary>AI 审核时间</summary>
    public DateTime? AiReviewedOn { get; set; }

    /// <summary>人工审核时间</summary>
    public DateTime? ReviewedOn { get; set; }

    /// <summary>审核人</summary>
    public string? ReviewedBy { get; set; }

    /// <summary>创建时间</summary>
    public DateTime CreatedOn { get; set; }
}

/// <summary>
/// 教师评价汇总（按教师聚合）
/// </summary>
public class TeacherCommentSummary
{
    /// <summary>教师姓名</summary>
    public string TeacherName { get; set; } = "";

    /// <summary>教师工号</summary>
    public string? TeacherId { get; set; }

    /// <summary>评价总数</summary>
    public int TotalCount { get; set; }

    /// <summary>平均评分</summary>
    public double AverageStar { get; set; }

    /// <summary>课程列表</summary>
    public List<string> Courses { get; set; } = [];
}