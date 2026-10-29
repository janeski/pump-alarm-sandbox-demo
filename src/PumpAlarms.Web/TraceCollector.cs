using System.Collections.Concurrent;
using System.Diagnostics;

public sealed record TraceSpan(string Name, double StartMs, double DurationMs, int Depth, string? Detail, bool Error);

// Collects the spans of one request (model calls, tool calls, sandbox calls) so the UI can show the
// sequence without a tracing backend. Listens to our own sources only, not to every HTTP call.
public sealed class TraceCollector : IDisposable
{
    readonly ActivityListener _listener;
    readonly ConcurrentDictionary<ActivityTraceId, ConcurrentQueue<Activity>> _traces = new();

    public TraceCollector()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("PumpAlarms."),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (_traces.TryGetValue(activity.TraceId, out var spans)) spans.Enqueue(activity);
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    // Call at the start of a request; dispose the result when the request is done.
    public Recording Record(Activity? root) => new(this, root);

    public void Dispose() => _listener.Dispose();

    public sealed class Recording : IDisposable
    {
        readonly TraceCollector _owner;
        readonly Activity? _root;

        internal Recording(TraceCollector owner, Activity? root)
        {
            _owner = owner;
            _root = root;
            if (root is not null) owner._traces[root.TraceId] = new();
        }

        public IReadOnlyList<TraceSpan> Spans()
        {
            if (_root is null || !_owner._traces.TryGetValue(_root.TraceId, out var queue)) return [];

            var spans = queue.OrderBy(a => a.StartTimeUtc).ToList();
            var byId = spans.ToDictionary(a => a.SpanId);

            int DepthOf(Activity a)
            {
                var depth = 0;
                while (byId.TryGetValue(a.ParentSpanId, out var parent)) { depth++; a = parent; }
                return depth;
            }

            return spans.Select(a => new TraceSpan(
                a.DisplayName,
                (a.StartTimeUtc - _root.StartTimeUtc).TotalMilliseconds,
                a.Duration.TotalMilliseconds,
                DepthOf(a),
                Describe(a),
                a.Status == ActivityStatusCode.Error)).ToList();
        }

        public void Dispose()
        {
            if (_root is not null) _owner._traces.TryRemove(_root.TraceId, out _);
        }
    }

    // A short human-readable summary from the tags we know about.
    static string? Describe(Activity a)
    {
        string? Tag(string name) => a.GetTagItem(name)?.ToString();

        if (Tag("gen_ai.usage.input_tokens") is { } input)
            return $"{input} tokens in, {Tag("gen_ai.usage.output_tokens") ?? "?"} out";
        if (Tag("sandbox.exit_code") is { } exit)
            return $"exit {exit}, {Tag("sandbox.execution_ms")} ms in sandbox: {Shorten(Tag("sandbox.command"))}";
        if (Tag("sandbox.path") is { } path)
            return $"{path}, {Tag("sandbox.bytes")} bytes";
        if (Tag("governance.rule") is { } rule)
            return $"{(Tag("governance.allowed") == "True" ? "allowed" : "blocked")} by {rule}: {Shorten(Tag("governance.facts"))}";
        return null;
    }

    static string? Shorten(string? s) => s is null || s.Length <= 80 ? s : s[..80] + "…";
}
