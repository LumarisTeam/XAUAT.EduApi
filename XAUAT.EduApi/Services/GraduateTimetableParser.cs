using System.Text.RegularExpressions;
using EduApi.Data.Models;

namespace XAUAT.EduApi.Services;

/// <summary>
/// 研究生教务系统课表页（<c>https://gmis.xauat.edu.cn/pyxx/pygl/xskbcx</c>）的解析器。
/// <para>
/// 逐条移植自仓库外的 <c>src/parse_html.py</c>。页面的数据不是 <c>&lt;table&gt;</c> 标签，
/// 而是一段段 <c>var td = document.getElementById("td_星期_节次"); td.innerHTML += "..."</c>
/// 的 JS —— 所以只能用正则按"单元格赋值"切块，再把块内每个 <c>innerHTML +=</c>
/// 当作一门课的一次安排。
/// </para>
/// <para>
/// <b>顺序不能改</b>：课程信息靠 <c>&lt;br&gt;</c> 分段，若先把整块 HTML 标签清掉，
/// 各行会粘成一行，<c>课程/班级/教室</c> 字段就再也切不出来。
/// </para>
/// </summary>
internal static class GraduateTimetableParser
{
    /// <summary>
    /// 节次编码 → (时段, 时段内第几节)。41-44 上午、45-46 中午、47-50 下午、51-52 晚上。
    /// 这些编码不是"第几节"，而是教务系统内部的排课格子编号，必须原样映射。
    /// </summary>
    private static readonly Dictionary<string, (string Period, int Index)> PeriodMap = new()
    {
        ["41"] = ("上午", 1),
        ["42"] = ("上午", 2),
        ["43"] = ("上午", 3),
        ["44"] = ("上午", 4),
        ["45"] = ("中午", 1),
        ["46"] = ("中午", 2),
        ["47"] = ("下午", 1),
        ["48"] = ("下午", 2),
        ["49"] = ("下午", 3),
        ["50"] = ("下午", 4),
        ["51"] = ("晚上", 1),
        ["52"] = ("晚上", 2)
    };

    /// <summary>时段起点，用于把"时段内第几节"换算成全天连续节次（1-12）。</summary>
    private static readonly Dictionary<string, int> UnitBase = new()
    {
        ["上午"] = 0,
        ["中午"] = 4,
        ["下午"] = 6,
        ["晚上"] = 10
    };

