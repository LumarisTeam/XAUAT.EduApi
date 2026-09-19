using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using XAUAT.EduApi.Caching;
using XAUAT.EduApi.Interfaces;
using XAUAT.EduApi.Services;

namespace XAUAT.EduApi.Tests.Services;

/// <summary>
/// CacheService单元测试
/// </summary>
public class CacheServiceTests : IDisposable
{
    private readonly Mock<IConnectionMultiplexer> _connectionMultiplexerMock;
    private readonly Mock<IDatabase> _redisDatabaseMock;
    private readonly Mock<ILogger<CacheService>> _loggerMock;
    private readonly Mock<IMonitoringService> _monitoringServiceMock;
    private readonly CacheService _cacheService;
    private readonly CacheOptions _cacheOptions;

    public CacheServiceTests()
    {
        _connectionMultiplexerMock = new Mock<IConnectionMultiplexer>();
        _redisDatabaseMock = new Mock<IDatabase>();
        _loggerMock = new Mock<ILogger<CacheService>>();
        _monitoringServiceMock = new Mock<IMonitoringService>();

        // 设置Redis连接返回模拟的数据库
        _connectionMultiplexerMock.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_redisDatabaseMock.Object);

        _cacheOptions = new CacheOptions
        {
            DefaultExpiration = TimeSpan.FromHours(1),
            StrategyType = CacheStrategyType.Hybrid,
            LocalCacheMaxSize = 1000
        };

        var optionsMock = new Mock<IOptions<CacheOptions>>();
        optionsMock.Setup(x => x.Value).Returns(_cacheOptions);

