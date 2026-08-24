namespace Iverson.StarRocks;

/// <summary>
/// Hard limits StarRocks enforces, verified directly against <c>starrocks/allin1-ubuntu:4.1.1</c>
/// rather than read off documentation. Kept in one place because the projection (which picks column
/// types) and the write path (which rejects values that will not fit) must agree exactly; if they
/// drift, a write is accepted and then dead-letters downstream, which is the failure this exists to
/// prevent.
/// </summary>
public static class StarRocksLimits
{
    /// <summary>
    /// The widest <c>VARCHAR</c> StarRocks accepts. <c>VARCHAR(1048577)</c> is rejected with
    /// "Varchar size must be &lt;= 1048576".
    ///
    /// <para><b>BYTES, not characters.</b> Four multi-byte characters (eight bytes) into a
    /// <c>VARCHAR(4)</c> are filtered out; four ASCII characters are stored. Any length check
    /// against this limit must therefore measure UTF-8 bytes — measuring
    /// <c>string.Length</c> would pass values StarRocks then drops.</para>
    /// </summary>
    public const int MaxVarcharBytes = 1_048_576;

    /// <summary>
    /// What the bare <c>STRING</c> keyword actually is. <c>DESC</c> on a <c>STRING</c> column
    /// reports <c>varchar(65533)</c> — <c>STRING</c> is an alias, not an unbounded type, and it was
    /// long assumed here to be StarRocks' ceiling. It is not: see <see cref="MaxVarcharBytes"/>.
    /// </summary>
    public const int StringAliasBytes = 65_533;

    /// <summary>The column type large text fields are projected as.</summary>
    public const string WideTextColumnType = "VARCHAR(1048576)";

    /// <summary>
    /// The byte ceiling for a value in <paramref name="isLargeField"/>'s column. Large fields get
    /// the wide type; everything else stays on the <c>STRING</c> alias, because a sort key or an
    /// ordinary short attribute has no reason to carry a megabyte.
    /// </summary>
    public static int MaxBytesForTextColumn(bool isLargeField) =>
        isLargeField ? MaxVarcharBytes : StringAliasBytes;
}
