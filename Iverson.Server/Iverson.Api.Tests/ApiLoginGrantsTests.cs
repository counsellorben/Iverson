using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Iverson.Api.Tests;

public sealed class ApiLoginGrantsTests
{
    // `... ON "Table" TO ...` or `... ON table TO ...`; the CONNECT grant's `ON DATABASE %I` names no table.
    private static readonly Regex GrantedTable = new("""\bON\s+(?:"(?<t>[^"]+)"|(?<t>\w+))\s+TO\b""");

    [Fact]
    public void Tables_NamesExactlyTheTablesTheStatementsGrantOn()
    {
        // Every statement is either a table grant this parses or the database's CONNECT grant, so
        // a table grant written in a shape the pattern misses fails here instead of going unchecked.
        var unparsed = ApiLoginGrants.Statements
            .Where(statement => !GrantedTable.IsMatch(statement) && !statement.Contains(" ON DATABASE ", StringComparison.Ordinal));
        unparsed.Should().BeEmpty();

        var granted = ApiLoginGrants.Statements
            .Select(statement => GrantedTable.Match(statement))
            .Where(match => match.Success)
            .Select(match => match.Groups["t"].Value)
            .Distinct();

        ApiLoginGrants.Tables.Should().OnlyHaveUniqueItems();
        ApiLoginGrants.Tables.Should().BeEquivalentTo(granted);
    }
}
