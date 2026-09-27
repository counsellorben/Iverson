using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Iverson.Patterns.Tests.Oracle;

public sealed record TrinoResult(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, string? Error, string? ErrorType);

/// <summary>Minimal client for Trino's REST protocol: POST /v1/statement, then follow nextUri.</summary>
public sealed class TrinoClient(Uri baseUri) : IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = baseUri };

    public async Task<TrinoResult> QueryAsync(string sql)
    {
        using var post = new HttpRequestMessage(HttpMethod.Post, "/v1/statement") { Content = new StringContent(sql, Encoding.UTF8) };
        post.Headers.Add("X-Trino-User", "oracle");
        var page = await Send(post);

        var columns = new List<string>();
        var types = new List<string>();
        var rows = new List<object?[]>();
        while (true)
        {
            if (columns.Count == 0 && page.TryGetProperty("columns", out var cols))
                foreach (var c in cols.EnumerateArray())
                {
                    columns.Add(c.GetProperty("name").GetString()!);
                    types.Add(c.GetProperty("type").GetString()!);
                }
            if (page.TryGetProperty("data", out var data))
                foreach (var row in data.EnumerateArray())
                    rows.Add(row.EnumerateArray().Select((v, i) => Read(v, types[i])).ToArray());
            if (page.TryGetProperty("error", out var error))
                return new TrinoResult(columns, rows, error.GetProperty("message").GetString(), error.GetProperty("errorType").GetString());
            if (!page.TryGetProperty("nextUri", out var next))
                return new TrinoResult(columns, rows, null, null);

            using var get = new HttpRequestMessage(HttpMethod.Get, next.GetString());
            get.Headers.Add("X-Trino-User", "oracle");
            page = await Send(get);
        }
    }

    private async Task<JsonElement> Send(HttpRequestMessage request)
    {
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }

    private static object? Read(JsonElement value, string type)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (type is "double" or "real")
            return value.ValueKind == JsonValueKind.String
                ? double.Parse(value.GetString()!, CultureInfo.InvariantCulture)   // "NaN", "Infinity"
                : value.GetDouble();
        if (type is "bigint" or "integer" or "smallint" or "tinyint") return value.GetInt64();
        if (type == "boolean") return value.GetBoolean();
        if (type.StartsWith("varchar", StringComparison.Ordinal)) return value.GetString();
        throw new NotSupportedException($"The oracle does not compare Trino type {type}.");
    }

    public void Dispose() => _http.Dispose();
}
