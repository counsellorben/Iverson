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
    string IdPrefix,
    string ServiceToken = "",
    // S8 identity's negative leg: an acting-user token for a DIFFERENT, active tenant, which the
    // driver sends in place of its own to prove the server denies that write. Empty for every
    // other scenario — and emitted as an empty value rather than omitted, so a driver's
    // positional `--flag value` parser never mis-pairs the flags that follow it.
    string WrongActingToken = "");

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
/// The seam every scenario reaches the drivers through. Extracted so a test can substitute a
/// scripted runner and drive a scenario's <c>RunAsync</c> END TO END, rather than only grading the
/// judgement helpers <c>RunAsync</c> happens to call.
///
/// <para><b>Why this exists.</b> Ruling 38's residual: the reaches-the-cell pattern grades the
/// EXTRACTED JUDGE, not the line in <c>RunAsync</c> that calls it — so deleting
/// <c>JudgeReadPhase(...)</c> or <c>JudgeDriverDepthRead(...)</c> from a scenario left the whole
/// suite green while the axis graded nothing. Ruling 42 named the general shape: a test that does
/// not constrain WHAT THE LIVE CODE ACTUALLY CONSUMED is a test that cannot see the live code stop
/// consuming it. This interface is the remedy for the scenario half of that.</para>
///
/// <para>Deliberately only the two members the scenarios use. Everything else on
/// <see cref="DriverRunner"/> — process spawning, repo-root location, flag construction — is an
/// implementation detail no scenario should be able to reach, and widening this interface to make
/// a test convenient re-opens the coupling it exists to break.</para>
/// </summary>
public interface IDriverRunner
{
    /// <inheritdoc cref="DriverRunner.RunPhaseAsync"/>
    Task<IReadOnlyList<DriverPhaseOutcome>> RunPhaseAsync(
        Phase phase,
        IReadOnlyCollection<string> languages,
        DriverContext context,
        CancellationToken ct = default);

    /// <inheritdoc cref="DriverRunner.KeysByLanguage"/>
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> KeysByLanguage { get; }
}

