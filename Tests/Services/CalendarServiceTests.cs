using System.Text;
using EduApi.Data.Models;
using Microsoft.Extensions.Logging;
using Moq;
using XAUAT.EduApi.Exceptions;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Tests.Services;

/// <summary>
/// 日历编排。重点守 UID 格式（老订户的去重依据）、事件顺序、以及"时间一律用
/// 校本部本地墙钟"这条最容易写错的约定。
/// </summary>
public class CalendarServiceTests
{
    private readonly Mock<ILoginService> _loginMock = new();
    private readonly Mock<ICourseService> _courseMock = new();
    private readonly Mock<IExamService> _examMock = new();
    private readonly Mock<IInfoService> _infoMock = new();

    private CalendarService CreateService()
        => new(
            _loginMock.Object,
            _courseMock.Object,
            _examMock.Object,
            _infoMock.Object,
            new ClassTimeService(),
            Mock.Of<ILogger<CalendarService>>());

    /// <summary>
    /// 让 <see cref="IInfoService.GetTime"/> 返回指定的学期起始日（即 START 环境变量）。
    /// </summary>
    private void SetSemesterStart(DateTime start)
        => _infoMock.Setup(x => x.GetTime())
            .Returns(new TimeModel { StartTime = start.ToString("yyyy-MM-dd"), EndTime = start.AddMonths(4).ToString("yyyy-MM-dd") });

    private void SetLogin()
        => _loginMock.Setup(x => x.LoginAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new LoginResponse { Success = true, StudentId = "2024001", Cookie = "__pstsid__=x;" });

