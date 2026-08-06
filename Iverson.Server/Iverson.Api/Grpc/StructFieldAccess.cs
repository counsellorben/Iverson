using Google.Protobuf.WellKnownTypes;

namespace Iverson.Api.Grpc;

// Client payloads use PascalCase or camelCase field names interchangeably — every read
// here tries the canonical name first, then its camelCase form, so callers only
// know one field name per property.
internal static class StructFieldAccess
{
    public static IEnumerable<string> Candidates(string name)
    {
        yield return name;
        if (name.Length > 0)
            yield return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static Value? GetFieldValue(Struct s, string fieldName)
    {
        foreach (var name in Candidates(fieldName))
            if (s.Fields.TryGetValue(name, out var v))
                return v;
        return null;
    }

    public static string? GetFieldString(Struct s, string fieldName)
    {
        foreach (var name in Candidates(fieldName))
            if (s.Fields.TryGetValue(name, out var v))
                return v.StringValue;
        return null;
    }

    public static IReadOnlyList<string> GetFieldStringList(Struct s, string fieldName)
    {
        foreach (var name in Candidates(fieldName))
            if (s.Fields.TryGetValue(name, out var v) && v.ListValue is not null)
                return v.ListValue.Values
                    .Select(x => x.StringValue)
                    .Where(x => !string.IsNullOrEmpty(x))
                    .ToList();
        return [];
    }

    /// <summary>
    /// Sets <paramref name="canonicalName"/>, first removing any key that differs from it only by
    /// the leading character's case. Clients serialize camelCase, so a payload arrives carrying
    /// <c>authorId</c> while the schema column is <c>AuthorId</c>; setting the canonical key
    /// without removing the camelCase one leaves BOTH in the Struct, and
    /// <c>StructSerializer.SerializePayload</c> upper-firsts every key into a Dictionary —
    /// throwing "An item with the same key has already been added."
    /// </summary>
    public static void SetField(Struct s, string canonicalName, Value value)
    {
        foreach (var candidate in Candidates(canonicalName))
            if (candidate != canonicalName)
                s.Fields.Remove(candidate);

        s.Fields[canonicalName] = value;
    }
}
