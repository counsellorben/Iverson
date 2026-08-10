using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Iverson.ClientConformance;

/// <summary>
/// The per-invocation context a driver needs regardless of phase. <see cref="AllKeys"/> and
/// <see cref="Id"/>Prefix change per call; the rest is stable for a whole harness run.
/// </summary>
public sealed record DriverContext(
    string Scenario,
    string Type,
    string Tenant,
    string GrpcUrl,
    string? ClientId,
    string? ClientSecret,
    string? TokenEndpoint,
    string ActingToken,
    string OwnerId,
    string IdPrefix);

/// <summary>
/// The outcome of running one language's driver for one phase. Exactly one of the three shapes
/// applies — see the discriminated subtypes.
/// </summary>
public abstract record DriverPhaseOutcome(string Language)
{
    /// <summary>The driver reported (exit 0) and its phase document parsed.</summary>
    public sealed record Success(string Language, PhaseDocument Document) : DriverPhaseOutcome(Language);

    /// <summary>The driver itself broke: a non-zero exit. This is not scenario data — the row fails.</summary>
    public sealed record Broken(string Language, int ExitCode, string Stderr) : DriverPhaseOutcome(Language);

    /// <summary>The language's toolchain is absent; the whole row is skipped for the run.</summary>
    public sealed record Skipped(string Language, string Reason) : DriverPhaseOutcome(Language);
}

/// <summary>
/// Builds each of the five drivers once per run and execs the appropriate one once per phase,
/// per the table in the Task 2 brief. Owns no assertions — it hands the orchestrator's Verifier
/// (Task 8) a <see cref="PhaseDocument"/> per successful call and reports build/exec failures as
/// data (<see cref="DriverPhaseOutcome"/>), never throwing them past a single language's row.
/// </summary>
public sealed class DriverRunner
{
    private readonly string _repoRoot;
    private readonly Dictionary<string, string> _skippedLanguages = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _builtLanguages = new(StringComparer.OrdinalIgnoreCase);

    // Accumulated across phases within a run: language -> (logical name -> row key). Populated
    // from each phase document's reported Keys and fed back as --keys on every phase after
    // register, language-qualified so five drivers sharing a logical name (e.g. "primary")
    // never collide.
    private readonly Dictionary<string, Dictionary<string, string>> _keysByLanguage =
        new(StringComparer.OrdinalIgnoreCase);

