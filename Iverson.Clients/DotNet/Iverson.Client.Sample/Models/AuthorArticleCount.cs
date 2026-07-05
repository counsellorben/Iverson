namespace Iverson.Client.Sample.Models;

/// <summary>
/// Typed projection for the Pipeline (CTE) demo in Program.cs — one row per author
/// with the count of articles they've published. Property names match the pipeline's
/// GroupBy key and metric alias (StructConverter.FromStruct does case-insensitive JSON binding).
/// </summary>
public sealed class AuthorArticleCount
{
    public string AuthorId { get; set; } = string.Empty;
    public double Total { get; set; }
}
