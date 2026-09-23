using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Tests.Services;

/// <summary>
/// 节次作息表。重点守两件事：雁塔夏/冬表要按**事件日期**选，以及 10 月属冬季
/// （与 <c>ScheduleTimeService</c> 下发的 <c>05/01~09/30</c> 区间、客户端
/// <c>ScheduleTable.coversDate</c> 三者一致）。
/// </summary>
public class ClassTimeServiceTests
{
    private readonly ClassTimeService _service = new();

    [Fact]
    public void GetStartTime_ShouldUseWinterTable_WhenDateIsOctober()
    {
        // 雁塔第 7 节：冬季 14:00，夏季 14:30。10/01 起属冬季。
        Assert.Equal("14:00", _service.GetStartTime("雁塔", 7, new DateTime(2026, 10, 1)));
        Assert.Equal("14:00", _service.GetStartTime("雁塔", 7, new DateTime(2026, 12, 20)));
    }

    [Fact]
    public void GetStartTime_ShouldUseSummerTable_WhenDateIsSeptember()
    {
        Assert.Equal("14:30", _service.GetStartTime("雁塔", 7, new DateTime(2026, 9, 30)));
        Assert.Equal("14:30", _service.GetStartTime("雁塔", 7, new DateTime(2026, 5, 1)));
    }

    [Fact]
    public void GetStartTime_ShouldPickSeasonByEventDate_NotToday()
    {
        // 同一个学期横跨两个季节：九月属夏季、十二月属冬季，两者必须给出不同时刻。
        // 这正是不能用 DateTime.Now 选表的原因。
        var summer = _service.GetStartTime("雁塔", 7, new DateTime(2026, 9, 15));
        var winter = _service.GetStartTime("雁塔", 7, new DateTime(2026, 12, 15));

        Assert.NotEqual(summer, winter);
    }

    [Fact]
    public void GetStartTime_ShouldReturnEmpty_WhenYantaUnitHasNoClass()
    {
        // 雁塔第 0/5/6 节空着（作息表里就是空串）
        Assert.Equal("", _service.GetStartTime("雁塔", 0, new DateTime(2026, 9, 15)));
        Assert.Equal("", _service.GetStartTime("雁塔", 5, new DateTime(2026, 9, 15)));
        Assert.Equal("", _service.GetStartTime("雁塔", 6, new DateTime(2026, 9, 15)));
        Assert.Equal("", _service.GetEndTime("雁塔", 5, new DateTime(2026, 9, 15)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(13)]
    [InlineData(99)]
    public void GetStartTime_ShouldReturnEmpty_WhenUnitOutOfRange(int unit)
    {
        Assert.Equal("", _service.GetStartTime("草堂", unit, new DateTime(2026, 9, 15)));
        Assert.Equal("", _service.GetEndTime("草堂", unit, new DateTime(2026, 9, 15)));
    }

    [Fact]
    public void GetStartTime_ShouldNotSplitCaotangBySeason()
    {
        // 草堂只有一套表，季节参数不该影响它
        var september = _service.GetStartTime("草堂", 1, new DateTime(2026, 9, 15));
        var december = _service.GetStartTime("草堂", 1, new DateTime(2026, 12, 15));

        Assert.Equal("08:30", september);
        Assert.Equal(september, december);
    }

    [Fact]
    public void TwoArgumentOverloads_ShouldStillWork()
    {
        // 无日期重载保留（等价于传 DateTime.Now），不能因为加了日期重载就退化
        Assert.Equal("08:30", _service.GetStartTime("草堂", 1));
        Assert.Equal("09:15", _service.GetEndTime("草堂", 1));
    }
}
