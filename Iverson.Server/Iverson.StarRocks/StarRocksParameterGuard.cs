using System.Text.RegularExpressions;
using Dapper;

namespace Iverson.StarRocks;

/// <summary>
/// Fails a statement that carries a parameter placeholder nothing will bind.
///
/// StarRocks accepts an unsubstituted <c>@p0</c> in a WHERE clause as an unset user variable:
/// <c>SELECT Id FROM t WHERE Name=@p0</c> raises no error and returns zero rows. So a binding
/// bug in this codebase would surface as SILENTLY WRONG RESULTS, not as an exception — a query
/// would simply start returning nothing, and every caller would read that as "no matches".
///
/// This guard converts that failure mode into a loud one. It is a cheap string scan on a code
/// path already doing network I/O, and it runs before the statement leaves the process.
///
/// See docs/runbooks/integration-test-flake-signatures.md, "Syntax error on '@'".
/// </summary>
internal static class StarRocksParameterGuard
{
    // `@name`, excluding `@@session_var` (leading @@ fails the lookbehind on the second @) and
    // excluding a bare `@` not followed by an identifier char — which is what the user spec in
    // `GRANT ... TO USER 'iverson_app'@'%'` is.
    private static readonly Regex Placeholder =
        new(@"(?<!@)@(?!@)(\w+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static void EnsureAllPlaceholdersBound(string sql, object? param)
    {
        var placeholders = Placeholder.Matches(sql)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (placeholders.Count == 0) return;

        var supplied = SuppliedNames(param);

        var missing = placeholders
            .Where(p => !supplied.Contains(p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        if (missing.Count == 0) return;

        throw new InvalidOperationException(
            $"StarRocks statement carries {missing.Count} unbound parameter placeholder(s): " +
            $"{string.Join(", ", missing.Select(m => "@" + m))}. StarRocks would accept these as " +
            "unset user variables and return zero rows rather than failing, so the statement is " +
            $"refused here instead. SQL: {sql}");
    }

    private static HashSet<string> SuppliedNames(object? param)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        switch (param)
        {
            case null:
                break;
            case DynamicParameters dynamicParameters:
                foreach (var name in dynamicParameters.ParameterNames) names.Add(name);
                break;
            default:
                foreach (var property in param.GetType().GetProperties()) names.Add(property.Name);
                break;
        }
        return names;
    }
}
