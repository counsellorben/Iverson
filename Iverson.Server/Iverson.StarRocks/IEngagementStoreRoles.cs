namespace Iverson.StarRocks;

public interface IEngagementStoreQueryExecutor
{
    Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null);
    Task<int> ExecuteAsync(string sql, object? param = null);
}

public interface IEngagementStoreSchemaManager
{
    Task ApplyTableAsync(StarRocksTableSchema schema);
}

public interface IEngagementStoreHealthCheck
{
    Task<StarRocksHealthStatus> CheckHealthAsync();
    Task<bool> IsHealthyAsync();
}

public interface IEngagementStoreEntityStore
{
    Task UpsertAsync(StarRocksTableSchema schema, string payloadJson);
    Task DeleteAsync(string tableName, string keyColumn, string keyValue);
}
