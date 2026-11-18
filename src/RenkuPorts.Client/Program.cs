using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Configuration;

// renkuports-client: claims build jobs for its platform from the coordinator, runs BuildCommand on each, and
// uploads the resulting hpkgs (or reports the failure with the build log).
// Config: appsettings.json (next to the binary, then the working directory), RENKUPORTS_* env vars, --Key value args.
// `jobs [--mine|--owner] [--status S] [--platform P] [--page N]` and `status` only show the queue (RenkuPorts.Client.InfoCommands).
if (args.Contains("--version")) { Console.WriteLine(RenkuPorts.Client.BuildClient.Version); return 0; }
var command = args.FirstOrDefault() is "jobs" or "status" ? args[0] : null;
string? jobsQuery = null;
var configArgs = args;
if (command is not null && !RenkuPorts.Client.InfoCommands.TryParse(command, args[1..], out jobsQuery, out configArgs))
{
    Console.Error.WriteLine(RenkuPorts.Client.InfoCommands.Usage);
    return 2;
}
var once = args.Contains("--once");
var cfg = new ConfigurationBuilder()
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
    .AddJsonFile(Path.Combine(Environment.CurrentDirectory, "appsettings.json"), optional: true)
    .AddEnvironmentVariables("RENKUPORTS_")
    .AddCommandLine(configArgs.Where(a => a != "--once").ToArray())
    .Build();

string Required(string key) => cfg[key] is { Length: > 0 } v ? v : throw new InvalidOperationException($"{key} is not configured (appsettings.json, RENKUPORTS_{key.ToUpperInvariant()} or --{key})");
RenkuPorts.Client.BuildClient worker;
try
{
    worker = new RenkuPorts.Client.BuildClient(new Uri(Required("Server")), Required("ClientId"), Required("Secret"), command is null ? Required("BuildCommand") : cfg["BuildCommand"] ?? "",
        Path.GetFullPath(cfg["WorkDir"] ?? "work"), TimeSpan.FromSeconds(cfg.GetValue("PollSeconds", 60)));
}
catch (InvalidOperationException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}

using var cts = new CancellationTokenSource();
if (command is not null) return await RenkuPorts.Client.InfoCommands.RunAsync(worker, command, jobsQuery!, cts.Token);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; cts.Cancel(); });
return await worker.RunAsync(once, cts.Token);

namespace RenkuPorts.Client
{
    public record JobInfo(long JobId, string PortName, string Version, string Category, string RecipePath, string Platform);

    /// <summary>A non-success response from the coordinator.</summary>
    public class ApiException(HttpStatusCode status, string body) : Exception($"{(int)status} {status}: {body}")
    {
        public HttpStatusCode Status { get; } = status;
    }

    /// <summary>The poll / build / report loop.</summary>
    public partial class BuildClient(Uri server, string clientId, string secret, string buildCommand, string workDir, TimeSpan poll)
    {
        private const long MaxLogBytes = 5 << 20;
        /// <summary>AssemblyInformationalVersion, set at build time with <c>-p:Version=$(scripts/version.sh)</c>.</summary>
        public static readonly string Version =
            typeof(BuildClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev";
        private readonly HttpClient _http = new() { BaseAddress = server, Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestHeaders = { UserAgent = { new ProductInfoHeaderValue("renkuports-client", Version) } } };
        private string? _token;
        private DateTime _tokenExpires;

        /// <summary>Runs until cancelled (or after one job with <paramref name="once"/>). Returns the process exit code.</summary>
        public async Task<int> RunAsync(bool once, CancellationToken ct)
        {
            Log($"renkuports-client {Version}: polling {server} every {poll.TotalSeconds}s{(once ? " (once)" : "")}");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var job = await NextAsync(ct);
                    if (job is not null) await ProcessAsync(job, ct);
                    else if (!once) await Task.Delay(poll, ct);
                    if (once) { if (job is null) Log("no job queued"); return 0; }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e) when (e is HttpRequestException or ApiException or IOException)
                {
                    Log($"error: {e.Message}");
                    if (once) return 1;
                    try { await Task.Delay(poll, ct); } catch (OperationCanceledException) { break; }
                }
            }
            Log("stopped");
            return 0;
        }

