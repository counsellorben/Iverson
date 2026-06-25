namespace Iverson.StarRocks;

public interface IStarRocksRepository
{
    Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null);
    Task<int> ExecuteAsync(string sql, object? param = null);
    Task UpsertAsync(StarRocksTableSchema schema, string payloadJson);
    Task DeleteAsync(string tableName, string keyColumn, string keyValue);
    Task ApplyTableAsync(StarRocksTableSchema schema);
    Task<bool> IsHealthyAsync();
}
