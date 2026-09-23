using System.Globalization;

namespace XAUAT.EduApi.Services;

/// <summary>
/// 「第 N 周 + 星期几」→ 具体日期 的换算。
/// <para>
/// 抽成无依赖的纯函数，是因为这是整条日历链路里最容易算错的一环，必须能脱离 HTTP 单独验证。
/// </para>
/// <para>
/// <b>周的起点是周日</b>，且 <c>weekday = 7</c>（周日）落在该周的**第一天**而不是最后一天。
/// 这不是推测：用真实上游样本（<c>schedule-table-datum.json</c>，315 条带
/// <c>date</c>/<c>weekIndex</c>/<c>weekday</c> 的记录）反解锚点，周日起始的假设 315/315 命中，
/// 周一起始的假设只有 302/315（13 条周日课落空）。与客户端
/// <c>WeekStartUtils.getWeekIndexByStartTime</c>（<c>weekStartDay = sunday</c>）两相印证。
/// </para>
/// </summary>
public static class SemesterWeekMath
{
    /// <summary>
    /// 由学期起始日推出「第 1 周的周首」。
    /// <para>
    /// 容错性很好：<paramref name="semesterStart"/> 落在第 1 周那个周日..周六区间的任意一天，
    /// 都会得到同一个锚点（±6 天）。
    /// </para>
    /// </summary>
    /// <returns>解析失败时返回 false，调用方应据此跳过课程事件。</returns>
    public static bool TryGetWeekAnchor(string? semesterStart, out DateTime anchor)
    {
        anchor = default;

        if (string.IsNullOrWhiteSpace(semesterStart))
        {
            return false;
        }

        if (!DateTime.TryParse(semesterStart, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var start))
        {
            return false;
        }

        // 含 start 的那个周日；C# 的 DayOfWeek.Sunday 就是 0，正好是要往回退的天数
        anchor = start.Date.AddDays(-(int)start.DayOfWeek);
        return true;
    }

    /// <summary>
    /// 第 <paramref name="weekIndex"/> 周、星期 <paramref name="weekday"/> 的日期。
    /// </summary>
    /// <param name="weekIndex">周次，从 1 开始（教务系统就是这么给的）。</param>
    /// <param name="weekday">星期，<b>1 = 周一 .. 7 = 周日</b>（教务系统与客户端的约定）。</param>
    /// <returns>参数越界时返回 <see cref="DateTime.MinValue"/>，调用方跳过该条而不是抛异常
    /// ——单条坏数据不该影响整张课表。</returns>
    public static DateTime GetDate(DateTime anchor, int weekIndex, int weekday)
    {
        if (weekIndex <= 0 || weekday is < 1 or > 7)
        {
            return DateTime.MinValue;
        }

        // weekday % 7：周一(1)→1 … 周六(6)→6、周日(7)→0，正是相对周首（周日）的偏移
        return anchor.AddDays((weekIndex - 1) * 7 + weekday % 7);
    }
}
