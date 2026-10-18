using System.Text.Json.Nodes;

namespace Iverson.Sql;

/// <summary>
/// The one step both server-owned-column writers share: a payload key that names the column in
/// any letter case must go before the authoritative value is set, because the column stores
/// resolve names differently (Postgres's <c>json_populate_record</c> is case-sensitive, StarRocks is
/// not) and a surviving re-cased key would reach one of them.
/// </summary>
public static class JsonObjectKeys
{
    /// <summary>Removes every key of <paramref name="obj"/> equal to <paramref name="name"/> ignoring letter case.</summary>
    public static void RemoveIgnoringCase(JsonObject obj, string name)
    {
        foreach (var key in obj
                     .Select(kv => kv.Key)
                     .Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                     .ToList())
            obj.Remove(key);
    }
}
