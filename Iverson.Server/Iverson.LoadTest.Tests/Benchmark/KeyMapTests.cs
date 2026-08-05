using FluentAssertions;
using Iverson.LoadTest.Benchmark;
using Xunit;

namespace Iverson.LoadTest.Tests.Benchmark;

public class KeyMapTests
{
    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsAllEntries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"keymap-test-{Guid.NewGuid()}.json");
        try
        {
            var map = new Dictionary<string, string>
            {
                ["11111111-1111-1111-1111-111111111111"] = "doc-1",
                ["22222222-2222-2222-2222-222222222222"] = "doc-2",
            };

            await KeyMap.SaveAsync(map, path);
            var loaded = await KeyMap.LoadAsync(path);

            loaded.Should().BeEquivalentTo(map);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAsync_CreatesMissingParentDirectory()
    {
        var dir  = Path.Combine(Path.GetTempPath(), $"keymap-test-dir-{Guid.NewGuid()}");
        var path = Path.Combine(dir, "keymap.json");
        try
        {
            await KeyMap.SaveAsync(new Dictionary<string, string> { ["k"] = "v" }, path);

            File.Exists(path).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_EmptyObject_ReturnsEmptyMap()
    {
        var path = Path.Combine(Path.GetTempPath(), $"keymap-test-empty-{Guid.NewGuid()}.json");
        try
        {
            await File.WriteAllTextAsync(path, "{}");
            var loaded = await KeyMap.LoadAsync(path);

            loaded.Should().BeEmpty();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
