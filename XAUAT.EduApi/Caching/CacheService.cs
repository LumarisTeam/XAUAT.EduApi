using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using XAUAT.EduApi.Interfaces;

namespace XAUAT.EduApi.Caching;

/// <summary>
/// 缓存服务实现类
/// </summary>
public class CacheService : ICacheService, IDisposable
{
    private readonly LocalCacheManager _localCache;
    private readonly RedisCacheManager? _redisCache;
    private readonly CacheOptions _options;
    private readonly ILogger<CacheService> _logger;

    // 缓存预热相关
    private readonly ConcurrentBag<CacheWarmupItem> _warmupTasks;
    private readonly ActionBlock<CacheWarmupItem> _warmupBlock;
    private readonly List<Func<string, Task>> _expirationCallbacks;
    private readonly List<Action<int, int>> _warmupCompletedCallbacks;

    // 缓存策略映射
    private readonly ConcurrentDictionary<string, CacheStrategyType> _cacheStrategies;

    // 防缓存击穿：每个Key一个信号量，确保只有一个线程回源抓取
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new();

    // 监控服务（可选）
    private readonly IMonitoringService? _monitoringService;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="multiplexers">Redis连接多路复用器</param>
    /// <param name="options">缓存配置选项</param>
    /// <param name="logger">日志记录器</param>
    /// <param name="monitoringService">监控服务</param>
    public CacheService(
        IEnumerable<IConnectionMultiplexer> multiplexers,
        IOptions<CacheOptions> options,
        ILogger<CacheService> logger,
        IMonitoringService? monitoringService = null)
    {
        _options = options.Value;
        _logger = logger;
        _monitoringService = monitoringService;
        var connectionMultiplexer = multiplexers.FirstOrDefault();

        // 初始化本地缓存
        _localCache = new LocalCacheManager(_options);

        // 初始化Redis缓存（如果可用）
        _redisCache = connectionMultiplexer != null ? new RedisCacheManager(connectionMultiplexer, _options) : null;

        // 初始化预热相关组件
        _warmupTasks = new ConcurrentBag<CacheWarmupItem>();
        _expirationCallbacks = new List<Func<string, Task>>();
        _warmupCompletedCallbacks = new List<Action<int, int>>();

        // 创建预热任务处理块
        _warmupBlock = new ActionBlock<CacheWarmupItem>(
            async item => await ProcessWarmupItemAsync(item),
            new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                BoundedCapacity = 1000,
                EnsureOrdered = false
            });

        // 初始化缓存策略映射
        _cacheStrategies = new ConcurrentDictionary<string, CacheStrategyType>();

        _logger.LogInformation(
            "CacheService initialized with strategy: {StrategyType}, LocalCacheMaxSize: {LocalCacheMaxSize}",
            _options.StrategyType, _options.LocalCacheMaxSize);
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        T? result = default;

