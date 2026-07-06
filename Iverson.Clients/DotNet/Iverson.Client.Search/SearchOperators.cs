// Static import aliases so callers can write:
//   .Where(a => a.Title, Contains, "basketball")
// instead of SearchOperator.Contains.

using Iverson.Client.Contracts;

namespace Iverson.Client.Search;

public static class SearchOperators
{
    /// <summary>Use <c>EqualTo</c> to avoid collision with <c>object.Equals</c>.</summary>
    public new static readonly SearchOperator Equals           = SearchOperator.Equals;
    public static readonly SearchOperator EqualTo              = SearchOperator.Equals;
    public static readonly SearchOperator NotEquals            = SearchOperator.NotEquals;
    public static readonly SearchOperator Contains             = SearchOperator.Contains;
    public static readonly SearchOperator StartsWith           = SearchOperator.StartsWith;
    public static readonly SearchOperator EndsWith             = SearchOperator.EndsWith;
    public static readonly SearchOperator GreaterThan          = SearchOperator.GreaterThan;
    public static readonly SearchOperator LessThan             = SearchOperator.LessThan;
    public static readonly SearchOperator GreaterThanOrEquals  = SearchOperator.GreaterThanOrEquals;
    public static readonly SearchOperator LessThanOrEquals     = SearchOperator.LessThanOrEquals;
    public static readonly SearchOperator In                   = SearchOperator.In;
}
