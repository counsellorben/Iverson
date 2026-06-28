using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Iverson.Client.Core;

internal static class StructConverter
{
    private static readonly JsonFormatter _formatter =
        new(JsonFormatter.Settings.Default.WithFormatDefaultValues(false));

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented               = false
    };

    /// <summary>Serializes a POCO to a protobuf Struct via JSON round-trip.</summary>
    public static Struct ToStruct<T>(T obj) where T : class
    {
        var json = JsonSerializer.Serialize(obj, _jsonOpts);
        return JsonParser.Default.Parse<Struct>(json);
    }

    /// <summary>Deserializes a protobuf Struct back to a POCO via JSON round-trip.</summary>
    public static T? FromStruct<T>(Struct? data)
    {
        if (data is null) return default;
        var json = _formatter.Format(data);
        return JsonSerializer.Deserialize<T>(json, _jsonOpts);
    }

    /// <summary>Extracts a string field from a Struct by key.</summary>
    public static string? GetString(Struct data, string key) =>
        data.Fields.TryGetValue(key, out var v) ? v.StringValue : null;

    /// <summary>Extracts a repeated-string field (e.g. join key ids) from a Struct.</summary>
    public static IReadOnlyList<string> GetStringList(Struct data, string key)
    {
        if (!data.Fields.TryGetValue(key, out var v) || v.ListValue is null)
            return [];
        return v.ListValue.Values.Select(x => x.StringValue).ToList();
    }
}