/// <summary>
/// Builds each of the five drivers once per run and execs the appropriate one once per phase,
/// per the table in the Task 2 brief. Owns no assertions — it hands the orchestrator's Verifier
/// (Task 8) a <see cref="PhaseDocument"/> per successful call and reports build/exec failures as
/// data (<see cref="DriverPhaseOutcome"/>), never throwing them past a single language's row.
/// </summary>
public sealed class DriverRunner : IDriverRunner
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
                    // The tool itself ran (Process.Start succeeded — see the ToolMissing branch
                    // above for the "not on PATH" case) and exited non-zero. That is a genuine
                    // build break, not toolchain absence, and must surface as Broken with its
                    // stderr so a real compile error (e.g. once Tasks 3-7 land driver code) is
                    // never silently reported as a skip.
                    outcomes.Add(new DriverPhaseOutcome.Broken(spec.Language, build.ExitCode, build.Stderr));
                    continue;
                }

                _builtLanguages.Add(spec.Language);
            }

            var outPath = Path.Combine(Path.GetTempPath(), $"iverson-conformance-{spec.Language}-{PhaseNames.ToToken(phase)}-{Guid.NewGuid():N}.json");
            var execArgs = new List<string>(spec.ExecArgs);
            execArgs.AddRange(BuildFlags(phase, spec.Language, context, outPath));

            var cwd = ResolveCwd(spec.Cwd);
            var execResult = await RunProcessAsync(ResolveCommand(spec.ExecCommand, cwd), execArgs, cwd, ct);

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

                // A missing/null "steps" key normalizes to an empty list (PhaseDocument's own
                // constructor) rather than null, so this can never NRE downstream — but zero
                // steps is itself evidence of a malformed document (early return, truncated
                // write, ...) and must be reported as a failure for this one language, not
                // silently treated as "the driver did nothing this phase and that's fine".
                if (document.Steps.Count == 0)
                    throw new InvalidOperationException("the driver reported no steps");
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

    /// <summary>
    /// Canonicalizes whatever the operator supplied for the gRPC endpoint into
    /// <c>scheme://host:port</c> — always a scheme, always an explicit port.
    ///
    /// The five client libraries do not agree on an endpoint syntax: .NET's
    /// <c>GrpcChannel.ForAddress</c> and Java's <c>URI.create(...).getHost()/getPort()</c> need the
    /// scheme (Java additionally needs the port, since <c>getPort()</c> returns -1 without one),
    /// whereas Go's <c>grpc.Dial</c> and the TypeScript driver's host/port split need it gone. There
    /// is therefore no raw value that works everywhere; the harness picks one canonical form here
    /// and each driver adapts, rather than five drivers each guessing.
    /// </summary>
    internal static string NormalizeGrpcUrl(string value)
    {
        var raw = (value ?? string.Empty).Trim();
        if (raw.Length == 0)
            throw new ArgumentException("gRPC endpoint is empty", nameof(value));

        // A bare "host:port" (or bare "host") is not an absolute URI; give it the plaintext
        // scheme the harness's drivers dial with.
        var withScheme = raw.Contains("://", StringComparison.Ordinal) ? raw : $"http://{raw}";

        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            throw new ArgumentException($"gRPC endpoint '{raw}' is not a usable host[:port] or URL", nameof(value));

        // Uri supplies the scheme default (80/443) when no port was written, so Port is never -1
        // here; emitting it explicitly is what makes the value parseable by Java's URI.getPort().
        return $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }

    internal List<string> BuildFlags(Phase phase, string language, DriverContext context, string outPath)
    {
        var flags = new List<string>
        {
            "--scenario", context.Scenario,
            "--phase", PhaseNames.ToToken(phase),
            "--type", context.Type,
            "--tenant", context.Tenant,
            // Normalized once, here, so that a single --grpc value is dialable by all five
            // drivers. Do not remove: .NET (GrpcChannel.ForAddress) and Java (URI.create().getHost()
            // /getPort()) REQUIRE the scheme and an explicit port, while Go (grpc.Dial) and
            // TypeScript (host/port split) must strip the scheme back off — which they now do.
            // Passing the raw value through instead resurrects a harness bug that reads as a
            // per-language client defect.
            "--grpc", NormalizeGrpcUrl(context.GrpcUrl),
            "--client-id", context.ClientId ?? string.Empty,
            "--client-secret", context.ClientSecret ?? string.Empty,
            "--token-endpoint", context.TokenEndpoint ?? string.Empty,
            // A pre-minted service token, and the only one the drivers should ever use. The
            // client-credentials trio above is left in the contract for a driver run by hand,
            // but a driver that mints its own token cannot produce a usable one here: Authentik
            // stamps the JWT's `iss` from the request's Host header, so a token fetched from
            // localhost carries an issuer the API rejects outright (401), and none of the five
            // drivers passes a scope, so even an accepted token would lack `schema_admin` (403
            // on RegisterSchema). The orchestrator already mints this correctly once, with both
            // the Host header and the scope, so it hands the result over rather than having
            // five languages each re-derive Authentik's issuer semantics.
            "--service-token", context.ServiceToken,
            "--acting-token", context.ActingToken,
            // Always emitted, empty included — see DriverContext.WrongActingToken.
            "--wrong-acting-token", context.WrongActingToken,
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

    /// <summary>
    /// Turns a driver-relative executable path into an absolute one. <c>ProcessStartInfo</c>
    /// resolves a relative <c>FileName</c> against the CALLING process's current directory, not
    /// against <c>WorkingDirectory</c> — so Go's <c>bin/conformance</c> (the one driver executed
    /// as a built artifact rather than through a tool on PATH) was reported as a missing
    /// toolchain and its whole row silently skipped, even though the build had just produced it.
    /// A bare command name is left alone so PATH lookup still happens.
    /// </summary>
    internal static string ResolveCommand(string command, string cwd)
    {
        if (Path.IsPathRooted(command))
            return command;

        var namesADirectory = command.Contains('/') || command.Contains(Path.DirectorySeparatorChar);
        return namesADirectory ? Path.GetFullPath(Path.Combine(cwd, command)) : command;
    }

    private string ResolveCwd(string? relativeCwd) =>
        relativeCwd is null ? _repoRoot : Path.Combine(_repoRoot, relativeCwd);

    /// <summary>
    /// The wall-clock ceiling on any one driver build or exec. Without it a driver that hangs —
    /// on a channel that never connects, or an interactive prompt nothing will ever answer —
    /// blocks the entire run indefinitely with no output. A blown deadline is reported as a
    /// non-zero exit (hence <see cref="DriverPhaseOutcome.Broken"/>) carrying the timeout in its
    /// stderr, never as a skip: a hang is a failure of the subject, not an absent toolchain.
    /// </summary>
    internal static TimeSpan ProcessTimeout { get; set; } = TimeSpan.FromMinutes(10);

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

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ProcessTimeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(deadline.Token);

        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Kill the whole tree: `dotnet run`, `mvn` and `npx` all spawn children that outlive
            // the parent, and leaving them behind would hold the driver's --out file and its
            // gRPC connections open for the rest of the run.
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new ProcessOutcome.Completed(
                -1, $"timed out after {ProcessTimeout.TotalSeconds:0}s running: {command} {string.Join(' ', args)}");
        }

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