        /// <summary>GETs a JSON resource of the client API (path relative to the server, e.g. <c>api/client/me</c>).</summary>
        public async Task<T> GetAsync<T>(string path, CancellationToken ct)
        {
            using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, path), ct);
            return await resp.Content.ReadFromJsonAsync<T>(ct) ?? throw new ApiException(resp.StatusCode, "empty response");
        }

        private async Task<JobInfo?> NextAsync(CancellationToken ct)
        {
            using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "api/client/jobs/next"), ct);
            if (resp.StatusCode == HttpStatusCode.NoContent) return null;
            return await resp.Content.ReadFromJsonAsync<JobInfo>(ct);
        }

        private async Task ProcessAsync(JobInfo job, CancellationToken ct)
        {
            Log($"job {job.JobId}: building {job.PortName} {job.Version} for {job.Platform}");
            var dir = Path.Combine(workDir, $"job-{job.JobId}");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            var outDir = Directory.CreateDirectory(Path.Combine(dir, "out")).FullName;
            var logPath = Path.Combine(dir, "build.log");

            try
            {
                var exit = await RunBuildAsync(job, outDir, logPath, ct);
                var hpkgs = Directory.GetFiles(outDir, "*.hpkg");
                if (exit != 0 || hpkgs.Length == 0)
                {
                    await FailAsync(job, exit != 0 ? $"build command exited with code {exit}" : "build produced no .hpkg", logPath);
                    return;
                }
                try
                {
                    foreach (var hpkg in hpkgs) await UploadAsync(job, hpkg, ct);
                    var log = ReadLogTail(logPath);
                    using var done = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"api/client/jobs/{job.JobId}/complete")
                        { Content = JsonContent.Create(new { log }) }, ct);
                    Log($"job {job.JobId}: succeeded, {hpkgs.Length} package(s) uploaded");
                }
                catch (Exception e) when (e is HttpRequestException or ApiException)
                {
                    await FailAsync(job, $"upload failed: {e.Message}", logPath);
                }
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                await FailAsync(job, $"cannot start BuildCommand '{buildCommand}': {e.Message}", logPath);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await FailAsync(job, "client stopped", logPath);
                throw;
            }
            finally
            {
                if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true); // hpkgs can be large; build.log stays
            }
        }

        /// <summary>Runs <c>BuildCommand &lt;port&gt; &lt;version&gt; &lt;outDir&gt;</c>, stdout and stderr to the log file.</summary>
        private async Task<int> RunBuildAsync(JobInfo job, string outDir, string logPath, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(buildCommand) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add(job.PortName);
            psi.ArgumentList.Add(job.Version);
            psi.ArgumentList.Add(outDir);
            psi.Environment["RENKUPORTS_PLATFORM"] = job.Platform;
            psi.Environment["RENKUPORTS_JOB_ID"] = job.JobId.ToString();
            psi.Environment["RENKUPORTS_RECIPE_PATH"] = job.RecipePath;

            await using var log = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
            using var p = new Process { StartInfo = psi };
            DataReceivedEventHandler write = (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
            p.OutputDataReceived += write;
            p.ErrorDataReceived += write;
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            try
            {
                await p.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                await StopAsync(p);
                throw;
            }
            p.WaitForExit(); // drains the redirected output
            return p.ExitCode;
        }

        /// <summary>Asks the build to stop (SIGTERM, so a script can clean up, e.g. build-stack-arm64.sh pausing the stack), then kills it.</summary>
        private static async Task StopAsync(Process p)
        {
            Log("stopping the build");
            if (!OperatingSystem.IsWindows()) _ = Kill(p.Id, 15);
            // Poll HasExited rather than WaitForExitAsync, which also waits for EOF on stdout/stderr: an orphaned
            // grandchild (e.g. a backgrounded sleep) can hold those open long after the build itself exited.
            for (var waited = TimeSpan.Zero; !p.HasExited && waited < TimeSpan.FromMinutes(2); waited += TimeSpan.FromMilliseconds(500))
                await Task.Delay(500);
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            p.CancelOutputRead(); // the log writer is disposed next
            p.CancelErrorRead();
        }

        [LibraryImport("libc", EntryPoint = "kill")]
        private static partial int Kill(int pid, int signal);

        /// <summary>
        /// Streams one hpkg (StreamContent, never buffered; the HttpClient has no timeout). The server stores it under its
        /// SHA-256 as it arrives, so sha256 and buildDate go first, before the file part.
        /// </summary>
        private async Task UploadAsync(JobInfo job, string path, CancellationToken ct)
        {
            string sha;
            await using (var f = File.OpenRead(path))
                sha = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(f, ct));
            using var resp = await SendAsync(() =>
            {
                var form = new MultipartFormDataContent
                {
                    { new StringContent(sha), "sha256" },
                    { new StringContent(File.GetLastWriteTimeUtc(path).ToString("O")), "buildDate" },
                };
                var file = new StreamContent(File.OpenRead(path), 1 << 20);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(file, "file", Path.GetFileName(path));
                return new HttpRequestMessage(HttpMethod.Post, $"api/client/jobs/{job.JobId}/packages") { Content = form };
            }, ct);
            Log($"job {job.JobId}: uploaded {Path.GetFileName(path)}");
        }

        /// <summary>Reports the failure. Runs even after cancellation, so a stopped client does not leave its job claimed.</summary>
        private async Task FailAsync(JobInfo job, string summary, string logPath)
        {
            Log($"job {job.JobId}: failed: {summary}");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                var log = ReadLogTail(logPath);
                using var _ = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"api/client/jobs/{job.JobId}/fail")
                    { Content = JsonContent.Create(new { summary, log }) }, timeout.Token);
            }
            catch (Exception e) when (e is HttpRequestException or ApiException or OperationCanceledException)
            {
                Log($"job {job.JobId}: reporting the failure failed ({e.Message}); the job stays claimed until its lease expires");
            }
        }

        /// <summary>Sends with a bearer token (fetched or refreshed as needed); on a 401 gets a new token and retries once.</summary>
        private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct)
        {
            for (var attempt = 0; ; attempt++)
            {
                using var req = make();
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
                var resp = await _http.SendAsync(req, ct);
                if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    resp.Dispose();
                    _token = null;
                    continue;
                }
                if (resp.IsSuccessStatusCode) return resp;
                var body = await resp.Content.ReadAsStringAsync(ct);
                resp.Dispose();
                throw new ApiException(resp.StatusCode, body.Length > 500 ? body[..500] : body);
            }
        }

        private async Task<string> TokenAsync(CancellationToken ct)
        {
            if (_token is not null && DateTime.UtcNow < _tokenExpires.AddMinutes(-5)) return _token;
            using var resp = await _http.PostAsJsonAsync("api/client/token", new { clientId, secret }, ct);
            if (!resp.IsSuccessStatusCode) throw new ApiException(resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
            var t = await resp.Content.ReadFromJsonAsync<TokenResponse>(ct) ?? throw new ApiException(resp.StatusCode, "empty token response");
            (_token, _tokenExpires) = (t.AccessToken, t.ExpiresAt.ToUniversalTime());
            return _token;
        }

        private record TokenResponse(string AccessToken, DateTime ExpiresAt);

        /// <summary>The last 5 MB of the build log (the end is where the error is).</summary>
        private static string ReadLogTail(string path)
        {
            if (!File.Exists(path)) return "";
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (f.Length > MaxLogBytes) f.Seek(-MaxLogBytes, SeekOrigin.End);
            return new StreamReader(f, Encoding.UTF8).ReadToEnd();
        }

        private static void Log(string msg) => Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}");
    }
}
