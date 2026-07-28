using EduApi.Data.Models;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Tests.Services;

public class ScheduleTimeServiceTests
{
    private readonly ScheduleTimeService _service = new();

    [Fact]
    public async Task GetAllScheduleTablesAsync_ShouldReturnAllTables()
    {
        var result = await _service.GetAllScheduleTablesAsync();
        
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        
        var canTangTable = result.FirstOrDefault(t => t.Campus == Campus.CanTang);
        Assert.NotNull(canTangTable);
        
        var yanTaWinterTable = result.FirstOrDefault(t => t.Campus == Campus.YanTa && t.Season == Season.Winter);
        Assert.NotNull(yanTaWinterTable);
        
        var yanTaSummerTable = result.FirstOrDefault(t => t.Campus == Campus.YanTa && t.Season == Season.Summer);
        Assert.NotNull(yanTaSummerTable);
    }

    [Fact]
    public async Task GetScheduleTableAsync_CanTang_ShouldReturnTable()
    {
        var table = await _service.GetScheduleTableAsync(Campus.CanTang, Season.Summer);
        
        Assert.NotNull(table);
        Assert.Equal("草堂校区", table.CampusName);
        Assert.Equal(Campus.CanTang, table.Campus);
        Assert.Equal("通用", table.SeasonName);
        Assert.Equal(13, table.Periods.Count);
    }

    [Fact]
    public async Task GetScheduleTableAsync_YanTaWinter_ShouldReturnTable()
    {
        var table = await _service.GetScheduleTableAsync(Campus.YanTa, Season.Winter);
        
        Assert.NotNull(table);
        Assert.Equal("雁塔校区", table.CampusName);
        Assert.Equal(Campus.YanTa, table.Campus);
        Assert.Equal("冬季", table.SeasonName);
        Assert.Equal(Season.Winter, table.Season);
        Assert.Equal(13, table.Periods.Count);
    }

    [Fact]
    public async Task GetScheduleTableAsync_YanTaSummer_ShouldReturnTable()
    {
        var table = await _service.GetScheduleTableAsync(Campus.YanTa, Season.Summer);
        
        Assert.NotNull(table);
        Assert.Equal("雁塔校区", table.CampusName);
        Assert.Equal(Campus.YanTa, table.Campus);
        Assert.Equal("夏季", table.SeasonName);
        Assert.Equal(Season.Summer, table.Season);
        Assert.Equal(13, table.Periods.Count);
    }

    [Fact]
    public async Task CanTangPeriod0_IsEarlyStudy()
    {
        var period = await _service.GetPeriodAsync(Campus.CanTang, Season.Summer, 0);
        
        Assert.NotNull(period);
        Assert.Equal(0, period.UnitIndex);
        Assert.Equal("8:00", period.StartTime);
        Assert.Equal("8:20", period.EndTime);
        Assert.True(period.HasClass);
        Assert.True(period.IsEarlyStudy);
    }

    [Fact]
    public async Task CanTangPeriod1_ShouldReturnCorrectTime()
    {
        var period = await _service.GetPeriodAsync(Campus.CanTang, Season.Summer, 1);
        
        Assert.NotNull(period);
        Assert.Equal(1, period.UnitIndex);
        Assert.Equal("8:30", period.StartTime);
        Assert.Equal("9:15", period.EndTime);
        Assert.True(period.HasClass);
        Assert.False(period.IsEarlyStudy);
    }

    [Fact]
    public async Task YanTaWinterPeriod0_ShouldBeEmpty()
    {
        var period = await _service.GetPeriodAsync(Campus.YanTa, Season.Winter, 0);
        
        Assert.NotNull(period);
        Assert.Equal(0, period.UnitIndex);
        Assert.Equal("", period.StartTime);
        Assert.Equal("", period.EndTime);
        Assert.False(period.HasClass);
        Assert.False(period.IsEarlyStudy);
    }

    [Fact]
    public async Task YanTaWinterPeriod1_ShouldReturnCorrectTime()
    {
        var period = await _service.GetPeriodAsync(Campus.YanTa, Season.Winter, 1);
        
        Assert.NotNull(period);
        Assert.Equal(1, period.UnitIndex);
        Assert.Equal("8:00", period.StartTime);
        Assert.Equal("8:50", period.EndTime);
        Assert.True(period.HasClass);
    }

    [Fact]
    public async Task YanTaSummerPeriod7_ShouldReturnCorrectTime()
    {
        var period = await _service.GetPeriodAsync(Campus.YanTa, Season.Summer, 7);
        
        Assert.NotNull(period);
        Assert.Equal(7, period.UnitIndex);
        Assert.Equal("14:30", period.StartTime);
        Assert.Equal("15:20", period.EndTime);
        Assert.True(period.HasClass);
    }

    [Fact]
    public async Task YanTaWinterPeriod7_ShouldReturnCorrectTime()
    {
        var period = await _service.GetPeriodAsync(Campus.YanTa, Season.Winter, 7);
        
        Assert.NotNull(period);
        Assert.Equal(7, period.UnitIndex);
        Assert.Equal("14:00", period.StartTime);
        Assert.Equal("14:50", period.EndTime);
        Assert.True(period.HasClass);
    }

    [Fact]
    public async Task YanTaWinterEmptyPeriods_ShouldHaveNoClass()
    {
        var period5 = await _service.GetPeriodAsync(Campus.YanTa, Season.Winter, 5);
        var period6 = await _service.GetPeriodAsync(Campus.YanTa, Season.Winter, 6);
        
        Assert.False(period5.HasClass);
        Assert.False(period6.HasClass);
    }

    [Fact]
    public async Task GetPeriodAsync_InvalidUnitIndex_ShouldReturnNull()
    {
        var result = await _service.GetPeriodAsync(Campus.CanTang, Season.Summer, 99);
        Assert.Null(result);
        
        var negativeResult = await _service.GetPeriodAsync(Campus.CanTang, Season.Summer, -1);
        Assert.Null(negativeResult);
    }

    [Fact]
    public async Task GetScheduleTimeResponseAsync_ShouldReturnValidResponse()
    {
        var response = await _service.GetScheduleTimeResponseAsync();
        
        Assert.True(response.Success);
        Assert.NotNull(response.Data);
        Assert.Equal(3, response.Data.Count);
        Assert.True(response.CurrentDate != default);
        Assert.Contains("CanTang", response.Campuses);
        Assert.Contains("YanTa", response.Campuses);
        Assert.Contains("Winter", response.Seasons);
        Assert.Contains("Summer", response.Seasons);
    }

    [Fact]
    public async Task CanTangAllPeriods_ShouldHaveClass()
    {
        var table = await _service.GetScheduleTableAsync(Campus.CanTang, Season.Summer);
        
        foreach (var period in table.Periods)
        {
            Assert.True(period.HasClass, $"草堂校区第{period.UnitIndex}节应该有课程安排");
        }
    }

    [Fact]
    public async Task YanTaWinterValidPeriods_CountShouldBeCorrect()
    {
        var table = await _service.GetScheduleTableAsync(Campus.YanTa, Season.Winter);
        
        Assert.Equal(10, table.ValidPeriodCount);
    }

    [Fact]
    public async Task YanTaSummerValidPeriods_CountShouldBeCorrect()
    {
        var table = await _service.GetScheduleTableAsync(Campus.YanTa, Season.Summer);
        
        Assert.Equal(10, table.ValidPeriodCount);
    }
}