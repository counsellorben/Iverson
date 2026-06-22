using System.Diagnostics;
using System.Net.Sockets;

const string solutionRoot = ".";

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("[Launcher] Starting Iverson infrastructure...");

// Step 1: bring up Docker infrastructure
await RunCommandAsync("docker", "compose up -d", solutionRoot, cts.Token);
Console.WriteLine("[Launcher] Docker Compose up — waiting for services to be ready...");

// Step 2: wait for each service port
await WaitForPortAsync("localhost", 5432, "PostgreSQL", cts.Token);
await WaitForPortAsync("localhost", 9200, "Elasticsearch", cts.Token);
await WaitForPortAsync("localhost", 6333, "Qdrant", cts.Token);
await WaitForPortAsync("localhost", 9092, "Kafka", cts.Token);
await WaitForPortAsync("localhost", 4317, "Jaeger (OTLP)", cts.Token);

Console.WriteLine("[Launcher] All infrastructure ready. Starting Iverson.Api...");

// Step 3: launch the API
var apiPath = Path.Combine(solutionRoot, "Iverson.Api");
var apiProcess = StartProcess("dotnet", "run --no-build", apiPath);

Console.WriteLine("[Launcher] Iverson.Api started (PID {0}). Press Ctrl+C to stop.", apiProcess.Id);

try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException) { }

Console.WriteLine("[Launcher] Shutting down...");

if (!apiProcess.HasExited)
{
    apiProcess.Kill(entireProcessTree: true);
    await apiProcess.WaitForExitAsync();
}

await RunCommandAsync("docker", "compose down", solutionRoot, CancellationToken.None);
Console.WriteLine("[Launcher] Shutdown complete.");

static async Task WaitForPortAsync(string host, int port, string serviceName, CancellationToken ct)
{
    Console.Write($"[Launcher] Waiting for {serviceName} on {host}:{port}");
    while (!ct.IsCancellationRequested)
    {
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, ct);
            Console.WriteLine(" ready.");
            return;
        }
        catch
        {
            Console.Write(".");
            await Task.Delay(2000, ct);
        }
    }
}

static async Task RunCommandAsync(string cmd, string args, string workingDir, CancellationToken ct)
{
    var psi = new ProcessStartInfo(cmd, args)
    {
        WorkingDirectory = workingDir,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };

    var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {cmd}");
    await proc.WaitForExitAsync(ct);

    if (proc.ExitCode != 0)
    {
        var err = await proc.StandardError.ReadToEndAsync(ct);
        Console.Error.WriteLine($"[Launcher] {cmd} exited {proc.ExitCode}: {err}");
    }
}

static Process StartProcess(string cmd, string args, string workingDir)
{
    var psi = new ProcessStartInfo(cmd, args)
    {
        WorkingDirectory = workingDir,
        UseShellExecute = false
    };
    return Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {cmd}");
}
