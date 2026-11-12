namespace LagLens.Core;

public enum ProbeKind { GatewayIcmp, InternetIcmp, InternetTcp, CustomTcp }
public enum ProbeOutcome { Success, Timeout, Error, RouteMismatch, Unavailable }
public enum Verdict { NeedMoreData, LocalNetworkSuspected, InternetPathSuspected, Mixed, NoIssueObserved, Inconclusive }

public sealed record AdapterInfo(string Id, string Name, string Description, string Kind,
    bool IsUp, long LinkSpeedBitsPerSecond, int InterfaceIndex, string? Ipv4Address, string? Gateway,
    bool IsDefaultRoute = false)
{
    public override string ToString() => $"{Name} — {Kind} — {(IsUp ? "connected" : "disconnected")}";
}
public sealed record AdapterDetails(string DriverVersion, string DriverDate, string DriverProvider, string DriverFile, string Notes);
public sealed record AdapterSnapshot(DateTimeOffset Timestamp, bool IsUp, long LinkSpeedBitsPerSecond,
    long ReceivedBytes, long SentBytes, long ReceiveErrors, long SendErrors, long ReceiveDiscards, long SendDiscards,
    string? Ipv4Address, string? Gateway, string? Error = null);
public sealed record ProbeTarget(string Id, string Label, string Address, ProbeKind Kind, int Port = 443);
public sealed record ProbeResult(string TargetId, ProbeOutcome Outcome, double? Milliseconds, string? Detail = null);
public sealed record SampleRound(int Sequence, DateTimeOffset Timestamp, AdapterSnapshot Adapter, IReadOnlyList<ProbeResult> Probes, double SchedulingDelayMs = 0);
public sealed record SessionMarker(DateTimeOffset Timestamp, string Label);
public sealed record TargetMetrics(string TargetId, string Label, int Attempts, int Successes, int Failures, int Skipped,
    double? AverageMs, double? P50Ms, double? P95Ms, double? MaxMs, double? JitterMs, double LossPercent);
public sealed record Assessment(Verdict Verdict, string Title, string Summary, IReadOnlyList<string> Evidence, IReadOnlyList<string> NextSteps);
public sealed record SessionView(DateTimeOffset StartedAt, DateTimeOffset? EndedAt, AdapterInfo Adapter, AdapterDetails? Driver,
    IReadOnlyList<ProbeTarget> Targets, IReadOnlyList<SampleRound> Rounds, IReadOnlyList<SessionMarker> Markers,
    IReadOnlyList<TargetMetrics> Metrics, Assessment Assessment, string? StoragePath = null);
public sealed record SessionOptions(AdapterInfo Adapter, TimeSpan? Duration = null, string? CustomHost = null, int CustomPort = 443);

public interface IPlatformDiagnostics
{
    Task<IReadOnlyList<AdapterInfo>> GetAdaptersAsync(CancellationToken cancellationToken);
    Task<AdapterDetails> GetAdapterDetailsAsync(AdapterInfo adapter, CancellationToken cancellationToken);
    Task<AdapterSnapshot> GetSnapshotAsync(AdapterInfo adapter, CancellationToken cancellationToken);
    Task<ProbeResult> ProbeAsync(ProbeTarget target, AdapterInfo adapter, CancellationToken cancellationToken);
}

public interface ISessionRecorder : IDisposable
{
    string StoragePath { get; }
    void WriteHeader(SessionView view);
    void WriteRound(SampleRound round);
    void WriteMarker(SessionMarker marker);
    void WriteFinal(SessionView view);
}
