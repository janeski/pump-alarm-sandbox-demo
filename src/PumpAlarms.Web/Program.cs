using System.ClientModel;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI.Responses;

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry (traces for /analyze, model calls, run_python, sandbox calls), health checks.
// Under Aspire the traces go to the dashboard; see PumpAlarms.ServiceDefaults.
builder.AddServiceDefaults();

builder.Services.Configure<SandboxOptions>(builder.Configuration.GetSection("Sandbox"));
builder.Services.AddSingleton<TokenCredential>(new DefaultAzureCredential());
builder.Services.AddHttpClient<SandboxClient>();
builder.Services.AddSingleton<TraceCollector>();

// The model is in another tenant, so it uses an API key; the sandbox uses Entra.
var foundry = builder.Configuration.GetSection("Foundry").Get<FoundryOptions>() ?? new();
if (foundry.IsConfigured)
{
    // The SDK adds /responses itself; accept the full URL from the portal too.
    var endpoint = foundry.Endpoint.TrimEnd('/');
    if (endpoint.EndsWith("/responses", StringComparison.OrdinalIgnoreCase)) endpoint = endpoint[..^"/responses".Length];

    builder.Services
        .AddChatClient(new ResponsesClient(new ApiKeyCredential(foundry.ApiKey),
                new ResponsesClientOptions { Endpoint = new Uri(endpoint) })
            .AsIChatClient(foundry.Deployment))
        .UseFunctionInvocation(configure: f => f.MaximumIterationsPerRequest = PumpAnalyst.MaxRounds)
        // GenAI spans per model call and per tool call. In Development they include prompts, code and output.
        .UseOpenTelemetry(sourceName: "PumpAlarms.AI",
            configure: o => o.EnableSensitiveData = builder.Environment.IsDevelopment());
    builder.Services.AddSingleton<CodeGovernor>();
    builder.Services.AddTransient<PumpAnalyst>();
}

var app = builder.Build();

app.MapDefaultEndpoints();
app.UseDefaultFiles();
app.UseStaticFiles();

const string DefaultQuestion = "Which pumps need attention and why?";

// Step 3: fixed analysis in the sandbox, no AI yet. Step 4 lets the model write this code.
const string FixedAnalysis = """
    import pandas as pd

    df = pd.read_csv("/root/pump_alarms.csv", parse_dates=["timestamp"])
    print(df.shape)
    print(df.groupby("pump_id").agg(
        alarms=("alarm_code", "size"),
        critical=("severity", lambda s: (s == "critical").sum()),
        minutes=("duration_min", "sum"),
    ).sort_values("critical", ascending=False))
    """;

app.MapPost("/analyze", async (IFormFile file, [FromForm] string? question,
    IOptions<SandboxOptions> options, IServiceProvider services, TraceCollector traces, CancellationToken ct) =>
{
    question = string.IsNullOrWhiteSpace(question) ? DefaultQuestion : question;

    // Without sandbox settings the page still works, on canned data.
    if (!options.Value.IsConfigured)
    {
        await Task.Delay(1500, ct);
        return Results.Ok(Stub.Response(file.FileName, question));
    }

    var sandbox = services.GetRequiredService<SandboxClient>();
    using var recording = traces.Record(System.Diagnostics.Activity.Current);
    try
    {
        await sandbox.EnsureRunningAsync(ct);

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        await sandbox.WriteFileAsync(PumpAnalyst.DataPath, buffer.ToArray(), ct);

        // With a model configured, the AI writes the code (step 4).
        if (services.GetService<PumpAnalyst>() is { } analyst)
        {
            var (answer, steps, audit) = await analyst.AnalyzeAsync(question, ct);
            return Results.Ok(new AnalyzeResponse("live", sandbox.SandboxId, file.FileName, question, answer, steps,
                recording.Spans(), audit, services.GetRequiredService<CodeGovernor>().PolicyName));
        }

        // Without a model: the fixed analysis from step 3.
        await sandbox.WriteFileAsync("/root/analyze_fixed.py", System.Text.Encoding.UTF8.GetBytes(FixedAnalysis), ct);

        var run = await sandbox.ExecAsync("python3 /root/analyze_fixed.py", ct);

        return Results.Ok(new AnalyzeResponse(
            Mode: "live",
            SessionId: sandbox.SandboxId,
            FileName: file.FileName,
            Question: question,
            Answer: run.ExitCode == 0
                ? $"Fixed analysis, no AI yet: alarms, critical alarms and minutes per pump. Ran in the sandbox in {run.ExecutionTimeMs} ms."
                : $"The analysis failed in the sandbox (exit code {run.ExitCode}).",
            Steps: [new AnalyzeStep(FixedAnalysis, run.Stdout, run.Stderr)],
            Trace: recording.Spans()));
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or AuthenticationFailedException)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway, title: "Sandbox error");
    }
    catch (ClientResultException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway, title: "Model error");
    }
}).DisableAntiforgery();

