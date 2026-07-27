namespace Iverson.Embeddings;

public interface IEnrichmentService
{
    string ModelId { get; }

    Task<string> GenerateAsync(string prompt, CancellationToken ct = default);

    Task<string> GenerateJsonAsync(string prompt, CancellationToken ct = default);
}