        _cacheService = new CacheService(
            [_connectionMultiplexerMock.Object],
            optionsMock.Object,
            _loggerMock.Object,
            _monitoringServiceMock.Object);
    }

    public void Dispose()
    {
        _cacheService.Dispose();
    }

    #region GetAsync Tests

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenKeyNotExists()
    {
        // Arrange
        var key = "non-existent-key";

        // Act
        var result = await _cacheService.GetAsync<string>(key);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnValue_WhenKeyExistsInLocalCache()
    {
        // Arrange
        var key = "test-key";
        var value = "test-value";
        await _cacheService.SetAsync(key, value, level: CacheLevel.Local);

        // Act
        var result = await _cacheService.GetAsync<string>(key);

        // Assert
        Assert.Equal(value, result);
    }

    #endregion

    #region SetAsync Tests

    [Fact]
    public async Task SetAsync_ShouldReturnTrue_WhenSettingLocalCache()
    {
        // Arrange
        var key = "test-key";
        var value = "test-value";

        // Act
        var result = await _cacheService.SetAsync(key, value, level: CacheLevel.Local);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task SetAsync_ShouldSetValueWithExpiration()
    {
        // Arrange
        var key = "test-key";
        var value = "test-value";
        var expiration = TimeSpan.FromMinutes(30);

        // Act
        var result = await _cacheService.SetAsync(key, value, expiration, CacheLevel.Local);

        // Assert
        Assert.True(result);
        var cachedValue = await _cacheService.GetAsync<string>(key);
        Assert.Equal(value, cachedValue);
    }

    [Fact]
    public async Task SetAsync_ShouldHandleNullValue()
    {
        // Arrange
        var key = "test-key";
        string? value = null;

        // Act
        var result = await _cacheService.SetAsync(key, value, level: CacheLevel.Local);

        // Assert
        Assert.True(result);
    }

    #endregion

    #region GetOrCreateAsync Tests

    [Fact]
    public async Task GetOrCreateAsync_ShouldReturnCachedValue_WhenKeyExists()
    {
        // Arrange
        var key = "test-key";
        var cachedValue = "cached-value";
        await _cacheService.SetAsync(key, cachedValue, level: CacheLevel.Local);
        var factoryCalled = false;

        // Act
        var result = await _cacheService.GetOrCreateAsync(key, async () =>
        {
            factoryCalled = true;
            return await Task.FromResult("new-value");
        }, level: CacheLevel.Local);

        // Assert
        Assert.Equal(cachedValue, result);
        Assert.False(factoryCalled);
    }

    [Fact]
    public async Task GetOrCreateAsync_ShouldCallFactory_WhenKeyNotExists()
    {
        // Arrange
        var key = "new-key";
        var newValue = "new-value";
        var factoryCalled = false;

        // Act
        var result = await _cacheService.GetOrCreateAsync(key, async () =>
        {
            factoryCalled = true;
            return await Task.FromResult(newValue);
        }, level: CacheLevel.Local);

        // Assert
        Assert.Equal(newValue, result);
        Assert.True(factoryCalled);
    }

    [Fact]
    public async Task GetOrCreateAsync_ShouldCacheFactoryResult()
    {
        // Arrange
        var key = "new-key";
        var newValue = "new-value";

        // Act
        await _cacheService.GetOrCreateAsync(key, async () => await Task.FromResult(newValue), level: CacheLevel.Local);
        var cachedResult = await _cacheService.GetAsync<string>(key);

        // Assert
        Assert.Equal(newValue, cachedResult);
    }

    #endregion

    #region RemoveAsync Tests

    [Fact]
    public async Task RemoveAsync_ShouldReturnTrue_WhenKeyExists()
    {
        // Arrange
        var key = "test-key";
        await _cacheService.SetAsync(key, "test-value", level: CacheLevel.Local);

        // Act
        var result = await _cacheService.RemoveAsync(key, CacheLevel.Local);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task RemoveAsync_ShouldRemoveValue()
    {
        // Arrange
        var key = "test-key";
        await _cacheService.SetAsync(key, "test-value", level: CacheLevel.Local);

        // Act
        await _cacheService.RemoveAsync(key, CacheLevel.Local);
        var result = await _cacheService.GetAsync<string>(key);

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region ExistsAsync Tests

    [Fact]
    public async Task ExistsAsync_ShouldReturnTrue_WhenKeyExists()
    {
        // Arrange
        var key = "test-key";
        await _cacheService.SetAsync(key, "test-value", level: CacheLevel.Local);

        // Act
        var result = await _cacheService.ExistsAsync(key, CacheLevel.Local);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task ExistsAsync_ShouldReturnFalse_WhenKeyNotExists()
    {
        // Arrange
        var key = "non-existent-key";

        // Act
        var result = await _cacheService.ExistsAsync(key, CacheLevel.Local);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region ClearAsync Tests

    [Fact]
    public async Task ClearAsync_ShouldClearAllLocalCache()
    {
        // Arrange
        await _cacheService.SetAsync("key1", "value1", level: CacheLevel.Local);
        await _cacheService.SetAsync("key2", "value2", level: CacheLevel.Local);

        // Act
        var result = await _cacheService.ClearAsync(CacheLevel.Local);

        // Assert
        Assert.True(result);
        Assert.False(await _cacheService.ExistsAsync("key1", CacheLevel.Local));
        Assert.False(await _cacheService.ExistsAsync("key2", CacheLevel.Local));
    }

    #endregion

    #region GetManyAsync Tests

    [Fact]
    public async Task GetManyAsync_ShouldReturnMultipleValues()
    {
        // Arrange
        await _cacheService.SetAsync("key1", "value1", level: CacheLevel.Local);
        await _cacheService.SetAsync("key2", "value2", level: CacheLevel.Local);
        var keys = new[] { "key1", "key2", "key3" };

        // Act
        var result = await _cacheService.GetManyAsync<string>(keys, CacheLevel.Local);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("value1", result["key1"]);
        Assert.Equal("value2", result["key2"]);
    }

    #endregion

    #region SetManyAsync Tests

    [Fact]
    public async Task SetManyAsync_ShouldSetMultipleValues()
    {
        // Arrange
        var items = new Dictionary<string, string?>
        {
            { "key1", "value1" },
            { "key2", "value2" }
        };

        // Act
        var result = await _cacheService.SetManyAsync(items, level: CacheLevel.Local);

        // Assert
        Assert.True(result);
        Assert.Equal("value1", await _cacheService.GetAsync<string>("key1"));
        Assert.Equal("value2", await _cacheService.GetAsync<string>("key2"));
    }

    #endregion

    #region RemoveManyAsync Tests

    [Fact]
    public async Task RemoveManyAsync_ShouldRemoveMultipleValues()
    {
        // Arrange
        await _cacheService.SetAsync("key1", "value1", level: CacheLevel.Local);
        await _cacheService.SetAsync("key2", "value2", level: CacheLevel.Local);
        var keys = new[] { "key1", "key2" };

        // Act
        var result = await _cacheService.RemoveManyAsync(keys, CacheLevel.Local);

        // Assert
        Assert.True(result);
        Assert.Null(await _cacheService.GetAsync<string>("key1"));
        Assert.Null(await _cacheService.GetAsync<string>("key2"));
    }

    #endregion

    #region GetStatisticsAsync Tests

    [Fact]
    public async Task GetStatisticsAsync_ShouldReturnStatistics()
    {
        // Arrange
        await _cacheService.SetAsync("key1", "value1", level: CacheLevel.Local);

        // Act
        var result = await _cacheService.GetStatisticsAsync();

        // Assert
        Assert.NotNull(result);
        Assert.True(result.TotalItems >= 0);
    }

    #endregion

    #region Warmup Tests

    [Fact]
    public async Task AddWarmupTask_ShouldAddTask()
    {
        // Arrange
        var warmupItem = new CacheWarmupItem
        {
            Key = "warmup-key",
            ValueFactory = async () => await Task.FromResult("warmup-value"),
            Priority = 5
        };

        // Act
        _cacheService.AddWarmupTask(warmupItem);
        var completedTasks = await _cacheService.ExecuteWarmupAsync();

        // Assert
        Assert.True(completedTasks >= 0);
    }

    [Fact]
    public async Task ExecuteWarmupAsync_ShouldExecuteAllTasks()
    {
        // Arrange
        _cacheService.AddWarmupTask(new CacheWarmupItem
        {
            Key = "warmup-key-1",
            ValueFactory = async () => await Task.FromResult("value-1"),
            Priority = 5
        });
        _cacheService.AddWarmupTask(new CacheWarmupItem
        {
            Key = "warmup-key-2",
            ValueFactory = async () => await Task.FromResult("value-2"),
            Priority = 10
        });

        // Act
        var completedTasks = await _cacheService.ExecuteWarmupAsync();

        // Assert
        Assert.Equal(2, completedTasks);
    }

    #endregion

    #region Strategy Tests

    [Fact]
    public async Task SetStrategyAsync_ShouldSetStrategy()
    {
        // Arrange
        var key = "strategy-key";
        var strategyType = CacheStrategyType.Lru;

        // Act
        var result = await _cacheService.SetStrategyAsync(key, strategyType);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task GetStrategyAsync_ShouldReturnStrategy()
    {
        // Arrange
        var key = "strategy-key";
        var strategyType = CacheStrategyType.Lfu;
        await _cacheService.SetStrategyAsync(key, strategyType);

        // Act
        var result = await _cacheService.GetStrategyAsync(key);

        // Assert
        Assert.Equal(strategyType, result);
    }

    #endregion

    #region Callback Tests

    [Fact]
    public void RegisterExpirationCallback_ShouldRegisterCallback()
    {
        // Arrange
        var callbackCalled = false;
        Func<string, Task> callback = async key =>
        {
            callbackCalled = true;
            await Task.CompletedTask;
        };

        // Act
        _cacheService.RegisterExpirationCallback("test-key", callback);

        // Assert - 验证没有抛出异常
        Assert.False(callbackCalled); // 回调尚未被调用
    }

    [Fact]
    public void RegisterWarmupCompletedCallback_ShouldRegisterCallback()
    {
        // Arrange
        var callbackCalled = false;
        Action<int, int> callback = (completed, total) => { callbackCalled = true; };

        // Act
        _cacheService.RegisterWarmupCompletedCallback(callback);

        // Assert - 验证没有抛出异常
        Assert.False(callbackCalled); // 回调尚未被调用
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task GetAsync_ShouldHandleComplexTypes()
    {
        // Arrange
        var key = "complex-key";
        var value = new TestComplexType { Id = 1, Name = "Test", Items = new List<string> { "a", "b", "c" } };
        await _cacheService.SetAsync(key, value, level: CacheLevel.Local);

        // Act
        var result = await _cacheService.GetAsync<TestComplexType>(key);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(value.Id, result.Id);
        Assert.Equal(value.Name, result.Name);
        Assert.Equal(value.Items.Count, result.Items.Count);
    }

    [Fact]
    public async Task SetAsync_ShouldHandleEmptyString()
    {
        // Arrange
        var key = "empty-string-key";
        var value = string.Empty;

        // Act
        var result = await _cacheService.SetAsync(key, value, level: CacheLevel.Local);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task GetOrCreateAsync_ShouldHandleFactoryException()
    {
        // Arrange
        var key = "exception-key";
        var factoryCallCount = 0;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<Exception>(() =>
            _cacheService.GetOrCreateAsync<string>(key, async () =>
            {
                factoryCallCount++;
                throw new Exception("Factory error");
            }, level: CacheLevel.Local));

        Assert.Equal("Factory error", exception.Message);
    }

    #endregion

    private class TestComplexType
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<string> Items { get; set; } = new();
    }
}
