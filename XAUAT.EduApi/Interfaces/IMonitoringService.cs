namespace XAUAT.EduApi.Interfaces;

/// <summary>
/// 监控服务接口
/// 用于收集和暴露业务监控指标
/// </summary>
public interface IMonitoringService
{
    /// <summary>
    /// 记录API调用次数
    /// </summary>
    /// <param name="endpoint">API端点</param>
    /// <param name="method">HTTP方法</param>
    void RecordApiCall(string endpoint, string method);
    
    /// <summary>
    /// 记录数据库查询次数
    /// </summary>
    /// <param name="tableName">表名</param>
    void RecordDatabaseQuery(string tableName);
    
    /// <summary>
    /// 记录外部API调用次数
    /// </summary>
    /// <param name="apiName">API名称</param>
    void RecordExternalApiCall(string apiName);
    
    /// <summary>
    /// 记录缓存命中次数
    /// </summary>
    /// <param name="cacheName">缓存名称</param>
    void RecordCacheHit(string cacheName);
    
    /// <summary>
    /// 记录缓存未命中次数
    /// </summary>
    /// <param name="cacheName">缓存名称</param>
    void RecordCacheMiss(string cacheName);
    
    /// <summary>
    /// 记录业务处理时间
    /// </summary>
    /// <param name="operationName">操作名称</param>
    /// <param name="durationMs">持续时间（毫秒）</param>
    void RecordOperationDuration(string operationName, double durationMs);
    
    /// <summary>
    /// 记录API错误
    /// </summary>
    void RecordApiError();
    
    /// <summary>
    /// 记录API请求持续时间
    /// </summary>
    /// <param name="durationMs">持续时间（毫秒）</param>
    void RecordApiRequestDuration(double durationMs);
    
    /// <summary>
    /// 记录数据库错误
    /// </summary>
    void RecordDatabaseError();
    
    /// <summary>
    /// 记录数据库查询持续时间
    /// </summary>
    /// <param name="durationMs">持续时间（毫秒）</param>
    void RecordDatabaseQueryDuration(double durationMs);
    
    /// <summary>
    /// 记录外部API错误
    /// </summary>
    void RecordExternalApiError();
    
    /// <summary>
    /// 记录外部API持续时间
    /// </summary>
    /// <param name="durationMs">持续时间（毫秒）</param>
    void RecordExternalApiDuration(double durationMs);
    
    /// <summary>
    /// 记录缓存错误
    /// </summary>
    void RecordCacheError();
    
    /// <summary>
    /// 记录业务操作
    /// </summary>
    void RecordBusinessOperation();
    
    /// <summary>
    /// 记录业务错误
    /// </summary>
    void RecordBusinessError();
}