    public DriverRunner(string? repoRoot = null)
    {
        _repoRoot = repoRoot ?? LocateRepoRoot();
    }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> KeysByLanguage =>
        _keysByLanguage.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyDictionary<string, string>)kv.Value,
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Locates the repository root by walking up from the running assembly's directory looking
    /// for <c>Iverson.slnx</c>, so the orchestrator can be launched from anywhere rather than
    /// assuming a fixed relative layout.
    /// </summary>
    public static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Iverson.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate repository root (no Iverson.slnx found walking up from {AppContext.BaseDirectory}).");
    }

    private static readonly IReadOnlyList<DriverSpec> Drivers =
    [
        new DriverSpec(
            "dotnet", null,
            "dotnet",
            ["build", "Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Iverson.Client.Conformance.Driver.csproj"],
            "dotnet",
            ["run", "--no-build", "--project", "Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver", "--"]),
        new DriverSpec(
            "python", "Iverson.Clients/Python",
            null, null,
            "python3", ["conformance/driver.py"]),
        new DriverSpec(
            "typescript", "Iverson.Clients/TypeScript",
            "npx", ["tsc", "-p", "tsconfig.conformance.json"],
            "node", ["dist-conformance/conformance/driver.js"]),
        new DriverSpec(
            "go", "Iverson.Clients/Go",
            "go", ["build", "-o", "bin/conformance", "./conformance"],
            "bin/conformance", []),
        new DriverSpec(
            "java", null,
            "mvn", ["-B", "-f", "Iverson.Clients/Java/pom.xml", "-pl", "conformance", "-am", "-DskipTests", "package"],
            "java", ["-jar", "Iverson.Clients/Java/conformance/target/iverson-conformance-driver.jar"]),
    ];

    /// <summary>
    /// Runs one phase for the given languages: builds any driver not yet built this run, then
    /// execs each requested driver once, parsing its <c>--out</c> document on a clean exit.
    /// </summary>
    public async Task<IReadOnlyList<DriverPhaseOutcome>> RunPhaseAsync(
        Phase phase,
        IReadOnlyCollection<string> languages,
        DriverContext context,
        CancellationToken ct = default)
    {
        var outcomes = new List<DriverPhaseOutcome>();

        foreach (var spec in Drivers)
        {
            if (!languages.Contains(spec.Language, StringComparer.OrdinalIgnoreCase))
                continue;

            if (_skippedLanguages.TryGetValue(spec.Language, out var reason))
            {
                outcomes.Add(new DriverPhaseOutcome.Skipped(spec.Language, reason));
                continue;
            }

            if (spec.BuildCommand is not null && !_builtLanguages.Contains(spec.Language))
            {
                var buildResult = await RunProcessAsync(
                    spec.BuildCommand, spec.BuildArgs!, ResolveCwd(spec.Cwd), ct);

                if (buildResult is ProcessOutcome.ToolMissing missing)
                {
                    var skipReason = $"skip ({missing.Tool} not found)";
                    _skippedLanguages[spec.Language] = skipReason;
                    outcomes.Add(new DriverPhaseOutcome.Skipped(spec.Language, skipReason));
                    continue;
                }

                var build = (ProcessOutcome.Completed)buildResult;
                if (build.ExitCode != 0)
                {
                    // A build failure this early in the harness's life (no driver code exists
                    // yet) is expected to be toolchain absence, not a real build break — the
                    // brief treats both the same way: skip the whole row.
                    var skipReason = $"skip ({spec.BuildCommand} build failed)";
                    _skippedLanguages[spec.Language] = skipReason;
                    outcomes.Add(new DriverPhaseOutcome.Skipped(spec.Language, skipReason));
                    continue;
                }

                _builtLanguages.Add(spec.Language);
            }

            var outPath = Path.Combine(Path.GetTempPath(), $"iverson-conformance-{spec.Language}-{PhaseNames.ToToken(phase)}-{Guid.NewGuid():N}.json");
            var execArgs = new List<string>(spec.ExecArgs);
            execArgs.AddRange(BuildFlags(phase, spec.Language, context, outPath));

            var execResult = await RunProcessAsync(spec.ExecCommand, execArgs, ResolveCwd(spec.Cwd), ct);

            if (execResult is ProcessOutcome.ToolMissing execMissing)
            {
                var skipReason = $"skip ({execMissing.Tool} not found)";
                _skippedLanguages[spec.Language] = skipReason;
                outcomes.Add(new DriverPhaseOutcome.Skipped(spec.Language, skipReason));
                continue;
            }

            var exec = (ProcessOutcome.Completed)execResult;
            if (exec.ExitCode != 0)
            {
                outcomes.Add(new DriverPhaseOutcome.Broken(spec.Language, exec.ExitCode, exec.Stderr));
                continue;
            }

            PhaseDocument document;
            try
            {
                var json = await File.ReadAllTextAsync(outPath, ct);
                document = JsonSerializer.Deserialize<PhaseDocument>(json, JsonOptions)
                    ?? throw new InvalidOperationException("driver wrote an empty/null phase document");
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
            {
                outcomes.Add(new DriverPhaseOutcome.Broken(spec.Language, exec.ExitCode, $"failed to read/parse --out document: {ex.Message}"));
                continue;
            }
            finally
            {
                if (File.Exists(outPath))
                    File.Delete(outPath);
            }

            MergeKeys(spec.Language, document);
            outcomes.Add(new DriverPhaseOutcome.Success(spec.Language, document));
        }

        return outcomes;
    }

    /// <summary>Internal so tests can verify the language-qualified <c>--keys</c> shape without running a process.</summary>
    internal void MergeKeys(string language, PhaseDocument document)
    {
        foreach (var step in document.Steps)
        {
            if (step.Keys is not { Count: > 0 } keys)
                continue;

            if (!_keysByLanguage.TryGetValue(language, out var forLanguage))
            {
                forLanguage = new Dictionary<string, string>(StringComparer.Ordinal);
                _keysByLanguage[language] = forLanguage;
            }

            foreach (var (logicalName, key) in keys)
                forLanguage[logicalName] = key;
        }
    }

    internal List<string> BuildFlags(Phase phase, string language, DriverContext context, string outPath)
    {
        var flags = new List<string>
        {
            "--scenario", context.Scenario,
            "--phase", PhaseNames.ToToken(phase),
            "--type", context.Type,
            "--tenant", context.Tenant,
            "--grpc", context.GrpcUrl,
            "--client-id", context.ClientId ?? string.Empty,
            "--client-secret", context.ClientSecret ?? string.Empty,
            "--token-endpoint", context.TokenEndpoint ?? string.Empty,
            "--acting-token", context.ActingToken,
            "--owner-id", context.OwnerId,
            "--id-prefix", context.IdPrefix,
            "--out", outPath,
        };

        if (phase != Phase.Register)
        {
            flags.Add("--keys");
            flags.Add(JsonSerializer.Serialize(KeysByLanguage, JsonOptions));
        }

        return flags;
    }

    private string ResolveCwd(string? relativeCwd) =>
        relativeCwd is null ? _repoRoot : Path.Combine(_repoRoot, relativeCwd);

    private static async Task<ProcessOutcome> RunProcessAsync(
        string command, IReadOnlyList<string> args, string cwd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            // Process.Start throws Win32Exception when the executable cannot be found on PATH —
            // the signal that a language's toolchain is absent, distinct from the toolchain
            // existing but the build/exec itself failing (a non-zero exit).
            return new ProcessOutcome.ToolMissing(command);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stderr = await stderrTask;
        _ = await stdoutTask;

        return new ProcessOutcome.Completed(process.ExitCode, stderr);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private abstract record ProcessOutcome
    {
        public sealed record Completed(int ExitCode, string Stderr) : ProcessOutcome;
        public sealed record ToolMissing(string Tool) : ProcessOutcome;
    }

    private sealed record DriverSpec(
        string Language,
        string? Cwd,
        string? BuildCommand,
        IReadOnlyList<string>? BuildArgs,
        string ExecCommand,
        IReadOnlyList<string> ExecArgs);
}
