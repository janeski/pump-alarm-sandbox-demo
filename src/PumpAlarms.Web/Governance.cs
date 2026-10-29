using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentGovernance;
using AgentGovernance.Policy;
using Microsoft.Extensions.Logging;

// Facts about one script, for the policy to decide on.
public sealed record CodeFacts(
    IReadOnlyList<string> Modules,
    bool ImportsNetwork,
    bool StartsProcesses,
    bool UsesDynamicCode,
    bool TouchesOutsideRoot,
    int CodeLines)
{
    public string Describe()
    {
        var found = new List<string>();
        if (ImportsNetwork) found.Add("network modules");
        if (StartsProcesses) found.Add("process execution");
        if (UsesDynamicCode) found.Add("eval/exec");
        if (TouchesOutsideRoot) found.Add("paths outside /root");
        found.Add($"{CodeLines} lines");
        if (Modules.Count > 0) found.Add("imports " + string.Join(", ", Modules));
        return string.Join("; ", found);
    }
}

// A text scan of the script, not a full Python parser. Good enough for a gate in front of the sandbox,
// which stays the real isolation boundary (microVM, egress Deny).
public static partial class CodeScanner
{
    static readonly HashSet<string> NetworkModules =
        ["socket", "ssl", "requests", "urllib", "urllib3", "http", "httpx", "aiohttp", "ftplib", "smtplib",
         "telnetlib", "websocket", "websockets", "paramiko"];

    static readonly HashSet<string> ProcessModules = ["subprocess", "pty"];

    public static CodeFacts Scan(string code)
    {
        var modules = new SortedSet<string>();
        foreach (Match m in ImportRegex().Matches(code))
            foreach (var name in m.Groups[1].Value.Split(','))
                modules.Add(name.Trim().Split(' ')[0].Split('.')[0]);
        foreach (Match m in FromImportRegex().Matches(code))
            modules.Add(m.Groups[1].Value.Split('.')[0]);
        modules.Remove("");

        return new CodeFacts(
            Modules: [.. modules],
            ImportsNetwork: modules.Overlaps(NetworkModules),
            StartsProcesses: modules.Overlaps(ProcessModules) || OsProcessRegex().IsMatch(code),
            UsesDynamicCode: DynamicCodeRegex().IsMatch(code) || modules.Contains("importlib"),
            TouchesOutsideRoot: OutsideRootPathRegex().IsMatch(code),
            CodeLines: code.Split('\n').Count(l => l.Trim().Length > 0));
    }

    [GeneratedRegex(@"^\s*import\s+([\w\.]+(?:\s+as\s+\w+)?(?:\s*,\s*[\w\.]+(?:\s+as\s+\w+)?)*)", RegexOptions.Multiline)]
    private static partial Regex ImportRegex();

    [GeneratedRegex(@"^\s*from\s+([\w\.]+)\s+import\b", RegexOptions.Multiline)]
    private static partial Regex FromImportRegex();

    [GeneratedRegex(@"\bos\.(system|popen|exec\w*|spawn\w*|fork)\s*\(")]
    private static partial Regex OsProcessRegex();

    // eval( / exec( / compile( / __import__( as functions, not methods such as df.eval or re.compile.
    [GeneratedRegex(@"(?<![\.\w])(eval|exec|compile|__import__)\s*\(")]
    private static partial Regex DynamicCodeRegex();

    // A quoted absolute path that is not under /root or /tmp, e.g. '/etc/passwd' or "/proc/self".
    [GeneratedRegex(@"[""'](/(?!root(?:/|[""']))(?!tmp(?:/|[""']))[A-Za-z_][^""'\s]*)[""']")]
    private static partial Regex OutsideRootPathRegex();
}

public sealed record GovernanceDecision(bool Allowed, string? Rule, string Reason, CodeFacts Facts);

// One line of the audit trail: every run_python attempt, allowed or blocked.
public sealed record AuditRecord(
    DateTimeOffset Time,
    int Run,
    string CodeSha256,
    bool Allowed,
    string? Rule,
    string Reason,
    string Facts,
    int? ExitCode = null,
    long? SandboxMs = null);

// Agent Governance Toolkit (Microsoft.AgentGovernance) in front of run_python:
// scan the script into facts, let the AGT policy decide, and audit every decision.
public sealed class CodeGovernor
{
    public const string AgentId = "did:mesh:pump-analyst";
    public const string ToolName = "run_python";

    static readonly ActivitySource Source = new("PumpAlarms.Governance");

    readonly GovernanceKernel _kernel;
    readonly ILogger<CodeGovernor> _logger;

    public string PolicyName { get; }

    public CodeGovernor(ILogger<CodeGovernor> logger)
    {
        _logger = logger;
        var path = Path.Combine(AppContext.BaseDirectory, "governance", "policy.yaml");
        PolicyName = Regex.Match(File.ReadAllText(path), @"^name:\s*(\S+)", RegexOptions.Multiline).Groups[1].Value;

        _kernel = new GovernanceKernel(new GovernanceOptions
        {
            PolicyPaths = [path],
            // Deny rules have higher priority than the allow rule, so the highest-priority match decides.
            ConflictStrategy = ConflictResolutionStrategy.PriorityFirstMatch,
            EnableAudit = true,
        });

        // AGT's own audit events (PolicyCheck, ToolCallBlocked, PolicyViolation) go to the log,
        // and with the log to the Aspire dashboard.
        _kernel.AuditEmitter.OnAll(e => _logger.LogInformation(
            "AGT audit {EventType}: agent {AgentId}, rule {Rule}, allowed {Allowed}",
            e.Type, e.AgentId, e.PolicyName, e.Data.GetValueOrDefault("allowed")));
    }

    public GovernanceDecision Check(string code, int run)
    {
        using var activity = Source.StartActivity("governance check");
        var facts = CodeScanner.Scan(code);

        var result = _kernel.EvaluateToolCall(AgentId, ToolName, new Dictionary<string, object>
        {
            ["run_number"] = run,
            ["code_lines"] = facts.CodeLines,
            ["imports_network"] = facts.ImportsNetwork,
            ["starts_processes"] = facts.StartsProcesses,
            ["uses_dynamic_code"] = facts.UsesDynamicCode,
            ["touches_outside_root"] = facts.TouchesOutsideRoot,
        });

        var rule = result.PolicyDecision?.MatchedRule;
        var decision = new GovernanceDecision(result.Allowed, rule, result.Reason ?? "", facts);

        activity?.SetTag("governance.policy", PolicyName);
        activity?.SetTag("governance.allowed", result.Allowed);
        activity?.SetTag("governance.rule", rule);
        activity?.SetTag("governance.facts", facts.Describe());
        if (!result.Allowed) activity?.SetStatus(ActivityStatusCode.Error, $"blocked by {rule}");
        return decision;
    }

    public static string Sha256(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)))[..12].ToLowerInvariant();
}