    /// <summary>
    /// 一个"格子赋值"块：从 <c>var td = getElementById("td_1_41");</c> 起，
    /// 到下一个 <c>var td</c> 或收尾的 <c>hb_cell()</c> 前为止。
    /// </summary>
    private static readonly Regex ChunkPattern = new(
        """var\s+td\s*=\s*document\.getElementById\("td_(\d)_(\d+)"\)\s*;(.*?)(?=var\s+td\s*=\s*document\.getElementById\(|hb_cell\s*\(\s*\)\s*;)""",
        RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>块内的一条 <c>td.innerHTML += "…";</c>。捕获组是 JS 字符串字面量的内容（含转义）。</summary>
    private static readonly Regex InnerHtmlPattern = new(
        """td\.innerHTML\s*\+=\s*"((?:[^"\\]|\\.)*)"\s*;""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LineBreakPattern = new(
        """<br\s*/?>""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FieldPattern = new(
        """^\s*(课程|班级|主讲教师|教室)\s*[:：]\s*(.*)$""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TagPattern = new(
        """<[^>]+>""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>教室字段末尾的周次范围：<c>(第1-16周)</c> / <c>(第2-8周,双周)</c>。</summary>
    private static readonly Regex WeekRangePattern = new(
        """\(\s*第\s*(\d+)\s*-\s*(\d+)\s*周\s*([^)]*)\)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>教室字段最后一段是不是"纯房间号"，如 <c>208</c>、<c>106</c>、<c>208A</c>。</summary>
    private static readonly Regex RoomNumberPattern = new(
        """^\d+[A-Za-z]?$""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 解析课程表 HTML，输出与本科生课表同构的 <see cref="CourseActivity"/> 列表：
    /// 每一条对应"课程 + 星期 + 连续节次 + 教室 + 周次"的一种安排。
    /// </summary>
    public static List<CourseActivity> Parse(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return [];
        }

        var entries = new List<CellEntry>();

        foreach (Match chunk in ChunkPattern.Matches(html))
        {
            if (!int.TryParse(chunk.Groups[1].Value, out var weekday) || weekday is < 1 or > 7)
            {
                continue;
            }

            if (!PeriodMap.TryGetValue(chunk.Groups[2].Value, out var period))
            {
                continue;
            }

            var unit = UnitBase[period.Period] + period.Index;

            foreach (Match cell in InnerHtmlPattern.Matches(chunk.Groups[3].Value))
            {
                var info = ParseCourseBlock(Unescape(cell.Groups[1].Value));
                if (info.Course.Length == 0)
                {
                    continue;
                }

                var (campus, room) = SplitRoom(info.Classroom);
                entries.Add(new CellEntry(
                    info.Course, info.Teacher, campus, room,
                    ExpandWeeks(info.WeekStart, info.WeekEnd), weekday, unit));
            }
        }

        // 同课程、同星期、同教室、同教师、同周次集合的相邻节次合并成一个区间
        // （例如 41、42 两格 → 第 1-2 节），与 Python 版一致。
        var courses = new List<CourseActivity>();

        foreach (var group in entries.GroupBy(entry => new
                 {
                     entry.Course,
                     entry.Weekday,
                     entry.Campus,
                     entry.Room,
                     entry.Teacher,
                     WeekKey = string.Join(',', entry.Weeks)
                 }))
        {
            var weeks = group.First().Weeks;

            foreach (var range in ToRanges(group.Select(entry => entry.Unit)))
            {
                courses.Add(new CourseActivity
                {
                    CourseName = group.Key.Course,
                    Weekday = group.Key.Weekday,
                    StartUnit = range.Start,
                    EndUnit = range.End,
                    Campus = group.Key.Campus,
                    // 与本科生链路的 Room 归一化保持一致（空值兜底、去掉上游的星号标记）
                    Room = group.Key.Room.Length == 0 ? "未知" : group.Key.Room.Replace("*", ""),
                    Teachers = group.Key.Teacher.Length == 0 ? [] : [group.Key.Teacher],
                    WeekIndexes = [.. weeks]
                });
            }
        }

        // 输出顺序固定，便于缓存/客户端做稳定 diff
        courses.Sort(static (left, right) =>
        {
            var byCourse = string.CompareOrdinal(left.CourseName, right.CourseName);
            if (byCourse != 0) return byCourse;

            var byWeekday = left.Weekday.CompareTo(right.Weekday);
            return byWeekday != 0 ? byWeekday : left.StartUnit.CompareTo(right.StartUnit);
        });

        return courses;
    }

    /// <summary>
    /// 解析一个 <c>innerHTML</c> 课程块。必须逐行 <c>&lt;br&gt;</c> 切分后再清洗标签。
    /// </summary>
    private static CourseBlockInfo ParseCourseBlock(string block)
    {
        string course = "", teacher = "", classroom = "";

        foreach (var rawLine in LineBreakPattern.Split(block))
        {
            var line = Clean(rawLine);
            if (line.Length == 0)
            {
                continue;
            }

            var match = FieldPattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            switch (match.Groups[1].Value)
            {
                case "课程":
                    course = match.Groups[2].Value.Trim();
                    break;
                case "主讲教师":
                    teacher = match.Groups[2].Value.Trim();
                    break;
                case "教室":
                    classroom = match.Groups[2].Value.Trim();
                    break;
                // "班级" 目前不输出，与 Python 版一致
            }
        }

        int? weekStart = null, weekEnd = null;
        var weekMatch = WeekRangePattern.Match(classroom);
        if (weekMatch.Success)
        {
            weekStart = int.Parse(weekMatch.Groups[1].Value);
            weekEnd = int.Parse(weekMatch.Groups[2].Value);
            classroom = classroom[..weekMatch.Index].Trim();
        }

        return new CourseBlockInfo(course, teacher, classroom, weekStart, weekEnd);
    }

    /// <summary>去掉 HTML 标签并还原两个最常见实体，与 Python 的 <c>_clean</c> 一致。</summary>
    private static string Clean(string value)
    {
        var cleaned = TagPattern.Replace(value, "");
        cleaned = cleaned.Replace("&nbsp;", " ").Replace("&amp;", "&");
        return cleaned.Trim();
    }

    /// <summary>
    /// 还原 JS 字符串里的转义。顺序与 Python 相同：先 <c>\"</c>，再 <c>\\</c>。
    /// </summary>
    private static string Unescape(string value)
        => value.Replace("\\\"", "\"").Replace("\\\\", "\\");

    /// <summary>
    /// <c>草堂校区-南山书院-南山书院303</c> → (草堂校区, 南山书院303)；
    /// <c>草堂校区-草堂校区9号楼-草堂9-208</c> → (草堂校区, 草堂9-208)。
    /// <para>
    /// 第一段是校区、最后一段是教室。<b>最后一段是纯房间号时要拼回上一段</b>：
    /// 这种格式下教务把楼栋简称切进了倒数第二段（<c>草堂9</c> + <c>208</c>），
    /// 只取最后一段会丢掉楼栋，两个不同楼的同名房间就分不开了。
    /// </para>
    /// <para>
    /// 没有分隔符时只认作校区、教室留空（调用方会兜底成"未知"），与 Python 版一致。
    /// </para>
    /// </summary>
    private static (string Campus, string Room) SplitRoom(string classroom)
    {
        var parts = classroom.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return ("", "");
        }

        var campus = parts[0];
        if (parts.Length == 1)
        {
            return (campus, "");
        }

        var last = parts[^1];
        var room = parts.Length >= 3 && RoomNumberPattern.IsMatch(last)
            ? $"{parts[^2]}-{last}"
            : last;

        return (campus, room);
    }

    private static List<int> ExpandWeeks(int? start, int? end)
    {
        if (start is null || end is null || end < start)
        {
            return [];
        }

        var weeks = new List<int>(end.Value - start.Value + 1);
        for (var week = start.Value; week <= end.Value; week++)
        {
            weeks.Add(week);
        }

        return weeks;
    }

    /// <summary>把节次集合压缩成连续区间：<c>3,4,6,7</c> → <c>(3,4),(6,7)</c>。</summary>
    private static List<(int Start, int End)> ToRanges(IEnumerable<int> units)
    {
        var values = units.Distinct().OrderBy(unit => unit).ToList();
        if (values.Count == 0)
        {
            return [];
        }

        var ranges = new List<(int Start, int End)>();
        var start = values[0];
        var previous = start;

        foreach (var value in values.Skip(1))
        {
            if (value == previous + 1)
            {
                previous = value;
                continue;
            }

            ranges.Add((start, previous));
            start = previous = value;
        }

        ranges.Add((start, previous));
        return ranges;
    }

    private readonly record struct CourseBlockInfo(
        string Course, string Teacher, string Classroom, int? WeekStart, int? WeekEnd);

    private sealed record CellEntry(
        string Course, string Teacher, string Campus, string Room, List<int> Weeks, int Weekday, int Unit);
}
