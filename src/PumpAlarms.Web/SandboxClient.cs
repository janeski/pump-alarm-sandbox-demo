using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure.Core;
using Microsoft.Extensions.Options;

public sealed class SandboxOptions
{
    public string ManagementUrl { get; set; } = "";   // e.g. https://management.swedencentral.azuredevcompute.io
    public string SubscriptionId { get; set; } = "";
    public string ResourceGroup { get; set; } = "";
    public string SandboxGroup { get; set; } = "";
    public string SandboxId { get; set; } = "";
    public string ApiVersion { get; set; } = "2026-02-01-preview";
    public string TokenScope { get; set; } = "https://management.azuredevcompute.io/.default";

    public bool IsConfigured =>
        ManagementUrl != "" && SubscriptionId != "" && ResourceGroup != "" && SandboxGroup != "" && SandboxId != "";
}

public sealed record ShellResult(int ExitCode, string Stdout, string Stderr, long ExecutionTimeMs);

// The parts of GET …/sandboxes/<id> the UI shows.
public sealed record SandboxInfo(string? State, SandboxStateDetails? StateDetails, SandboxLifecycle? Lifecycle, SandboxResources? Resources);
public sealed record SandboxStateDetails(string? StoppedReason, DateTimeOffset? StoppedAt);
public sealed record SandboxLifecycle(SandboxAutoSuspend? AutoSuspendPolicy);
public sealed record SandboxAutoSuspend(bool Enabled, int Interval);
public sealed record SandboxResources(string? Cpu, string? Memory);

// Talks to the ACA Sandboxes data plane. Only executeShellCommand is used: it is the call we have
// seen the aca CLI make, and files can be written through it as well.
public sealed class SandboxClient(HttpClient http, TokenCredential credential, IOptions<SandboxOptions> options,
    ILogger<SandboxClient> logger)
{
    static readonly ActivitySource Source = new("PumpAlarms.Sandbox");

    // Base64 characters per shell command when writing a file.
    const int ChunkSize = 48 * 1024;

    readonly SandboxOptions _o = options.Value;

    // Static because typed HttpClient services are created per request.
    static AccessToken _token;

    public string SandboxId => _o.SandboxId;

    public async Task<ShellResult> ExecAsync(string command, CancellationToken ct = default)
    {
        using var activity = Source.StartActivity("sandbox exec");
        activity?.SetTag("sandbox.id", _o.SandboxId);
        activity?.SetTag("sandbox.command", command);

        var result = await ExecCoreAsync(command, ct);

        activity?.SetTag("sandbox.exit_code", result.ExitCode);
        activity?.SetTag("sandbox.execution_ms", result.ExecutionTimeMs);
        if (result.ExitCode != 0) activity?.SetStatus(ActivityStatusCode.Error, $"exit code {result.ExitCode}");
        logger.LogInformation("Sandbox exec: {Command} -> exit {ExitCode} in {ExecutionMs} ms",
            command, result.ExitCode, result.ExecutionTimeMs);
        return result;
    }

    async Task<ShellResult> ExecCoreAsync(string command, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "/executeShellCommand", new { command }, ct);
        return (await response.Content.ReadFromJsonAsync<ShellResult>(ct))!;
    }

    // Sandboxes suspend themselves when idle (autoSuspendPolicy), and exec then fails with 409.
    // Resume and wait for Running; resuming from the snapshot takes about a second.
    public async Task EnsureRunningAsync(CancellationToken ct = default)
    {
        var state = (await GetInfoAsync(ct)).State;
        if (state == "Running") return;

        using var activity = Source.StartActivity("sandbox wake up");
        logger.LogInformation("Sandbox is {State}, resuming", state);
        await ResumeAsync(ct);
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            state = (await GetInfoAsync(ct)).State;
            if (state == "Running") return;
        }
        throw new InvalidOperationException($"Sandbox did not start in 30 s (state: {state}).");
    }

    public async Task<SandboxInfo> GetInfoAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "", null, ct);
        return (await response.Content.ReadFromJsonAsync<SandboxInfo>(ct))!;
    }

    // Stop keeps memory and disk in a snapshot; resume continues from it.
    public async Task StopAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Sandbox stop");
        (await SendAsync(HttpMethod.Post, "/stop", new { }, ct)).Dispose();
    }

    public async Task ResumeAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Sandbox resume");
        (await SendAsync(HttpMethod.Post, "/resume", new { }, ct)).Dispose();
    }

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string action, object? body, CancellationToken ct)
    {
        // A common mix-up is the portal (sandboxes.azure.com), which answers with an HTML page.
        if (!Uri.TryCreate(_o.ManagementUrl, UriKind.Absolute, out var management) ||
            !management.Host.EndsWith(".azuredevcompute.io", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Sandbox:ManagementUrl must be the data plane, https://management.<region>.azuredevcompute.io, not '{_o.ManagementUrl}'.");

        var url =$"{_o.ManagementUrl.TrimEnd('/')}/subscriptions/{_o.SubscriptionId}/resourceGroups/{_o.ResourceGroup}" +
                  $"/sandboxGroups/{_o.SandboxGroup}/sandboxes/{_o.SandboxId}{action}?api-version={_o.ApiVersion}";

        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(ct));

        var response = await http.SendAsync(request, ct);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!response.IsSuccessStatusCode || (response.Content.Headers.ContentLength != 0 && mediaType != "application/json"))
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            // Show where the call actually went (after redirects) and what came back.
            throw new HttpRequestException(
                $"Sandbox call failed: {method} {response.RequestMessage?.RequestUri ?? new Uri(url)}\n" +
                $"{(int)response.StatusCode} {response.ReasonPhrase}, content type '{mediaType}'\n" +
                (text.Length > 500 ? text[..500] + "…" : text));
        }
        return response;
    }

    // Sends the file as base64 in chunks, then decodes it in the sandbox.
    public async Task WriteFileAsync(string path, byte[] content, CancellationToken ct = default)
    {
        using var activity = Source.StartActivity("sandbox write file");
        activity?.SetTag("sandbox.path", path);
        activity?.SetTag("sandbox.bytes", content.Length);

        var b64 = Convert.ToBase64String(content);
        var tmp = path + ".b64";
        var chunks = (b64.Length + ChunkSize - 1) / ChunkSize;

        await RunOrThrowAsync($": > '{tmp}'", ct);
        for (var i = 0; i < b64.Length; i += ChunkSize)
            await RunOrThrowAsync($"printf '%s' '{b64.Substring(i, Math.Min(ChunkSize, b64.Length - i))}' >> '{tmp}'", ct);
        await RunOrThrowAsync($"base64 -d '{tmp}' > '{path}' && rm '{tmp}'", ct);

        logger.LogInformation("Sandbox write: {Path} ({Bytes} bytes, {Chunks} chunks)", path, content.Length, chunks);
    }

    async Task RunOrThrowAsync(string command, CancellationToken ct)
    {
        var result = await ExecCoreAsync(command, ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Sandbox command failed ({result.ExitCode}): {result.Stderr}");
    }

    // Az CLI tokens are not cached by Azure.Identity, so keep one until it is close to expiring.
    async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token.ExpiresOn < DateTimeOffset.UtcNow.AddMinutes(5))
            _token = await credential.GetTokenAsync(new TokenRequestContext([_o.TokenScope]), ct);
        return _token.Token;
    }
}
