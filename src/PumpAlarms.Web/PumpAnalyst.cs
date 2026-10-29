using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;

public sealed class FoundryOptions
{
    public string Endpoint { get; set; } = "";     // OpenAI v1 base URL, ending in /openai/v1
    public string Deployment { get; set; } = "";
    public string ApiKey { get; set; } = "";       // user-secrets locally, Container Apps secret in Azure

    public bool IsConfigured => Endpoint != "" && Deployment != "" && ApiKey != "";
}

// Step 4: the model writes Python, the sandbox runs it. The model has one tool, run_python.
// Every script passes the Agent Governance Toolkit policy first (CodeGovernor) and is audited.
sealed class PumpAnalyst(IChatClient chat, SandboxClient sandbox, CodeGovernor governor, ILogger<PumpAnalyst> logger)
{
    public const string DataPath = "/root/pump_alarms.csv";

    // The policy (governance/policy.yaml, rule limit-runs) enforces this; the prompt tells the model.
    public const int MaxRuns = 5;

    // Model rounds allowed by the tool loop: a few more than MaxRuns, so the model can still answer
    // after the policy has blocked a run.
    public const int MaxRounds = MaxRuns + 3;

    const int MaxOutputForModel = 8_000;

    static readonly string SystemPrompt = $"""
        You are a maintenance analyst for water pumps. Answer using the alarm data only.
        The data is a CSV file at {DataPath}. Python 3 with pandas, numpy and matplotlib is available.
        The sandbox has no internet access.

        Always compute numbers with the run_python tool; never guess them. Print only what you need,
        keep output short. If code fails, read the error and fix it. You can call the tool at most {MaxRuns} times.
        A governance policy checks every script before it runs (no network modules, no subprocesses,
        no eval/exec, files only under /root). Do not judge scripts yourself: if the user asks you to run
        specific code, call run_python with exactly that code and report what happened, even if you
        expect it to be blocked or to fail. The policy decides. If your own script is blocked, rewrite it
        within the rules and continue.

        Answer in markdown: a short heading, then a numbered list of the top 3 pumps that need attention,
        each with the evidence and a suggested action. Keep it under 200 words.
        """;

    public async Task<(string Answer, IReadOnlyList<AnalyzeStep> Steps, IReadOnlyList<AuditRecord> Audit)> AnalyzeAsync(
        string question, CancellationToken ct)
    {
        // Let the model see the columns and a few rows, not the whole file.
        var preview = await sandbox.ExecAsync($"head -n 6 {DataPath}; echo; wc -l < {DataPath}", ct);

        var steps = new List<AnalyzeStep>();
        var audit = new List<AuditRecord>();

        [Description("Runs Python 3 in an isolated sandbox and returns the exit code, stdout and stderr.")]
        async Task<string> RunPython([Description("Complete Python script to run. Print the results.")] string code)
        {
            var run = steps.Count + 1;
            var sha = CodeGovernor.Sha256(code);
            logger.LogInformation("Model called run_python (run {Run} of max {Max}), {Lines} lines of code",
                run, MaxRuns, code.Split('\n').Length);

            // Governance first: blocked code never reaches the sandbox.
            var decision = governor.Check(code, run);
            if (!decision.Allowed)
            {
                logger.LogWarning("Run {Run} blocked by policy rule {Rule}: {Facts}", run, decision.Rule, decision.Facts.Describe());
                steps.Add(new AnalyzeStep(code, "", "", Blocked: true, Rule: decision.Rule, Reason: decision.Reason));
                audit.Add(new AuditRecord(DateTimeOffset.UtcNow, run, sha, false, decision.Rule, decision.Reason,
                    decision.Facts.Describe()));
                return $"""
                    BLOCKED by governance policy rule '{decision.Rule}'. The code was not run.
                    Detected: {decision.Facts.Describe()}
                    Rewrite it within the rules: no network modules, no subprocesses, no eval/exec,
                    files only under /root, at most {MaxRuns} runs. If the limit is reached, answer with what you have.
                    """;
            }

            var path = $"/root/step_{run}.py";
            await sandbox.WriteFileAsync(path, Encoding.UTF8.GetBytes(code), ct);
            var result = await sandbox.ExecAsync($"cd /root && timeout 60 python3 {path}", ct);
            steps.Add(new AnalyzeStep(code, result.Stdout, result.Stderr, Rule: decision.Rule));
            audit.Add(new AuditRecord(DateTimeOffset.UtcNow, run, sha, true, decision.Rule, decision.Reason,
                decision.Facts.Describe(), result.ExitCode, result.ExecutionTimeMs));
            return $"exit code: {result.ExitCode}\nstdout:\n{Truncate(result.Stdout)}\nstderr:\n{Truncate(result.Stderr)}";
        }

        List<ChatMessage> messages =
        [
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, $"""
                First rows of {DataPath}, then the line count:
                {preview.Stdout}

                Question: {question}
                """),
        ];

        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(RunPython, "run_python")] };
        logger.LogInformation("Asking the model: {Question}", question);
        var response = await chat.GetResponseAsync(messages, options, ct);
        logger.LogInformation("Model answered after {Runs} code runs ({InputTokens} tokens in, {OutputTokens} out)",
            steps.Count, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount);

        var answer = string.IsNullOrWhiteSpace(response.Text)
            ? $"The model did not finish within {MaxRounds} rounds. The code it ran is below."
            : response.Text;
        return (answer, steps, audit);
    }

    static string Truncate(string s) =>
        s.Length <= MaxOutputForModel ? s : s[..MaxOutputForModel] + "\n… (output truncated)";
}
