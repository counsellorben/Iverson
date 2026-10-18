using System.Runtime.CompilerServices;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Schema;

namespace Iverson.Api.Grpc;

/// <summary>
/// CSR round 12 #2. Renames each top-level payload key that names a column, ignoring case, to the
/// column's registered spelling. The four client write RPCs run it first: before the key is read
/// and before <see cref="AuthorizationFieldMasking.EnforceWriteAuthorization"/>. Every later step
/// that finds a column by name then sees one spelling: the key read, the owner checks, the size
/// guard, the serializer, and Postgres' case-sensitive <c>json_populate_record</c>.
/// <para>
/// Two things went wrong without it. A re-cased owner key (<c>OWNERID</c>) passed the owner checks,
/// which look a name up exactly or lower-first, and StarRocks, which resolves column names
/// case-insensitively, then wrote it into the owner column. And the .NET client's camelCase policy
/// folds a leading acronym: <c>URLPath</c> is sent as <c>urlPath</c>, which reached Postgres as
/// <c>UrlPath</c> and was stored as NULL.
/// </para>
/// <para>
/// Two cases are refused with InvalidArgument: two keys that name the same column, and a key that
/// names more than one column (on a type stored before case-duplicate columns were refused at
/// registration). Keys that name no column, navigation properties among them, are left alone. The
/// server-owned tenant column is never a target, so the smuggling check refuses a re-cased copy of
/// it under the caller's own spelling.
/// </para>
/// </summary>
internal static class PayloadKeyCanonicalizer
{
    // Keyed on the descriptor instance, as DecayFieldResolver's cache is: SchemaRegistry replaces a
    // type's instance wholesale when it reloads or re-registers it, so a recased column is a new key.
    // The map is a function of the instance's column names alone and is only read after it is built.
    private static readonly ConditionalWeakTable<SchemaDescriptor, Dictionary<string, List<string>>> ColumnSpellings = new();

    internal static void Canonicalize(SchemaDescriptor schema, Struct payload)
    {
        var columns = ColumnSpellings.GetValue(schema, SchemaBuilder.ColumnSpellingsByName);

        var keyFor = new Dictionary<string, string>(StringComparer.Ordinal);
        var renames = new List<(string From, string To)>();
        foreach (var key in payload.Fields.Keys)
        {
            if (!columns.TryGetValue(key, out var spellings)) continue;

            if (spellings.Count > 1)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Payload field '{key}' on '{schema.TypeName}' matches more than one column " +
                    $"({string.Join(", ", spellings.Select(s => $"'{s}'"))}), whose names differ only by " +
                    "letter case. Re-register the type with property names that are distinct ignoring case."));
            }

            var column = spellings[0];
            if (keyFor.TryGetValue(column, out var other))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Payload fields '{other}' and '{key}' on '{schema.TypeName}' both name column '{column}'; " +
                    "send it once."));
            }

            keyFor[column] = key;
            if (!string.Equals(key, column, StringComparison.Ordinal))
                renames.Add((key, column));
        }

        foreach (var (from, to) in renames)
        {
            var value = payload.Fields[from];
            payload.Fields.Remove(from);
            payload.Fields[to] = value;
        }
    }
}