// Sandbox status and pause/resume for the badge in the top bar.
var sandboxApi = app.MapGroup("/sandbox");

sandboxApi.MapGet("", (IOptions<SandboxOptions> options, IServiceProvider services, CancellationToken ct) =>
    SandboxCall(options, services, _ => Task.CompletedTask, ct));

sandboxApi.MapPost("/stop", (IOptions<SandboxOptions> options, IServiceProvider services, CancellationToken ct) =>
    SandboxCall(options, services, sandbox => sandbox.StopAsync(ct), ct));

sandboxApi.MapPost("/resume", (IOptions<SandboxOptions> options, IServiceProvider services, CancellationToken ct) =>
    SandboxCall(options, services, sandbox => sandbox.ResumeAsync(ct), ct));

app.Run();

// Runs an optional action, then returns the sandbox status. The UI polls until the state settles.
static async Task<IResult> SandboxCall(IOptions<SandboxOptions> options, IServiceProvider services,
    Func<SandboxClient, Task> action, CancellationToken ct)
{
    if (!options.Value.IsConfigured)
        return Results.Ok(new SandboxStatus(false, null, null, null, null, null, null));

    var sandbox = services.GetRequiredService<SandboxClient>();
    try
    {
        await action(sandbox);
        var info = await sandbox.GetInfoAsync(ct);
        return Results.Ok(new SandboxStatus(
            Configured: true,
            Id: sandbox.SandboxId,
            State: info.State,
            StoppedReason: info.StateDetails?.StoppedReason,
            AutoSuspendSeconds: info.Lifecycle?.AutoSuspendPolicy is { Enabled: true } p ? p.Interval : null,
            Cpu: info.Resources?.Cpu,
            Memory: info.Resources?.Memory));
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or AuthenticationFailedException)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway, title: "Sandbox error");
    }
}

record SandboxStatus(bool Configured, string? Id, string? State, string? StoppedReason,
    int? AutoSuspendSeconds, string? Cpu, string? Memory);

record AnalyzeStep(string Code, string Stdout, string Stderr, bool Blocked = false, string? Rule = null, string? Reason = null);

record AnalyzeResponse(
    string Mode,
    string SessionId,
    string FileName,
    string Question,
    string Answer,
    IReadOnlyList<AnalyzeStep> Steps,
    IReadOnlyList<TraceSpan>? Trace = null,
    IReadOnlyList<AuditRecord>? Audit = null,
    string? Policy = null);

// Canned result for running the UI without a sandbox.
static class Stub
{
    public static AnalyzeResponse Response(string fileName, string question) => new(
        Mode: "stub",
        SessionId: Guid.NewGuid().ToString(),
        FileName: fileName,
        Question: question,
        Answer: """
            ### Pumps that need attention

            1. **P-101 (Bergen-North)**: 4 of 8 alarms, 3 of them critical. `HIGH_TEMP` keeps coming back and lasts longer each time (14 → 32 → 51 min), plus a 45 min `VIBRATION` alarm. **Action:** inspect bearings and cooling this week.
            2. **P-310 (Trondheim)**: one critical `DRY_RUN` alarm and a 2 hour `COMM_LOSS`. **Action:** check the inlet level sensor and the network link.
            3. **P-204 (Oslo-East)**: two short `LOW_PRESSURE` warnings. **Action:** keep watching; check the suction side if it repeats.
            """,
        Steps:
        [
            new("""
                import pandas as pd
                df = pd.read_csv("/root/pump_alarms.csv", parse_dates=["timestamp"])
                print(df.shape)
                print(df.groupby("pump_id").agg(
                    alarms=("alarm_code", "size"),
                    critical=("severity", lambda s: (s == "critical").sum()),
                    minutes=("duration_min", "sum"),
                ).sort_values("critical", ascending=False))
                """,
                """
                (8, 6)
                         alarms  critical  minutes
                pump_id
                P-101         4         3      142
                P-310         2         1      123
                P-204         2         0       14
                """,
                ""),
            new("""
                hot = df[(df.pump_id == "P-101") & (df.alarm_code == "HIGH_TEMP")]
                print(hot[["timestamp", "severity", "duration_min"]].to_string(index=False))
                """,
                """
                          timestamp severity  duration_min
                2026-10-01 06:12:00  warning            14
                2026-10-01 07:40:00 critical            32
                2026-10-03 21:47:00 critical            51
                """,
                ""),
        ]);
}
