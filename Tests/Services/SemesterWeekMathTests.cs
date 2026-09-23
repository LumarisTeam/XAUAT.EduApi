using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Tests.Services;

/// <summary>
/// 「第 N 周 + 星期几 → 日期」的换算。
/// <para>
/// 用例里的 <c>(周次, 星期, 日期)</c> 三元组不是编的，取自真实上游样本
/// （LoginApi 抓的 <c>schedule-table-datum.json</c>，315 条记录里的去重日期三元组，
/// 不含任何个人信息）。这份样本是唯一能证明"建大的周从周日算起"的证据，
/// 所以把它固化在这里，防止有人按"周一起始"的直觉改坏公式。
/// </para>
/// </summary>
public class SemesterWeekMathTests
{
    /// <summary>由真实样本反解出的「第 1 周周首」：2026-08-30（周日）。</summary>
    private static readonly DateTime RealAnchor = new(2026, 8, 30);

    [Theory]
    // 真实样本里的日期三元组：第 1 周的周一到周六
    [InlineData(1, 1, "2026-08-31")]
    [InlineData(1, 2, "2026-09-01")]
    [InlineData(1, 3, "2026-09-02")]
    [InlineData(1, 4, "2026-09-03")]
    [InlineData(1, 5, "2026-09-04")]
    [InlineData(1, 6, "2026-09-05")]
    // 周日：落在该周的**第一天**（样本里 13 条周日记录都是这个规律）
    [InlineData(1, 7, "2026-08-30")]
    [InlineData(3, 7, "2026-09-13")]
    [InlineData(4, 7, "2026-09-20")]
    [InlineData(7, 7, "2026-10-11")]
    [InlineData(12, 7, "2026-11-15")]
    // 样本抓取日 2026-09-22 是周二，且样本里 print-data 的 currentWeek = 4
    [InlineData(4, 2, "2026-09-22")]
    public void GetDate_ShouldMatchRealUpstreamFixture(int weekIndex, int weekday, string expected)
    {
        Assert.Equal(DateTime.Parse(expected), SemesterWeekMath.GetDate(RealAnchor, weekIndex, weekday));
    }

    [Fact]
    public void GetWeekAnchor_ShouldNormalizeToSunday_WhenStartFallsMidWeek()
    {
        // 锚点容错 ±6 天：START 只要落在第 1 周那个周日..周六区间内，结果都一样
        foreach (var day in Enumerable.Range(30, 7))
        {
            var start = day <= 31 ? new DateTime(2026, 8, day) : new DateTime(2026, 9, day - 31);

            Assert.True(SemesterWeekMath.TryGetWeekAnchor(start.ToString("yyyy-MM-dd"), out var anchor));
            Assert.Equal(RealAnchor, anchor);
        }
    }

    [Fact]
    public void GetWeekAnchor_ShouldBeIdentity_WhenStartIsSunday()
    {
        // 缺省 START = "2026-03-01" 恰好是周日 → 归一化是恒等操作
        Assert.True(SemesterWeekMath.TryGetWeekAnchor("2026-03-01", out var anchor));
        Assert.Equal(new DateTime(2026, 3, 1), anchor);
        Assert.Equal(DayOfWeek.Sunday, anchor.DayOfWeek);
    }

    [Fact]
    public void GetWeekAnchor_ShouldNormalizeMondayBackToPreviousSunday()
    {
        Assert.True(SemesterWeekMath.TryGetWeekAnchor("2026-03-02", out var anchor));
        Assert.Equal(new DateTime(2026, 3, 1), anchor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-date")]
    public void GetWeekAnchor_ShouldFail_WhenStartMissingOrUnparsable(string? start)
    {
        // 失败要能被调用方看见：日历宁可不出课程事件，也不能把整学期的课算到错误日期上
        Assert.False(SemesterWeekMath.TryGetWeekAnchor(start, out _));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 8)]
    public void GetDate_ShouldReturnMinValue_WhenArgumentsOutOfRange(int weekIndex, int weekday)
    {
        // 单条坏数据只跳过这一条，不抛异常影响整张课表
        Assert.Equal(DateTime.MinValue, SemesterWeekMath.GetDate(RealAnchor, weekIndex, weekday));
    }

    [Fact]
    public void GetDate_ShouldBeContinuousAcrossMonthAndYearBoundaries()
    {
        // 中国无夏令时，跨月/跨年不该有任何跳变。
        // 注意周内的先后顺序是 周日→周一→…→周六（周日是这一周的第一天）
        // 从"第 1 周周日的**前一天**"起步，这样第一次比较也有意义
        var previous = RealAnchor.AddDays(-1);
        int[] weekdayOrder = [7, 1, 2, 3, 4, 5, 6];

        foreach (var week in Enumerable.Range(1, 26))
        {
            foreach (var weekday in weekdayOrder)
            {
                var date = SemesterWeekMath.GetDate(RealAnchor, week, weekday);
                Assert.True(date > previous, $"{date} 应晚于 {previous}");
                previous = date;
            }
        }
    }
}
