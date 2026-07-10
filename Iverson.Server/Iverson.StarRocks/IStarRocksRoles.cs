namespace Iverson.StarRocks;

public interface IStarRocksQueryExecutor
{
    Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null);
    Task<int> ExecuteAsync(string sql, object? param = null);
}

public interface IStarRocksSchemaManager
{
    Task ApplyTableAsync(StarRocksTableSchema schema);
}

public interface IStarRocksHealthCheck
{
    Task<StarRocksHealthStatus> CheckHealthAsync();
    Task<bool> IsHealthyAsync();
}

public interface IStarRocksEntityStore
{
    Task UpsertAsync(StarRocksTableSchema schema, string payloadJson);
    Task DeleteAsync(string tableName, string keyColumn, string keyValue);
}
