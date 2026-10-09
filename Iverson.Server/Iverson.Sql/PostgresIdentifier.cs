using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Iverson.Sql;

/// <summary>
/// Identifier handling for the parts of a Postgres statement that cannot be parameters: table and
/// column names taken from a registered schema. Values always travel as parameters.
/// </summary>
public static class PostgresIdentifier
{
    // The shape every client-declared type, property and relation name must have
    // (SchemaRegistrationOrchestrator.IsValidIdentifier delegates here). No underscores, because
    // NamingExtensions.ToSnakeCase inserts its own when deriving table names; \A and \z, not ^ and
    // $, so a trailing newline is refused; an empty string never matches.
    private static readonly Regex PlainPattern = new(@"\A[A-Za-z][A-Za-z0-9]*\z", RegexOptions.Compiled);

    /// <summary>
    /// True when <paramref name="name"/> is a letter followed by letters and digits. Null-tolerant
    /// because SchemaRegistry.LoadAsync passes names straight from deserialized JSON, where a
    /// non-nullable string can still arrive null, and a throw there would abort the whole load.
    /// </summary>
    public static bool IsPlain(string? name) => name is not null && PlainPattern.IsMatch(name);

    /// <summary>
    /// <paramref name="name"/> as a delimited identifier. Refuses a double quote, which would end
    /// the identifier early, and NUL, which Postgres cannot store in an identifier. The exception
    /// reports the call-site expression (for example <c>schema.TableName</c>) as its
    /// <see cref="ArgumentException.ParamName"/> and never the rejected value.
    /// </summary>
    public static string Quote(string name, [CallerArgumentExpression(nameof(name))] string? paramName = null)
    {
        if (name.Contains('"') || name.Contains('\0'))
            throw new ArgumentException(
                "A Postgres identifier may not contain a double quote or a NUL character.", paramName);

        return $"\"{name}\"";
    }
}
