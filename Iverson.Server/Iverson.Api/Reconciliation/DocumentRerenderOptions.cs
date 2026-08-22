namespace Iverson.Api.Reconciliation;

public sealed class DocumentRerenderOptions
{
    public const string Section = "DocumentRerender";
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);
    public int BatchSize        { get; set; } = 100;
    public int PageSize         { get; set; } = 500;
    public int MaxAttempts      { get; set; } = 10;
}