    private void SetExams(params ExamInfo[] exams)
        => _examMock.Setup(x => x.GetExamArrangementsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(new ExamResponse { Exams = [.. exams], CanClick = exams.Length > 0 });

    private void SetCourses(params CourseActivity[] courses)
        => _courseMock.Setup(x => x.GetCoursesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync([.. courses]);

    private static CourseActivity Course(
        int weekday = 1, int startUnit = 1, int endUnit = 2, params int[] weeks)
        => new()
        {
            LessonId = "lesson-test-101",
            CourseName = "测试数据结构",
            Teachers = ["张老师"],
            Campus = "雁塔",
            Room = "教学楼A101",
            Weekday = weekday,
            StartUnit = startUnit,
            EndUnit = endUnit,
            WeekIndexes = [.. weeks]
        };

    private static string ExamTime(DateTime date, string range = "09:00-11:00")
        => $"{date:yyyy-MM-dd} {range}";

    private static async Task<string> GenerateAsync(CalendarService service, string? filter = null)
        => Encoding.UTF8.GetString(await service.GenerateAsync("2024001", "pw", filter));

    [Fact]
    public async Task GenerateAsync_ShouldEmitExamBeforeCourse()
    {
        // 顺序契约：先考试、后课程（与 Flask 一路继承下来）
        SetLogin();
        SetSemesterStart(new DateTime(2026, 3, 1));
        SetCourses(Course(weeks: 1));
        SetExams(new ExamInfo { Name = "测试软件工程", Time = ExamTime(new DateTime(2026, 6, 22), "14:00-16:00"), Location = "教学楼B202", Seat = "12" });

        var ics = await GenerateAsync(CreateService());

        Assert.True(ics.IndexOf("UID:exam-", StringComparison.Ordinal)
                    < ics.IndexOf("UID:course-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateAsync_ShouldKeepUidFormatIdenticalToLoginApi()
    {
        // UID 变了，老订户的日历里就会出现重复事件（旧 UID 不会自己消失）。
        // 课程起始时刻必须是**本地墙钟**：2026-03-02（周一）雁塔第 1 节 08:00。
        SetLogin();
        SetSemesterStart(new DateTime(2026, 3, 1));
        SetCourses(Course(weeks: 1));
        SetExams(new ExamInfo { Name = "测试软件工程", Time = ExamTime(new DateTime(2026, 6, 22), "14:00-16:00"), Location = "教学楼B202", Seat = "12" });

        var ics = await GenerateAsync(CreateService());

        Assert.Contains("UID:course-lesson-test-101-2026-03-02T08:00:00", ics, StringComparison.Ordinal);
        Assert.Contains("UID:exam-测试软件工程-2026-06-22T14:00:00", ics, StringComparison.Ordinal);
        Assert.Contains("DTSTART;TZID=Asia/Shanghai:20260302T080000", ics, StringComparison.Ordinal);
        Assert.Contains("DTEND;TZID=Asia/Shanghai:20260302T095000", ics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_ShouldEmitLegacyEventText()
    {
        SetLogin();
        SetSemesterStart(new DateTime(2026, 3, 1));
        SetCourses(Course(weeks: 1));
        SetExams(new ExamInfo { Name = "测试软件工程", Time = ExamTime(new DateTime(2026, 6, 22), "14:00-16:00"), Location = "教学楼B202", Seat = "12" });

        var ics = await GenerateAsync(CreateService());

        Assert.Contains("SUMMARY:测试数据结构", ics, StringComparison.Ordinal);
        Assert.Contains("DESCRIPTION:张老师", ics, StringComparison.Ordinal);
        Assert.Contains("LOCATION:教学楼A101", ics, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:测试软件工程考试", ics, StringComparison.Ordinal);
        Assert.Contains("LOCATION:教室: 教学楼B202 座位号: 12", ics, StringComparison.Ordinal);
        Assert.Contains("TRIGGER:-PT15M", ics, StringComparison.Ordinal);
        Assert.Contains("TRIGGER:-PT30M", ics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_ShouldDropPastEvents_WhenFilterIsFuture()
    {
        var pastStart = SchoolClock.Now.AddDays(-70);
        SetLogin();
        SetSemesterStart(pastStart);
        SetCourses(Course(weeks: 1));
        SetExams(new ExamInfo { Name = "旧考试", Time = ExamTime(SchoolClock.Now.AddDays(-30)), Location = "A", Seat = "1" });

        var ics = await GenerateAsync(CreateService(), filter: "future");

        Assert.DoesNotContain("BEGIN:VEVENT", ics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_ShouldKeepEvents_WhenFilterIsNotFuture()
    {
        var pastStart = SchoolClock.Now.AddDays(-70);
        SetLogin();
        SetSemesterStart(pastStart);
        SetCourses(Course(weeks: 1));
        SetExams(new ExamInfo { Name = "旧考试", Time = ExamTime(SchoolClock.Now.AddDays(-30)), Location = "A", Seat = "1" });

        var ics = await GenerateAsync(CreateService());

        Assert.Equal(2, CountOccurrences(ics, "BEGIN:VEVENT"));
    }

    [Fact]
    public async Task GenerateAsync_ShouldKeepFutureEvents_WhenFilterIsFuture()
    {
        SetLogin();
        SetSemesterStart(SchoolClock.Now.AddDays(30));
        SetCourses(Course(weeks: 1));
        SetExams(new ExamInfo { Name = "未来考试", Time = ExamTime(SchoolClock.Now.AddDays(40)), Location = "A", Seat = "1" });

        var ics = await GenerateAsync(CreateService(), filter: "future");

        Assert.Equal(2, CountOccurrences(ics, "BEGIN:VEVENT"));
    }

    [Fact]
    public async Task GenerateAsync_ShouldSkipCourseEvent_WhenUnitHasNoTime()
    {
        // 雁塔第 5 节在作息表里是空串（本来就没课）
        SetLogin();
        SetSemesterStart(new DateTime(2026, 3, 1));
        SetCourses(Course(startUnit: 5, endUnit: 5, weeks: 1));
        SetExams();

        var ics = await GenerateAsync(CreateService());

        Assert.DoesNotContain("UID:course-", ics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_ShouldEmitOneEventPerWeek()
    {
        SetLogin();
        SetSemesterStart(new DateTime(2026, 3, 1));
        SetCourses(Course(weeks: [1, 2, 3]));
        SetExams();

        var ics = await GenerateAsync(CreateService());

        Assert.Equal(3, CountOccurrences(ics, "UID:course-"));
    }

    [Fact]
    public async Task GenerateAsync_ShouldStillEmitExams_WhenSemesterStartIsUnparsable()
    {
        // 学期起始日不可信时宁可不出课程事件，也不能把整学期的课算到错误日期上
        SetLogin();
        _infoMock.Setup(x => x.GetTime()).Returns(new TimeModel { StartTime = "", EndTime = "" });
        SetCourses(Course(weeks: 1));
        SetExams(new ExamInfo { Name = "测试软件工程", Time = ExamTime(new DateTime(2026, 6, 22), "14:00-16:00"), Location = "B202", Seat = "12" });

        var ics = await GenerateAsync(CreateService());

        Assert.DoesNotContain("UID:course-", ics, StringComparison.Ordinal);
        Assert.Contains("UID:exam-测试软件工程-2026-06-22T14:00:00", ics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_ShouldThrow_WhenLoginFails()
    {
        _loginMock.Setup(x => x.LoginAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new LoginFailedException("用户名或密码错误"));

        await Assert.ThrowsAsync<LoginFailedException>(
            () => CreateService().GenerateAsync("2024001", "bad", null));
    }

    [Fact]
    public async Task GenerateAsync_ShouldPassStudentIdFromLoginToBothServices()
    {
        SetLogin();
        SetSemesterStart(new DateTime(2026, 3, 1));
        SetCourses(Course(weeks: 1));
        SetExams();

        await GenerateAsync(CreateService());

        _courseMock.Verify(x => x.GetCoursesAsync("2024001", "__pstsid__=x;", It.IsAny<string>()), Times.Once);
        _examMock.Verify(x => x.GetExamArrangementsAsync("__pstsid__=x;", "2024001", It.IsAny<string>(), It.IsAny<IEnumerable<string>?>()), Times.Once);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
