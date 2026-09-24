using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Tests.Services;

/// <summary>
/// InfoService单元测试
/// </summary>
public class InfoServiceTests
{
    private readonly InfoService _infoService;
    
    /// <summary>
    /// 构造函数，初始化测试依赖
    /// </summary>
    public InfoServiceTests()
    {
        _infoService = new InfoService();
    }
    
    /// <summary>
    /// 测试GetTime方法，验证是否能正确获取时间范围
    /// </summary>
    [Fact]
    public void GetTime_ShouldReturnTimeModel_WhenCalled()
    {
        // Act
        var result = _infoService.GetTime();
        
        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.StartTime);
        Assert.NotNull(result.EndTime);
        Assert.NotEmpty(result.StartTime);
        Assert.NotEmpty(result.EndTime);
    }
    
    /// <summary>
    /// 测试GetTime方法，验证当环境变量存在时是否能正确获取环境变量中的值
    /// </summary>
    [Fact]
    public void GetTime_ShouldReturnEnvironmentValues_WhenEnvironmentVariablesExist()
    {
        // Arrange
        const string expectedStartTime = "2025-01-01";
        const string expectedEndTime = "2025-12-31";
        
        // 设置环境变量
        Environment.SetEnvironmentVariable("START", expectedStartTime, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("END", expectedEndTime, EnvironmentVariableTarget.Process);
        
        // Act
        var result = _infoService.GetTime();
        
        // Assert
        Assert.Equal(expectedStartTime, result.StartTime);
        Assert.Equal(expectedEndTime, result.EndTime);
        
        // 清理环境变量
        Environment.SetEnvironmentVariable("START", null, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("END", null, EnvironmentVariableTarget.Process);
    }
    
    /// <summary>
    /// 测试IsInSchool方法，验证当当前时间在时间范围内时是否返回true
    /// </summary>
    [Fact]
    public void IsInSchool_ShouldReturnTrue_WhenCurrentTimeIsInRange()
    {
        // Arrange
        const string startTime = "2000-01-01";
        const string endTime = "2100-12-31";
        
        // 设置环境变量
        Environment.SetEnvironmentVariable("START", startTime, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("END", endTime, EnvironmentVariableTarget.Process);
        
        // Act
        var result = _infoService.IsGreatThanStart();
        
        // Assert
        Assert.True(result);
        
        // 清理环境变量
        Environment.SetEnvironmentVariable("START", null, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("END", null, EnvironmentVariableTarget.Process);
    }
    
    /// <summary>
    /// 测试IsInSchool方法，验证当当前时间不在时间范围内时是否返回false
    /// </summary>
    [Fact]
    public void IsInSchool_ShouldReturnFalse_WhenCurrentTimeIsOutOfRange()
    {
        // Arrange
        const string startTime = "1900-01-01";
        const string endTime = "1999-12-31";
        
        // 设置环境变量
        Environment.SetEnvironmentVariable("START", startTime, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("END", endTime, EnvironmentVariableTarget.Process);
        
        // Act
        var result = _infoService.IsInSchool();
        
        // Assert
        Assert.False(result);
        
        // 清理环境变量
        Environment.SetEnvironmentVariable("START", null, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("END", null, EnvironmentVariableTarget.Process);
    }
    
    /// <summary>
    /// 测试GetTime方法，验证当环境变量为空字符串时是否使用默认值
    /// </summary>
    [Fact]
    public void GetTime_ShouldReturnDefaultValues_WhenEnvironmentVariablesAreEmpty()
    {
        // Arrange
        const string emptyString = "";
        
        // 设置环境变量为空字符串
        Environment.SetEnvironmentVariable("START", emptyString, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("END", emptyString, EnvironmentVariableTarget.Process);
        
        // Act
        var result = _infoService.GetTime();
        
        // Assert
        Assert.Equal(emptyString, result.StartTime);
        Assert.Equal(emptyString, result.EndTime);
        
        // 清理环境变量
        Environment.SetEnvironmentVariable("START", null, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("END", null, EnvironmentVariableTarget.Process);
    }
    
    /// <summary>
    /// 测试IsInSchool方法，验证当环境变量为无效日期格式时是否抛出异常
    /// </summary>
    [Fact]
    public void IsInSchool_ShouldThrowFormatException_WhenEnvironmentVariablesAreInvalidDate()
    {
        // Arrange
        const string invalidDate = "invalid-date";
        
        // 设置环境变量为无效日期格式
        Environment.SetEnvironmentVariable("START", invalidDate, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("END", invalidDate, EnvironmentVariableTarget.Process);
        
        // Act & Assert
        Assert.Throws<FormatException>(() => _infoService.IsGreatThanStart());
        
        // 清理环境变量
        Environment.SetEnvironmentVariable("START", null, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("END", null, EnvironmentVariableTarget.Process);
    }
    
    /// <summary>
    /// 测试IsInSchool方法，验证当当前时间正好等于开始时间时是否返回true
    /// </summary>
    [Fact]
    public void IsInSchool_ShouldReturnTrue_WhenCurrentTimeEqualsStartTime()
    {
        // Arrange
        var now = DateTime.Now;
        var startTime = now.ToString("yyyy-MM-dd");
        var endTime = now.AddDays(1).ToString("yyyy-MM-dd");
        
        // 设置环境变量
        Environment.SetEnvironmentVariable("START", startTime, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("END", endTime, EnvironmentVariableTarget.Process);
        
        // Act
        var result = _infoService.IsGreatThanStart();
        
        // Assert
        Assert.True(result);
        
        // 清理环境变量
        Environment.SetEnvironmentVariable("START", null, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("END", null, EnvironmentVariableTarget.Process);
    }
    
    /// <summary>
    /// 测试IsInSchool方法，验证当当前时间正好等于结束时间时是否返回true
    /// </summary>
    [Fact]
    public void IsInSchool_ShouldReturnTrue_WhenCurrentTimeEqualsEndTime()
    {
        // Arrange
        var now = DateTime.Now;
        var startTime = now.AddDays(-1).ToString("yyyy-MM-dd");
        var endTime = now.ToString("yyyy-MM-dd");
        
        // 设置环境变量
        Environment.SetEnvironmentVariable("START", startTime, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("END", endTime, EnvironmentVariableTarget.Process);
        
        // Act
        var result = _infoService.IsGreatThanStart();
        
        // Assert
        Assert.True(result);
        
        // 清理环境变量
        Environment.SetEnvironmentVariable("START", null, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("END", null, EnvironmentVariableTarget.Process);
    }
    
    /// <summary>
    /// 测试GetTime方法，验证当环境变量为极端早日期时是否能正确处理
    /// </summary>
    [Fact]
    public void GetTime_ShouldHandleExtremeEarlyDate()
    {
        // Arrange
        const string extremeEarlyDate = "0001-01-01";
        
        // 设置环境变量
        Environment.SetEnvironmentVariable("START", extremeEarlyDate, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("END", extremeEarlyDate, EnvironmentVariableTarget.Process);
        
        // Act
        var result = _infoService.GetTime();
        
        // Assert
        Assert.Equal(extremeEarlyDate, result.StartTime);
        Assert.Equal(extremeEarlyDate, result.EndTime);
        
        // 清理环境变量
        Environment.SetEnvironmentVariable("START", null, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("END", null, EnvironmentVariableTarget.Process);
    }
    
    /// <summary>
    /// 测试GetTime方法，验证当环境变量为极端晚日期时是否能正确处理
    /// </summary>
    [Fact]
    public void GetTime_ShouldHandleExtremeLateDate()
    {
        // Arrange
        const string extremeLateDate = "9999-12-31";
        
        // 设置环境变量
        Environment.SetEnvironmentVariable("START", extremeLateDate, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("END", extremeLateDate, EnvironmentVariableTarget.Process);
        
        // Act
        var result = _infoService.GetTime();
        
        // Assert
        Assert.Equal(extremeLateDate, result.StartTime);
        Assert.Equal(extremeLateDate, result.EndTime);
        
        // 清理环境变量
        Environment.SetEnvironmentVariable("START", null, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("END", null, EnvironmentVariableTarget.Process);
    }
    
    /// <summary>
    /// 测试GetTime方法，验证当环境变量为特殊字符时是否能正确处理
    /// </summary>
    [Fact]
    public void GetTime_ShouldHandleSpecialCharacters()
    {
        // Arrange
        const string specialCharacters = "!@#$%^&*()_+[]{}|;:,.<>?/~`";
        
        // 设置环境变量
        Environment.SetEnvironmentVariable("START", specialCharacters, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("END", specialCharacters, EnvironmentVariableTarget.Process);
        
        // Act
        var result = _infoService.GetTime();
        
        // Assert
        Assert.Equal(specialCharacters, result.StartTime);
        Assert.Equal(specialCharacters, result.EndTime);
        
        // 清理环境变量
        Environment.SetEnvironmentVariable("START", null, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("END", null, EnvironmentVariableTarget.Process);
    }
    
    /// <summary>
    /// 测试GetTime方法，验证当环境变量为非常长的字符串时是否能正确处理
    /// </summary>
    [Fact]
    public void GetTime_ShouldHandleVeryLongStrings()
    {
        // Arrange
        var veryLongString = new string('a', 1000);
        
        // 设置环境变量
        Environment.SetEnvironmentVariable("START", veryLongString, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("END", veryLongString, EnvironmentVariableTarget.Process);
        
        // Act
        var result = _infoService.GetTime();
        
        // Assert
        Assert.Equal(veryLongString, result.StartTime);
        Assert.Equal(veryLongString, result.EndTime);
        
        // 清理环境变量
        Environment.SetEnvironmentVariable("START", null, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("END", null, EnvironmentVariableTarget.Process);
    }
    
    /// <summary>
    /// 测试IsInSchool方法，验证当开始时间晚于结束时间时是否返回false
    /// </summary>
    [Fact]
    public void IsInSchool_ShouldReturnFalse_WhenStartTimeIsAfterEndTime()
    {
        // Arrange
        const string startTime = "2026-01-25";
        const string endTime = "2025-08-30";
        
        // 设置环境变量
        Environment.SetEnvironmentVariable("START", startTime, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("END", endTime, EnvironmentVariableTarget.Process);
        
        // Act
        var result = _infoService.IsInSchool();
        
        // Assert
        Assert.False(result);
        
        // 清理环境变量
        Environment.SetEnvironmentVariable("START", null, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("END", null, EnvironmentVariableTarget.Process);
    }
}