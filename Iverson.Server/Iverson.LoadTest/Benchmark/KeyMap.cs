using System.Text.Json;

namespace Iverson.LoadTest.Benchmark;

/// <summary>
/// Maps a server-assigned <c>ParentKey</c> (returned by <c>PersistAsync</c>) back to the corpus's
/// own document id. Ingest runs once and is shared across all eight sweep configurations (spec §1);
/// each configuration is a separate process run after a server rebuild, so this map has to survive
/// as a file — an in-memory field would not outlive the ingest process for Task 4 to read.
/// </summary>
public static class KeyMap
{
    public static async Task SaveAsync(IReadOnlyDictionary<string, string> map, string path, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, map, JsonOptions, ct);
    }

    public static async Task<Dictionary<string, string>> LoadAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var map = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, JsonOptions, ct);
        return map ?? [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
