using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

// Navigate from bin/Debug/net10.0/ up to Iverson.Server/ where docker-compose.yml lives
var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => cts.Cancel());

Console.WriteLine("[Launcher] Starting Iverson infrastructure...");

// Step 1: bring up Docker infrastructure (not iverson-api — that runs locally via dotnet run below)
await RunCommandAsync("docker", "compose up -d postgres elasticsearch qdrant kafka zookeeper jaeger ollama ollama-init", solutionRoot, cts.Token);
Console.WriteLine("[Launcher] Docker Compose up — waiting for services to be ready...");

// Step 2: wait for each service port
await WaitForPortAsync("localhost", 5432, "PostgreSQL", cts.Token);
await WaitForPortAsync("localhost", 6333, "Qdrant", cts.Token);
await WaitForOllamaAsync("http://localhost:11434/api/tags", cts.Token);
await WaitForPortAsync("localhost", 9092, "Kafka", cts.Token);
await WaitForPortAsync("localhost", 4317, "Jaeger (OTLP)", cts.Token);
await WaitForElasticsearchAsync("http://localhost:9200", cts.Token);

Console.WriteLine("[Launcher] All infrastructure ready. Starting Iverson.Api...");

// Step 3: launch the API over HTTPS using the developer certificate
var apiPath = Path.Combine(solutionRoot, "Iverson.Api");
var certPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".aspnet", "https", "Iverson.Api.pfx");
var apiEnv = new Dictionary<string, string>
{
    ["ASPNETCORE_Kestrel__Certificates__Default__Path"]     = certPath,
    ["ASPNETCORE_Kestrel__Certificates__Default__Password"] = "devcert",
};
var apiProcess = StartProcess("dotnet", "run --launch-profile https", apiPath, apiEnv);

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

static async Task WaitForOllamaAsync(string url, CancellationToken ct)
{
    Console.Write($"[Launcher] Waiting for Ollama at {url}");
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    while (!ct.IsCancellationRequested)
    {
        try
        {
            var response = await client.GetAsync(url, ct);
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine(" ready.");
                return;
            }
        }
        catch (OperationCanceledException) { return; }
        catch { }
        Console.Write(".");
        try { await Task.Delay(2000, ct); }
        catch (OperationCanceledException) { return; }
    }
}

static async Task WaitForElasticsearchAsync(string baseUrl, CancellationToken ct)
{
    Console.Write($"[Launcher] Waiting for Elasticsearch at {baseUrl}");
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    while (!ct.IsCancellationRequested)
    {
        try
        {
            var body = await client.GetStringAsync($"{baseUrl}/_cluster/health", ct);
            using var doc    = System.Text.Json.JsonDocument.Parse(body);
            var clusterStatus = doc.RootElement.GetProperty("status").GetString();
            if (clusterStatus is "green" or "yellow")
            {
                Console.WriteLine(" ready.");
                return;
            }
        }
        catch (OperationCanceledException) { return; }
        catch { }
        Console.Write(".");
        try { await Task.Delay(2000, ct); }
        catch (OperationCanceledException) { return; }
    }
}

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
        catch (OperationCanceledException) { return; }
        catch { }
        Console.Write(".");
        try { await Task.Delay(2000, ct); }
        catch (OperationCanceledException) { return; }
    }
}

static async Task RunCommandAsync(string cmd, string args, string workingDir, CancellationToken ct)
{
    var psi = new ProcessStartInfo(cmd, args)
    {
        WorkingDirectory = workingDir,
        UseShellExecute = false
    };

    var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {cmd}");
    await proc.WaitForExitAsync(ct);

    if (proc.ExitCode != 0)
        Console.Error.WriteLine($"[Launcher] {cmd} exited with code {proc.ExitCode}");
}

static Process StartProcess(string cmd, string args, string workingDir,
    Dictionary<string, string>? env = null)
{
    var psi = new ProcessStartInfo(cmd, args)
    {
        WorkingDirectory = workingDir,
        UseShellExecute = false
    };
    if (env is not null)
        foreach (var (k, v) in env)
            psi.Environment[k] = v;
    return Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {cmd}");
}
