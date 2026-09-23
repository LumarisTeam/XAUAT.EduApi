using System.Globalization;
using EduApi.Data.Models;
using XAUAT.EduApi.Interfaces;

namespace XAUAT.EduApi.Services;

/// <summary>
/// ICS 日历订阅编排。移植自 XAUAT.LoginApi 的 <c>CalendarService</c>，
/// 但**课程与考试数据改为复用本服务自己的 Service**。
/// <para>
/// 三条必须守住的兼容性约束（都是从 Flask 一路继承下来的既成事实）：
/// </para>
/// <list type="number">
/// <item>事件顺序与 Flask 一致：<b>先考试、后课程</b>。</item>
/// <item>UID 逐字保持（<c>exam-{课程}-{起始时间}</c> / <c>course-{lessonId}-{起始时间}</c>），
/// 否则已订阅的用户会在日历里看到重复事件。</item>
/// <item>写进 ICS 的一律是<b>校本部本地时间</b>（Asia/Shanghai 墙上时间）。考试时刻经
/// <see cref="ExamService.ParseExamTimeRange"/> 拿到手就已经是本地时间，<b>不要</b>换成
/// <see cref="ExamRecord.ExamTime"/> 那个 UTC 值。</item>
/// </list>
/// </summary>
public class CalendarService(
    ILoginService loginService,
    ICourseService courseService,
    IExamService examService,
    IInfoService infoService,
    IClassTimeService classTimeService,
    ILogger<CalendarService> logger) : ICalendarService
{
    private const string CalendarName = "课程表";
    private const string AppleColor = "#540EB9";

    /// <summary>「课程表」里课程事件的提醒提前量（分钟）。</summary>
    private const int CourseAlarmMinutes = 15;

    /// <summary>考试事件的提醒提前量（分钟）。</summary>
    private const int ExamAlarmMinutes = 30;

    public async Task<byte[]> GenerateAsync(
        string username, string password, string? filter, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("开始生成日历，学号: {Username}", username);

        // 登录。测试账号在 HttpLoginService 内部就已短路（返回伪造 cookie 与固定学号），
        // 后续的 CourseService/ExamService 也会各自识别测试账号并改走 fixtures，
        // 所以这里不需要为测试账号写任何分支。
        var login = await loginService.LoginAsync(username, password);

        var coursesTask = courseService.GetCoursesAsync(login.StudentId, login.Cookie);
        var examsTask = examService.GetExamArrangementsAsync(login.Cookie, login.StudentId);

        await Task.WhenAll(coursesTask, examsTask);

        var courses = await coursesTask;
        var exams = await examsTask;

        var onlyFuture = string.Equals(filter, "future", StringComparison.Ordinal);

        var examEvents = BuildExamEvents(exams.Exams, onlyFuture);
        var courseEvents = BuildCourseEvents(courses, onlyFuture);

        var events = new List<IcsEvent>(examEvents.Count + courseEvents.Count);
        events.AddRange(examEvents);
        events.AddRange(courseEvents);

        var content = IcsCalendarWriter.Create(events, CalendarName, AppleColor, DateTimeOffset.UtcNow);

        logger.LogInformation("日历生成完成: 考试 {ExamCount} 场 → {ExamEventCount} 个事件; 课程 {CourseCount} 门 → {CourseEventCount} 个事件",
            exams.Exams.Count, examEvents.Count, courses.Count, courseEvents.Count);

        return content;
    }

    private List<IcsEvent> BuildExamEvents(List<ExamInfo> exams, bool onlyFuture)
    {
        var events = new List<IcsEvent>(exams.Count);

        foreach (var exam in exams)
        {
            var (start, end) = ExamService.ParseExamTimeRange(exam.Time);

            // 时间解析不出来的条目不进日历（单条坏数据不该影响整份日历）
            if (start == DateTime.MinValue)
            {
                logger.LogWarning("跳过时间无法解析的考试: {Name} / {Time}", exam.Name, exam.Time);
                continue;
            }

            if (onlyFuture && end <= SchoolClock.Now) continue;

            events.Add(new IcsEvent(
                Uid: $"exam-{exam.Name}-{IsoFormat(start)}",
                Start: start,
                End: end,
                Summary: $"{exam.Name}考试",
                Description: $"考试时间: {exam.Time}",
                Location: $"教室: {exam.Location} 座位号: {exam.Seat}",
                AlarmDescription: $"{exam.Name}考试即将开始！",
                AlarmMinutesBefore: ExamAlarmMinutes));
        }

        return events;
    }

    private List<IcsEvent> BuildCourseEvents(List<CourseActivity> courses, bool onlyFuture)
    {
        var events = new List<IcsEvent>();

        if (!SemesterWeekMath.TryGetWeekAnchor(infoService.GetTime().StartTime, out var anchor))
        {
            // 没有可信的学期起始日就不生成课程事件：宁可日历里没有课，
            // 也不能把整学期的课算到错误的日期上（考试仍照常生成）。
            logger.LogWarning("无法解析学期起始日（START 环境变量），本次不生成课程事件");
            return events;
        }

        foreach (var course in courses)
        {
            var teacher = course.Teachers.FirstOrDefault() ?? "";

            foreach (var weekIndex in course.WeekIndexes)
            {
                var date = SemesterWeekMath.GetDate(anchor, weekIndex, course.Weekday);
                if (date == DateTime.MinValue) continue;

                var campus = ResolveCampus(course);

                // 作息表按**事件当天**选季节：一个学期横跨两个季节，用"今天"选表会算错另一半
                var startText = classTimeService.GetStartTime(campus, course.StartUnit, date);
                var endText = classTimeService.GetEndTime(campus, course.EndUnit, date);

                // 空串表示该节次本来就没课（如雁塔第 0/5/6 节），跳过
                if (!TryCombine(date, startText, out var start) ||
                    !TryCombine(date, endText, out var end))
                {
                    continue;
                }

                if (onlyFuture && end <= SchoolClock.Now) continue;

                events.Add(new IcsEvent(
                    Uid: $"course-{course.LessonId}-{IsoFormat(start)}",
                    Start: start,
                    End: end,
                    Summary: course.CourseName,
                    Description: teacher,
                    Location: course.Room,
                    AlarmDescription: $"{course.CourseName}课程在{course.Room}即将开始！",
                    AlarmMinutesBefore: CourseAlarmMinutes));
            }
        }

        // 上游返回的顺序是按课程的，这里按时间排一遍，让输出稳定（便于比对与测试）。
        // 日历客户端自己会按 DTSTART 排序，所以这不改变用户看到的结果。
        events.Sort(static (left, right) =>
        {
            var byStart = left.Start.CompareTo(right.Start);
            return byStart != 0 ? byStart : string.CompareOrdinal(left.Uid, right.Uid);
        });

        return events;
    }

    /// <summary>
    /// 判定课程属于哪个校区，规则与客户端 <c>TimeService.resolveCampusName</c> 一致：
    /// <b>非草堂即雁塔</b>——校区的权威依据是<b>教室名</b>（教务下发的校区字段可能不准，
    /// 客户端就是靠"教室以草堂开头"来兜底的）。
    /// </summary>
    /// <remarks>
    /// 不能把原始 <c>Campus</c> 直接透传给 <see cref="IClassTimeService"/>：它认不出校区时
    /// 默认回草堂，而客户端默认是雁塔，两边会算出不同的作息（草堂第 1 节 08:30、雁塔 08:00）。
    /// </remarks>
    private static string ResolveCampus(CourseActivity course)
    {
        var isCaoTang = course.Campus.Contains("草堂", StringComparison.Ordinal) ||
                        course.Room.StartsWith("草堂", StringComparison.Ordinal);

        return isCaoTang ? "草堂" : "雁塔";
    }

    /// <summary>把日期与 <c>HH:mm</c> 拼成本地时间；任一段为空或格式不对就返回 false。</summary>
    private static bool TryCombine(DateTime date, string time, out DateTime value)
    {
        value = default;

        if (string.IsNullOrEmpty(time) ||
            !TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        value = date.Date.Add(parsed.ToTimeSpan());
        return true;
    }

    /// <summary>
    /// 复刻 Python 的 <c>datetime.isoformat()</c>：<c>2026-06-20T08:00:00</c>。
    /// 秒数为 0 时 Python **仍会写出来**，微秒为 0 时省略——这里保持一致，
    /// 否则 UID 会变，老订阅者会看到重复事件。
    /// </summary>
    private static string IsoFormat(DateTime value)
        => value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
}
