using LagLens.Core;

internal static class Program
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly AdapterInfo Adapter = new("nic-1", "Ethernet", "Synthetic Ethernet", "Ethernet", true,
        1_000_000_000, 3, "192.168.1.20", "192.168.1.1", true);
    private static readonly ProbeTarget[] Targets =
    [
        new("gateway", "Router", "192.168.1.1", ProbeKind.GatewayIcmp),
        new("cf-icmp", "Cloudflare ping", "1.1.1.1", ProbeKind.InternetIcmp),
        new("google-icmp", "Google ping", "8.8.8.8", ProbeKind.InternetIcmp),
        new("cf-tcp", "Cloudflare TCP", "1.1.1.1", ProbeKind.InternetTcp),
        new("google-tcp", "Google TCP", "8.8.8.8", ProbeKind.InternetTcp)
    ];
    private static int _checks;
    private static int _failures;

    private static async Task<int> Main()
    {
        Check("healthy wired trace", () => Expect(Healthy(), Verdict.NoIssueObserved));
        Check("insufficient observations stay provisional", () => Expect(Healthy(10), Verdict.NeedMoreData));
        Check("empty trace stays provisional", () => Expect([], Verdict.NeedMoreData));
        Check("receive-error increment is local evidence", () => Expect(WithSnapshots(s => s.Sequence >= 20 ? s.Adapter with { ReceiveErrors = 4 } : s.Adapter), Verdict.LocalNetworkSuspected));
        Check("send-error increment is local evidence", () => Expect(WithSnapshots(s => s.Sequence >= 20 ? s.Adapter with { SendErrors = 2 } : s.Adapter), Verdict.LocalNetworkSuspected));
        Check("old cumulative errors do not implicate current session", () => Expect(WithSnapshots(s => s.Adapter with { ReceiveErrors = 300, SendErrors = 90 }), Verdict.NoIssueObserved));
        Check("counter reset does not become huge local delta", () => Expect(WithSnapshots(s => s.Adapter with { ReceiveErrors = s.Sequence < 20 ? 300 : 0, SendErrors = s.Sequence < 20 ? 90 : 0 }), Verdict.NoIssueObserved));
        Check("link down is local path evidence", () => Expect(WithSnapshots(s => s.Sequence == 20 ? s.Adapter with { IsUp = false } : s.Adapter), Verdict.LocalNetworkSuspected));
        Check("first sample disconnected is preserved as local evidence", () => Expect(WithSnapshots(s => s.Sequence == 0 ? s.Adapter with { IsUp = false } : s.Adapter), Verdict.LocalNetworkSuspected));
        Check("link down losing its address remains local evidence", () => Expect(WithSnapshots(s => s.Sequence == 20 ? s.Adapter with { IsUp = false, Ipv4Address = null, Gateway = null } : s.Adapter), Verdict.LocalNetworkSuspected));
        Check("negotiated link speed changes are local evidence", () => Expect(WithSnapshots(s => s.Sequence >= 20 ? s.Adapter with { LinkSpeedBitsPerSecond = 100_000_000 } : s.Adapter), Verdict.LocalNetworkSuspected));
        Check("unavailable telemetry is not a fabricated link event", () => Expect(WithSnapshots(s => s.Sequence == 20 ? s.Adapter with { IsUp = false, LinkSpeedBitsPerSecond = 0, ReceiveErrors = -1, SendErrors = -1, ReceiveDiscards = -1, SendDiscards = -1, Ipv4Address = null, Gateway = null, Error = "Synthetic inventory failure" } : s.Adapter), Verdict.Inconclusive));
        Check("blocked gateway prevents localization", () => Expect(Modify((s, p) => p.TargetId == "gateway" ? Failed(p) : p), Verdict.Inconclusive));
        Check("unavailable gateway prevents localization", () => Expect(Modify((s, p) => p.TargetId == "gateway" ? p with { Outcome = ProbeOutcome.Unavailable, Milliseconds = null } : p), Verdict.Inconclusive));
        Check("ICMP filtering does not overrule healthy TCP", () => Expect(Modify((s, p) => p.TargetId.EndsWith("icmp", StringComparison.Ordinal) ? Failed(p) : p), Verdict.NoIssueObserved));
        Check("one public TCP endpoint impaired is inconclusive", () => Expect(Modify((s, p) => s.Sequence >= 20 && p.TargetId == "cf-tcp" ? Failed(p) : p), Verdict.Inconclusive));
        Check("other public TCP endpoint impaired is inconclusive", () => Expect(Modify((s, p) => s.Sequence >= 20 && p.TargetId == "google-tcp" ? Failed(p) : p), Verdict.Inconclusive));
        Check("coincident public TCP timeouts with clean router suggest WAN", () => Expect(Modify((s, p) => IsIncident(s) && IsPublicTcp(p) ? Failed(p) : p), Verdict.InternetPathSuspected));
        Check("coincident public TCP spikes with clean router suggest WAN", () => Expect(Modify((s, p) => IsIncident(s) && IsPublicTcp(p) ? p with { Milliseconds = 300 } : p), Verdict.InternetPathSuspected));
        Check("outage after clean baseline suggests WAN", () => Expect(Modify((s, p) => s.Sequence >= 20 && IsPublicTcp(p) ? Failed(p) : p), Verdict.InternetPathSuspected));
        Check("public TCP never reachable has no usable control baseline", () => Expect(Modify((s, p) => IsPublicTcp(p) ? Failed(p) : p), Verdict.Inconclusive));
        Check("router-only latency spikes are inconclusive", () => Expect(Modify((s, p) => IsIncident(s) && p.TargetId == "gateway" ? p with { Milliseconds = 100 } : p), Verdict.Inconclusive));
        Check("correlated router and TCP spikes suggest local path", () => Expect(Modify((s, p) => IsIncident(s) && (p.TargetId == "gateway" || IsPublicTcp(p)) ? p with { Milliseconds = 300 } : p), Verdict.LocalNetworkSuspected));
        Check("correlated router and TCP loss suggest local path", () => Expect(Modify((s, p) => IsIncident(s) && (p.TargetId == "gateway" || IsPublicTcp(p)) ? Failed(p) : p), Verdict.LocalNetworkSuspected));
        Check("one latency-only WAN spike is not enough for suspicion", () => NotVerdict(Modify((s, p) => s.Sequence == 20 && IsPublicTcp(p) ? p with { Milliseconds = 300 } : p), Verdict.InternetPathSuspected));
        Check("noncoincident endpoint spikes do not suggest shared WAN event", () => NotVerdict(Modify((s, p) => (s.Sequence is 20 or 22 or 24 && p.TargetId == "cf-tcp") || (s.Sequence is 21 or 23 or 25 && p.TargetId == "google-tcp") ? p with { Milliseconds = 300 } : p), Verdict.InternetPathSuspected));
        Check("route mismatch prevents final localization", () => Expect(Modify((s, p) => s.Sequence == 20 ? p with { Outcome = ProbeOutcome.RouteMismatch, Milliseconds = null } : p), Verdict.Inconclusive));
        Check("IPv4 changes prevent aggregation across paths", () => Expect(WithSnapshots(s => s.Sequence >= 20 ? s.Adapter with { Ipv4Address = "192.168.1.21" } : s.Adapter), Verdict.Inconclusive));
        Check("gateway changes prevent aggregation across paths", () => Expect(WithSnapshots(s => s.Sequence >= 20 ? s.Adapter with { Gateway = "192.168.1.254" } : s.Adapter), Verdict.Inconclusive));
        Check("missing mandatory probe results prevent healthy verdict", () => Expect(Healthy().Select(s => s with { Probes = s.Probes.Where(p => p.TargetId != "cf-tcp").ToArray() }).ToArray(), Verdict.Inconclusive));
        Check("sampling stalls cannot establish internet latency source", () => Expect(Modify((s, p) => IsIncident(s) && IsPublicTcp(p) ? p with { Milliseconds = 300 } : p)
            .Select(s => IsIncident(s) ? s with { SchedulingDelayMs = 3000 } : s).ToArray(), Verdict.Inconclusive));
        Check("sampling stalls cannot establish local latency source", () => Expect(Modify((s, p) => IsIncident(s) && (p.TargetId == "gateway" || IsPublicTcp(p)) ? p with { Milliseconds = 300 } : p)
            .Select(s => IsIncident(s) ? s with { SchedulingDelayMs = 3000 } : s).ToArray(), Verdict.Inconclusive));
        Check("too few actual observations are inconclusive despite many rounds", () => Expect(Healthy().Select(s => s.Sequence < 12 ? s : s with { Probes = [] }).ToArray(), Verdict.Inconclusive));
        Check("distinct local errors and WAN events produce mixed evidence", () =>
        {
            var rounds = Modify((s, p) => IsIncident(s) && IsPublicTcp(p) ? Failed(p) : p)
                .Select(s => s with { Adapter = s.Sequence >= 45 ? s.Adapter with { ReceiveErrors = 2 } : s.Adapter }).ToArray();
            Expect(rounds, Verdict.Mixed);
        });
        Check("metrics distinguish failures and skipped probes", MetricsOutcomes);
        Check("metrics percentiles and jitter use observed timings", MetricsValues);
        Check("jitter never bridges a lost sample", MetricsGap);
        Check("missing results count as skipped and break jitter adjacency", MetricsMissing);
        Check("targets with no attempts have no invented latency", MetricsEmpty);
        Check("all failed metric samples do not invent zero latency", MetricsFailed);
        await CheckAsync("cancellation finalizes recorder and completed observations", EngineCancellation);
        Console.WriteLine($"{(_failures == 0 ? "PASS" : "FAIL")}: {_checks - _failures}/{_checks} diagnostic regression checks.");
        return _failures == 0 ? 0 : 1;
    }

    private static bool IsIncident(SampleRound round) => round.Sequence is 25 or 30 or 35 or 40;
    private static bool IsPublicTcp(ProbeResult result) => result.TargetId.EndsWith("tcp", StringComparison.Ordinal);
    private static ProbeResult Failed(ProbeResult result) => result with { Outcome = ProbeOutcome.Timeout, Milliseconds = null, Detail = "Synthetic timeout" };
    private static SampleRound[] Healthy(int count = 60) => Enumerable.Range(0, count).Select(i =>
    {
        var time = Start.AddSeconds(i);
        var snapshot = new AdapterSnapshot(time, true, 1_000_000_000, i * 10_000, i * 3_000,
            0, 0, 0, 0, Adapter.Ipv4Address, Adapter.Gateway);
        return new SampleRound(i, time, snapshot,
            Targets.Select(t => new ProbeResult(t.Id, ProbeOutcome.Success,
                t.Kind == ProbeKind.GatewayIcmp ? 1 : t.Address == "1.1.1.1" ? 20 : 25)).ToArray());
    }).ToArray();

    private static SampleRound[] Modify(Func<SampleRound, ProbeResult, ProbeResult> transform) => Healthy()
        .Select(s => s with { Probes = s.Probes.Select(p => transform(s, p)).ToArray() }).ToArray();
    private static SampleRound[] WithSnapshots(Func<SampleRound, AdapterSnapshot> transform) => Healthy()
        .Select(s => s with { Adapter = transform(s) }).ToArray();
    private static void Expect(IReadOnlyList<SampleRound> rounds, Verdict expected)
    {
        var actual = Analysis.Assess(Adapter, Targets, rounds);
        Require(actual.Verdict == expected, $"Expected {expected}; got {actual.Verdict}. {actual.Summary} Evidence: {string.Join(" | ", actual.Evidence)}");
    }
    private static void NotVerdict(IReadOnlyList<SampleRound> rounds, Verdict excluded)
    {
        var actual = Analysis.Assess(Adapter, Targets, rounds);
        Require(actual.Verdict != excluded, $"Unexpected {excluded}: {actual.Summary}");
    }
    private static TargetMetrics Metric(params ProbeResult[] probes)
    {
        var rounds = Healthy(probes.Length).Select((s, i) => s with { Probes = new[] { probes[i] } }).ToArray();
        return Analysis.CalculateMetrics(new[] { Targets[0] }, rounds).Single();
    }
    private static ProbeResult Success(double value) => new("gateway", ProbeOutcome.Success, value);
    private static ProbeResult Outcome(ProbeOutcome outcome) => new("gateway", outcome, null);
    private static void MetricsOutcomes()
    {
        var result = Metric(Success(10), Success(20), Outcome(ProbeOutcome.Timeout), Outcome(ProbeOutcome.Error),
            Outcome(ProbeOutcome.RouteMismatch), Outcome(ProbeOutcome.Unavailable));
        Require(result.Attempts == 4 && result.Successes == 2 && result.Failures == 2 && result.Skipped == 2, "Attempt/failure/skipped counts are incorrect.");
        Near(result.LossPercent, 50);
        Near(result.AverageMs, 15);
    }
    private static void MetricsValues()
    {
        var result = Metric(Success(10), Success(20), Success(30), Success(40), Success(100));
        Near(result.AverageMs, 40);
        Near(result.P50Ms, 30);
        Near(result.MaxMs, 100);
        Near(result.JitterMs, 22.5);
        Require(result.P95Ms is >= 40 and <= 100, "P95 must lie within observed upper range.");
    }
    private static void MetricsGap()
    {
        var result = Metric(Success(10), Outcome(ProbeOutcome.Timeout), Success(100));
        Require(result.JitterMs is null, "A missing observation must break jitter adjacency.");
    }
    private static void MetricsEmpty()
    {
        var result = Analysis.CalculateMetrics(new[] { Targets[0] }, Array.Empty<SampleRound>()).Single();
        Require(result.Attempts == 0 && result.P50Ms is null && result.AverageMs is null && result.JitterMs is null, "Empty metrics invented a timing.");
    }
    private static void MetricsMissing()
    {
        var rounds = Healthy(3).Select((s, i) => s with { Probes = i == 1 ? Array.Empty<ProbeResult>() : new[] { Success(i == 0 ? 10 : 100) } }).ToArray();
        var result = Analysis.CalculateMetrics(new[] { Targets[0] }, rounds).Single();
        Require(result.Attempts == 2 && result.Skipped == 1 && result.JitterMs is null, "Missing sample was not preserved as a skipped gap.");
    }
    private static void MetricsFailed()
    {
        var result = Metric(Outcome(ProbeOutcome.Timeout), Outcome(ProbeOutcome.Error));
        Require(result.AverageMs is null && result.P95Ms is null && result.JitterMs is null, "Failed samples invented a timing.");
        Near(result.LossPercent, 100);
    }
    private static async Task EngineCancellation()
    {
        using var recorder = new MemoryRecorder();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var engine = new SessionEngine(new FakePlatform(), recorder);
        engine.Updated += view => { if (view.Rounds.Count >= 2) cancellation.Cancel(); };
        try { await engine.RunAsync(new SessionOptions(Adapter, TimeSpan.FromMinutes(5)), cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Require(engine.Current is not null, "Session was not retained.");
        var view = engine.Current!;
        Require(view.EndedAt is not null, "Cancelled session was not finalized.");
        Require(view.Rounds.Count >= 2 && view.Rounds.Count < 6, "Cancellation did not stop sampling promptly.");
        Require(recorder.Headers == 1 && recorder.Finals == 1, "Recorder header/final counts are wrong.");
        Require(recorder.Rounds == view.Rounds.Count, "Completed observations were not recorded.");
    }
    private static void Near(double? actual, double expected) => Require(actual.HasValue && Math.Abs(actual.Value - expected) < 0.0001, $"Expected {expected}; got {actual?.ToString() ?? "null"}.");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Check(string name, Action test)
    {
        _checks++;
        try { test(); Console.WriteLine($"PASS {name}"); }
        catch (Exception error) { _failures++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
    }
    private static async Task CheckAsync(string name, Func<Task> test)
    {
        _checks++;
        try { await test(); Console.WriteLine($"PASS {name}"); }
        catch (Exception error) { _failures++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
    }
    private sealed class MemoryRecorder : ISessionRecorder
    {
        public string StoragePath => "memory://session";
        public int Headers { get; private set; }
        public int Rounds { get; private set; }
        public int Finals { get; private set; }
        public void WriteHeader(SessionView view) => Headers++;
        public void WriteRound(SampleRound round) => Rounds++;
        public void WriteMarker(SessionMarker marker) { }
        public void WriteFinal(SessionView view) => Finals++;
        public void Dispose() { }
    }
    private sealed class FakePlatform : IPlatformDiagnostics
    {
        public Task<IReadOnlyList<AdapterInfo>> GetAdaptersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AdapterInfo>>(new[] { Adapter });
        public Task<AdapterDetails> GetAdapterDetailsAsync(AdapterInfo adapter, CancellationToken cancellationToken) => Task.FromResult(new AdapterDetails("1.0", "2026-01-01", "Test", "test.sys", "Synthetic"));
        public Task<AdapterSnapshot> GetSnapshotAsync(AdapterInfo adapter, CancellationToken cancellationToken) => Task.FromResult(new AdapterSnapshot(DateTimeOffset.UtcNow, true, adapter.LinkSpeedBitsPerSecond, 1000, 500, 0, 0, 0, 0, adapter.Ipv4Address, adapter.Gateway));
        public Task<ProbeResult> ProbeAsync(ProbeTarget target, AdapterInfo adapter, CancellationToken cancellationToken) => Task.FromResult(new ProbeResult(target.Id, ProbeOutcome.Success, target.Kind == ProbeKind.GatewayIcmp ? 1 : 20));
    }
}
