using System.Diagnostics;

namespace Iverson.Embeddings;

internal static class Telemetry
{
    internal static readonly ActivitySource Source = new("Iverson.Embeddings");
    internal const string HttpClientName = "iverson.ollama";
    internal const string EnrichmentHttpClientName = "iverson.ollama.enrichment";
}
