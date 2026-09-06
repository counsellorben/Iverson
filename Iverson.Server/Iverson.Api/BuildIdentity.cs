using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace Iverson.Api;

/// <summary>
/// Identity of the code actually running, for attributing benchmark runs.
///
/// MVIDs are read from the assembly FILES, never from loaded assemblies:
/// AppDomain.CurrentDomain.GetAssemblies() is load-order dependent, and
/// Iverson.Client.Contracts is never touched directly by Program.cs, so a
/// loaded-set composite could differ between two requests to one process.
///
/// The composite spans every Iverson.* assembly rather than the entry assembly
/// alone: a change confined to Iverson.Vector leaves Iverson.Api's MVID
/// identical, and Iverson.Vector is where the ranking code being measured lives.
/// </summary>
internal static class BuildIdentity
{
    internal static (string Composite, SortedDictionary<string, string> Assemblies) Compute()
    {
        var assemblies = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(AppContext.BaseDirectory, "Iverson.*.dll"))
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var md = pe.GetMetadataReader();
            assemblies[Path.GetFileNameWithoutExtension(path)] =
                md.GetGuid(md.GetModuleDefinition().Mvid).ToString();
        }

        var sb = new StringBuilder();
        foreach (var (name, mvid) in assemblies)
            sb.Append(name).Append(':').Append(mvid).Append('\n');

        var composite = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16]
            .ToLowerInvariant();

        return (composite, assemblies);
    }
}