        try
        {
            // 1. 先从本地缓存获取
            result = _localCache.Get<T>(key);
            if (result != null)
            {
                _logger.LogTrace("Cache hit (Local): {Key}", key);
                _monitoringService?.RecordCacheHit("LocalCache");
                return result;
            }

            // 2. 本地缓存未命中，从Redis获取
            if (_redisCache is { IsAvailable: true })
            {
                result = await _redisCache.GetAsync<T>(key, cancellationToken);
                if (result != null)
                {
                    _logger.LogTrace("Cache hit (Redis): {Key}", key);
                    _monitoringService?.RecordCacheHit("RedisCache");

                    // 同步到本地缓存
                    await SetLocalCacheAsync(key, result, null, 5, cancellationToken);
                    return result;
                }
            }

            _logger.LogTrace("Cache miss: {Key}", key);
            _monitoringService?.RecordCacheMiss("CacheService");
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache item: {Key}", key);
            _monitoringService?.RecordCacheMiss("CacheService");
            return result;
        }
        finally
        {
            stopwatch.Stop();
            _monitoringService?.RecordOperationDuration("Cache.Get", stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    /// <inheritdoc />
    public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiration = null,
        CacheLevel level = CacheLevel.MultiLevel, int businessPriority = 5,
        CancellationToken cancellationToken = default, bool isUse = true)
    {
        if (!isUse)
        {
            return await factory();
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // 1. 快速路径：先从缓存获取
            var cachedValue = await GetAsync<T>(key, cancellationToken);
            if (cachedValue != null)
            {
                return cachedValue;
            }

            // 2. 获取或创建该Key的信号量，防止缓存击穿
            var slim = _keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await slim.WaitAsync(cancellationToken);
            try
            {
                // 3. 双重检查：获取锁后再次检查缓存
                cachedValue = await GetAsync<T>(key, cancellationToken);
                if (cachedValue != null)
                {
                    return cachedValue;
                }

                // 4. 缓存未命中，调用工厂方法（每个Key只有一个线程能到达这里）
                _logger.LogTrace("Cache miss, calling factory: {Key}", key);
                var newValue = await factory();

                // 5. 将新值存入缓存
                await SetAsync(key, newValue, expiration, level, businessPriority, cancellationToken);

                return newValue;
            }
            finally
            {
                slim.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetOrCreateAsync: {Key}", key);
            // 缓存操作失败时降级：直接调用工厂方法获取数据（不重试已失败的工厂）
            throw;
        }
        finally
        {
            stopwatch.Stop();
            _monitoringService?.RecordOperationDuration("Cache.GetOrCreate", stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    /// <inheritdoc />
    public async Task<bool> SetAsync<T>(string key, T? value, TimeSpan? expiration = null,
        CacheLevel level = CacheLevel.MultiLevel, int businessPriority = 5,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var success = true;

        try
        {
            var actualExpiration = expiration ?? _options.DefaultExpiration;

            // 1. 根据缓存级别设置缓存
            switch (level)
            {
                case CacheLevel.Local:
                    // 只设置本地缓存
                    _localCache.Set(key, value, actualExpiration, businessPriority);
                    _logger.LogTrace("Set cache (Local): {Key}, Expiration: {Expiration}", key, actualExpiration);
                    break;

                case CacheLevel.Distributed:
                    // 只设置分布式缓存（Redis）
                    if (_redisCache is { IsAvailable: true })
                    {
                        success = await _redisCache.SetAsync(key, value, actualExpiration, businessPriority,
                            cancellationToken);
                        if (success)
                        {
                            _logger.LogTrace("Set cache (Redis): {Key}, Expiration: {Expiration}", key,
                                actualExpiration);
                        }
                    }

                    break;

                case CacheLevel.MultiLevel:
                    // 同时设置本地和分布式缓存
                    _localCache.Set(key, value, actualExpiration, businessPriority);
                    _logger.LogTrace("Set cache (Local): {Key}, Expiration: {Expiration}", key, actualExpiration);

                    if (_redisCache is { IsAvailable: true })
                    {
                        success = await _redisCache.SetAsync(key, value, actualExpiration, businessPriority,
                            cancellationToken);
                        if (success)
                        {
                            _logger.LogTrace("Set cache (Redis): {Key}, Expiration: {Expiration}", key,
                                actualExpiration);
                        }
                    }

                    break;
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting cache item: {Key}", key);
            return false;
        }
        finally
        {
            stopwatch.Stop();
            _monitoringService?.RecordOperationDuration("Cache.Set", stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(string key, CacheLevel level = CacheLevel.MultiLevel,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var success = true;

        try
        {
            // 根据缓存级别移除缓存
            switch (level)
            {
                case CacheLevel.Local:
                    // 只移除本地缓存
                    success = _localCache.Remove(key);
                    break;

                case CacheLevel.Distributed:
                    // 只移除分布式缓存
                    if (_redisCache is { IsAvailable: true })
                    {
                        success = await _redisCache.RemoveAsync(key, cancellationToken);
                    }

                    break;

                case CacheLevel.MultiLevel:
                    // 同时移除本地和分布式缓存
                    success = _localCache.Remove(key);
                    if (_redisCache is { IsAvailable: true })
                    {
                        success &= await _redisCache.RemoveAsync(key, cancellationToken);
                    }

                    break;
            }

            _logger.LogTrace("Remove cache: {Key}, Level: {Level}, Success: {Success}", key, level, success);
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing cache item: {Key}", key);
            return false;
        }
        finally
        {
            stopwatch.Stop();
            _monitoringService?.RecordOperationDuration("Cache.Remove", stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string key, CacheLevel level = CacheLevel.MultiLevel,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // 根据缓存级别检查缓存
            switch (level)
            {
                case CacheLevel.Local:
                    return _localCache.Exists(key);

                case CacheLevel.Distributed:
                    return _redisCache is { IsAvailable: true } &&
                           await _redisCache.ExistsAsync(key, cancellationToken);

                case CacheLevel.MultiLevel:
                    // 先检查本地缓存，再检查分布式缓存
                    return _localCache.Exists(key) ||
                           (_redisCache is { IsAvailable: true } &&
                            await _redisCache.ExistsAsync(key, cancellationToken));

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking cache existence: {Key}", key);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ClearAsync(CacheLevel level = CacheLevel.MultiLevel,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var success = true;

        try
        {
            // 根据缓存级别清除缓存
            switch (level)
            {
                case CacheLevel.Local:
                    _localCache.Clear();
                    break;

                case CacheLevel.Distributed:
                    if (_redisCache is { IsAvailable: true })
                    {
                        success = await _redisCache.ClearAsync(cancellationToken);
                    }

                    break;

                case CacheLevel.MultiLevel:
                    _localCache.Clear();
                    if (_redisCache is { IsAvailable: true })
                    {
                        success &= await _redisCache.ClearAsync(cancellationToken);
                    }

                    break;
            }

            _logger.LogInformation("Clear cache: Level = {Level}, Success = {Success}", level, success);
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing cache: Level = {Level}", level);
            return false;
        }
        finally
        {
            stopwatch.Stop();
            _monitoringService?.RecordOperationDuration("Cache.Clear", stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, T?>> GetManyAsync<T>(IEnumerable<string> keys,
        CacheLevel level = CacheLevel.MultiLevel, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new Dictionary<string, T?>();

        try
        {
            var keyArray = keys.ToArray();

            // 1. 先从本地缓存批量获取
            foreach (var key in keyArray)
            {
                var value = _localCache.Get<T>(key);
                if (value != null)
                {
                    result[key] = value;
                    _monitoringService?.RecordCacheHit("LocalCache");
                }
            }

            // 2. 从Redis获取剩余的键
            var remainingKeys = keyArray.Where(key => !result.ContainsKey(key)).ToArray();
            if (remainingKeys.Any() && _redisCache is { IsAvailable: true })
            {
                var redisResult = await _redisCache.GetManyAsync<T>(remainingKeys, cancellationToken);

                var syncTasks = new List<Task>();
                foreach (var (key, value) in redisResult)
                {
                    if (value != null)
                    {
                        result[key] = value;
                        _monitoringService?.RecordCacheHit("RedisCache");

                        // 同步到本地缓存
                        syncTasks.Add(SetLocalCacheAsync(key, value, null, 5, cancellationToken));
                    }
                }

                await Task.WhenAll(syncTasks);
            }

            // 3. 记录未命中
            foreach (var key in keyArray.Where(key => !result.ContainsKey(key)))
            {
                _logger.LogInformation("Cache miss: {Key}", key);
                _monitoringService?.RecordCacheMiss("CacheService");
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting multiple cache items");
            return result;
        }
        finally
        {
            stopwatch.Stop();
            _monitoringService?.RecordOperationDuration("Cache.GetMany", stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    /// <inheritdoc />
    public async Task<bool> SetManyAsync<T>(IDictionary<string, T?> items, TimeSpan? expiration = null,
        CacheLevel level = CacheLevel.MultiLevel, int businessPriority = 5,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var success = true;

        try
        {
            // 根据缓存级别设置缓存
            switch (level)
            {
                case CacheLevel.Local:
                    // 批量设置本地缓存
                    foreach (var (key, value) in items)
                    {
                        _localCache.Set(key, value, expiration, businessPriority);
                    }

                    break;

                case CacheLevel.Distributed:
                    // 批量设置分布式缓存
                    if (_redisCache is { IsAvailable: true })
                    {
                        success = await _redisCache.SetManyAsync(items, expiration, businessPriority,
                            cancellationToken);
                    }

                    break;

                case CacheLevel.MultiLevel:
                    // 同时设置本地和分布式缓存
                    foreach (var (key, value) in items)
                    {
                        _localCache.Set(key, value, expiration, businessPriority);
                    }

                    if (_redisCache is { IsAvailable: true })
                    {
                        success &= await _redisCache.SetManyAsync(items, expiration, businessPriority,
                            cancellationToken);
                    }

                    break;
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting multiple cache items");
            return false;
        }
        finally
        {
            stopwatch.Stop();
            _monitoringService?.RecordOperationDuration("Cache.SetMany", stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemoveManyAsync(IEnumerable<string> keys,
        CacheLevel level = CacheLevel.MultiLevel, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var success = true;

        try
        {
            var keyArray = keys.ToArray();

            // 根据缓存级别移除缓存
            switch (level)
            {
                case CacheLevel.Local:
                    // 批量移除本地缓存
                    foreach (var key in keyArray)
                    {
                        _localCache.Remove(key);
                    }

                    break;

                case CacheLevel.Distributed:
                    // 批量移除分布式缓存
                    if (_redisCache is { IsAvailable: true })
                    {
                        success = await _redisCache.RemoveManyAsync(keyArray, cancellationToken);
                    }

                    break;

                case CacheLevel.MultiLevel:
                    // 同时移除本地和分布式缓存
                    foreach (var key in keyArray)
                    {
                        _localCache.Remove(key);
                    }

                    if (_redisCache is { IsAvailable: true })
                    {
                        success &= await _redisCache.RemoveManyAsync(keyArray, cancellationToken);
                    }

                    break;
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing multiple cache items");
            return false;
        }
        finally
        {
            stopwatch.Stop();
            _monitoringService?.RecordOperationDuration("Cache.RemoveMany", stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    /// <inheritdoc />
    public async Task<CacheStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var localStats = _localCache.GetStatistics();
            var redisStats = _redisCache?.GetStatistics();

            var stats = new CacheStatistics
            {
                TotalItems = localStats.TotalItems +
                             (redisStats?.IsAvailable == true ? await GetRedisItemCountAsync(cancellationToken) : 0),
                HitCount = localStats.HitCount + (redisStats?.HitCount ?? 0),
                MissCount = localStats.MissCount + (redisStats?.MissCount ?? 0),
                MemorySize = localStats.MemorySize,
                ExpiredItems = localStats.ExpiredItems + (redisStats?.IsAvailable == true
                    ? await GetRedisExpiredCountAsync()
                    : 0),
                LevelStatistics = new Dictionary<CacheLevel, long>
                {
                    { CacheLevel.Local, localStats.TotalItems },
                    {
                        CacheLevel.Distributed,
                        redisStats?.IsAvailable == true ? await GetRedisItemCountAsync(cancellationToken) : 0
                    },
                    {
                        CacheLevel.MultiLevel,
                        localStats.TotalItems + (redisStats?.IsAvailable == true
                            ? await GetRedisItemCountAsync(cancellationToken)
                            : 0)
                    }
                }
            };

            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache statistics");
            return new CacheStatistics();
        }
    }

    /// <inheritdoc />
    public async Task<bool> RefreshAsync(string key, TimeSpan? newExpiration = null,
        CacheLevel level = CacheLevel.MultiLevel, CancellationToken cancellationToken = default)
    {
        try
        {
            // 获取当前值
            var value = await GetAsync<object>(key, cancellationToken);
            if (value == null)
            {
                return false;
            }

            // 重新设置缓存，更新过期时间
            return await SetAsync(key, value, newExpiration, level, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing cache item: {Key}", key);
            return false;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> SearchKeysAsync(string pattern,
        CacheLevel level = CacheLevel.MultiLevel,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 1. 搜索本地缓存键
        if (level == CacheLevel.Local || level == CacheLevel.MultiLevel)
        {
            await foreach (var key in _localCache.SearchKeysAsync(pattern, cancellationToken))
            {
                yield return key;
            }
        }

        // 2. 搜索Redis缓存键
        if ((level == CacheLevel.Distributed || level == CacheLevel.MultiLevel) &&
            _redisCache is { IsAvailable: true })
        {
            await foreach (var key in _redisCache.SearchKeysAsync(pattern, cancellationToken))
            {
                yield return key;
            }
        }
    }

    #region 缓存预热相关实现

    /// <inheritdoc />
    public void AddWarmupTask(CacheWarmupItem warmupItem)
    {
        _warmupTasks.Add(warmupItem);
        _logger.LogInformation("Added warmup task: {Key}", warmupItem.Key);
    }

    /// <inheritdoc />
    public async Task<int> ExecuteWarmupAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var totalTasks = _warmupTasks.Count;
        var completedTasks = 0;

        try
        {
            _logger.LogInformation("Starting cache warmup, total tasks: {TotalTasks}", totalTasks);

            // 按优先级排序
            var sortedTasks = _warmupTasks.OrderByDescending(t => t.Priority).ToList();

            // 使用并发执行预热任务，设置合理的并发度防止请求过载
            await Parallel.ForEachAsync(sortedTasks, new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount * 2,
                CancellationToken = cancellationToken
            }, async (task, ct) =>
            {
                if (await ProcessWarmupItemAsync(task))
                {
                    Interlocked.Increment(ref completedTasks);
                }
            });

            _logger.LogInformation("Cache warmup completed, success: {CompletedTasks}/{TotalTasks}, elapsed: {Elapsed}",
                completedTasks, totalTasks, stopwatch.Elapsed);

            // 触发预热完成回调
            foreach (var callback in _warmupCompletedCallbacks)
            {
                try
                {
                    callback(completedTasks, totalTasks);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error executing warmup completed callback");
                }
            }

            return completedTasks;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Cache warmup canceled");
            return completedTasks;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing cache warmup");
            return completedTasks;
        }
        finally
        {
            stopwatch.Stop();
        }
    }

    /// <inheritdoc />
    public async Task<int> ExecuteIncrementalWarmupAsync(string[]? businessTags = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var completedTasks = 0;

        try
        {
            // 筛选符合标签的任务
            var filteredTasks = _warmupTasks.Where(task =>
                businessTags == null || businessTags.Length == 0 ||
                task.BusinessTags.Any(tag => businessTags.Contains(tag))).ToList();

            _logger.LogInformation("Starting incremental cache warmup, tasks: {TaskCount}", filteredTasks.Count);

            var tasksToRun = filteredTasks.OrderByDescending(t => t.Priority).ToList();

            // 使用并发执行预热任务
            await Parallel.ForEachAsync(tasksToRun, new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount * 2,
                CancellationToken = cancellationToken
            }, async (task, ct) =>
            {
                if (await ProcessWarmupItemAsync(task))
                {
                    Interlocked.Increment(ref completedTasks);
                }
            });

            _logger.LogInformation("Incremental cache warmup completed, success: {CompletedTasks}/{TotalTasks}",
                completedTasks, filteredTasks.Count);

            return completedTasks;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Incremental cache warmup canceled");
            return completedTasks;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing incremental cache warmup");
            return completedTasks;
        }
        finally
        {
            stopwatch.Stop();
        }
    }

    /// <summary>
    /// 处理单个预热任务
    /// </summary>
    /// <param name="warmupItem">预热任务项</param>
    /// <returns>是否处理成功</returns>
    private async Task<bool> ProcessWarmupItemAsync(CacheWarmupItem warmupItem)
    {
        try
        {
            _logger.LogTrace("Processing warmup task: {Key}", warmupItem.Key);

            // 调用工厂方法获取值
            var value = await warmupItem.ValueFactory();

            // 存入多级缓存
            await SetAsync(warmupItem.Key, value, warmupItem.Expiration, CacheLevel.MultiLevel, warmupItem.Priority);

            _logger.LogDebug("Warmup task completed: {Key}", warmupItem.Key);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing warmup task: {Key}", warmupItem.Key);
            return false;
        }
    }

    #endregion

    #region 缓存策略相关实现

    /// <inheritdoc />
    public Task<bool> SetStrategyAsync(string key, CacheStrategyType strategyType,
        TimeSpan? expiration = null, CancellationToken cancellationToken = default)
    {
        try
        {
            _cacheStrategies[key] = strategyType;

            // 如果设置了过期时间，创建过期回调
            if (expiration.HasValue)
            {
                _ = Task.Delay(expiration.Value, cancellationToken).ContinueWith(async t =>
                {
                    if (!t.IsCanceled)
                    {
                        _cacheStrategies.TryRemove(key, out _);
                        await RemoveAsync(key, cancellationToken: cancellationToken);
                    }
                }, cancellationToken);
            }

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting cache strategy: {Key}", key);
            return Task.FromResult(false);
        }
    }

    /// <inheritdoc />
    public Task<CacheStrategyType?> GetStrategyAsync(string key, CancellationToken cancellationToken = default)
    {
        _cacheStrategies.TryGetValue(key, out var strategyType);
        return Task.FromResult((CacheStrategyType?)strategyType);
    }

    #endregion

    #region 缓存监控相关实现

    /// <inheritdoc />
    public void RegisterExpirationCallback(string key, Func<string, Task> callback)
    {
        _expirationCallbacks.Add(callback);
        _logger.LogDebug("Registered expiration callback for key: {Key}", key);
    }

    /// <inheritdoc />
    public void RegisterWarmupCompletedCallback(Action<int, int> callback)
    {
        _warmupCompletedCallbacks.Add(callback);
        _logger.LogDebug("Registered warmup completed callback");
    }

    #endregion

    #region 私有辅助方法

    /// <summary>
    /// 设置本地缓存（异步包装）
    /// </summary>
    private async Task SetLocalCacheAsync<T>(string key, T? value, TimeSpan? expiration, int businessPriority,
        CancellationToken cancellationToken)
    {
        // 本地缓存操作是同步的，这里包装为异步方法
        await Task.Run(() => { _localCache.Set(key, value, expiration, businessPriority); }, cancellationToken);
    }

    /// <summary>
    /// 获取Redis缓存项数量
    /// </summary>
    private async Task<long> GetRedisItemCountAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_redisCache == null || !_redisCache.IsAvailable)
            {
                return 0;
            }

            // 简单实现，实际可以通过Redis INFO命令获取更准确的统计信息
            var count = 0L;
            await foreach (var _ in SearchKeysAsync("*", CacheLevel.Distributed, cancellationToken))
            {
                count++;
            }

            return count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Redis item count");
            return 0;
        }
    }

    /// <summary>
    /// 获取Redis过期项数量（模拟实现）
    /// </summary>
    private Task<long> GetRedisExpiredCountAsync()
    {
        // Redis不直接支持获取过期项数量，这里返回0作为占位
        return Task.FromResult(0L);
    }

    #endregion

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        _warmupBlock.Complete();
        _warmupBlock.Completion.Wait(TimeSpan.FromSeconds(5));
    }
}
