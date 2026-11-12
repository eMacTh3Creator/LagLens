using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace LagLens.Core;

/// <summary>Samples low-volume network probes concurrently and emits immutable snapshots for UI/reporting.</summary>
public sealed class SessionEngine(IPlatformDiagnostics platform, ISessionRecorder? recorder = null)
{
    private readonly object gate = new();
    private readonly List<SampleRound> rounds = [];
    private readonly List<SessionMarker> markers = [];
    private SessionView? current;
    private int running;
    public event Action<SessionView>? Updated;
    public SessionView? Current { get { lock (gate) return current; } }

    public async Task RunAsync(SessionOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new InvalidOperationException("A diagnostic session is already running.");
        var initialized = false;
        try
        {
            lock (gate) { rounds.Clear(); markers.Clear(); current = null; }
            if (options.Duration is { } invalidDuration && invalidDuration <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options), "Duration must be greater than zero.");
            var duration = options.Duration is { } d && d < TimeSpan.FromHours(4) ? d : TimeSpan.FromHours(4);
            var targets = await CreateTargetsAsync(options, ct).ConfigureAwait(false);
            AdapterDetails driver;
            try { driver = await platform.GetAdapterDetailsAsync(options.Adapter, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { driver = new("Unavailable", "Unavailable", "Unavailable", "Unavailable", "Driver inventory failed: " + ex.Message); }
            var started = DateTimeOffset.UtcNow;
            var initial = new SessionView(started, null, options.Adapter, driver, targets, [], [], [],
                Analysis.Assess(options.Adapter, targets, []), recorder?.StoragePath);
            lock (gate)
            {
                recorder?.WriteHeader(initial);
                current = initial;
                initialized = true;
            }
            Updated?.Invoke(initial);
            var stopwatch = Stopwatch.StartNew();
            var expected = TimeSpan.Zero;
            while (!ct.IsCancellationRequested && stopwatch.Elapsed < duration)
            {
                var roundStart = stopwatch.Elapsed;
                var timestamp = DateTimeOffset.UtcNow;
                var snapshotTask = SafeSnapshotAsync(options.Adapter, ct);
                var probesTask = Task.WhenAll(targets.Select(t => SafeProbeAsync(t, options.Adapter, ct)));
                await Task.WhenAll(snapshotTask, probesTask).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var executionOvershoot = Math.Max(0, (stopwatch.Elapsed - roundStart).TotalMilliseconds - 1000);
                var round = new SampleRound(rounds.Count + 1, timestamp, await snapshotTask.ConfigureAwait(false),
                    await probesTask.ConfigureAwait(false), Math.Max(executionOvershoot, Math.Max(0, (roundStart - expected).TotalMilliseconds)));
                SessionView updated;
                lock (gate)
                {
                    recorder?.WriteRound(round);
                    rounds.Add(round);
                    updated = BuildView(null);
                    current = updated;
                }
                Updated?.Invoke(updated);
                expected = roundStart + TimeSpan.FromSeconds(1);
                var delay = expected - stopwatch.Elapsed;
                var remaining = duration - stopwatch.Elapsed;
                if (delay > remaining) delay = remaining;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            try
            {
                if (initialized)
                {
                    SessionView final;
                    lock (gate)
                    {
                        final = BuildView(DateTimeOffset.UtcNow);
                        current = final;
                        recorder?.WriteFinal(final);
                    }
                    Updated?.Invoke(final);
                }
            }
            finally { Interlocked.Exchange(ref running, 0); }
        }
    }

    public void Mark(string label = "Lag / stream drop")
    {
        SessionView view;
        lock (gate)
        {
            if (current is null || current.EndedAt is not null) return;
            var marker = new SessionMarker(DateTimeOffset.UtcNow, string.IsNullOrWhiteSpace(label) ? "Lag / stream drop" : label.Trim()[..Math.Min(label.Trim().Length, 200)]);
            recorder?.WriteMarker(marker);
            markers.Add(marker);
            view = BuildView(null);
            current = view;
        }
        Updated?.Invoke(view);
    }

    private SessionView BuildView(DateTimeOffset? endedAt)
    {
        var previous = current!;
        var samples = rounds.ToArray();
        var metrics = Analysis.CalculateMetrics(previous.Targets, samples);
        return previous with { EndedAt = endedAt, Rounds = samples, Markers = markers.ToArray(), Metrics = metrics, Assessment = Analysis.AssessWithMetrics(previous.Adapter, previous.Targets, samples, metrics) };
    }

    private async Task<AdapterSnapshot> SafeSnapshotAsync(AdapterInfo adapter, CancellationToken ct)
    {
        try { return await platform.GetSnapshotAsync(adapter, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new(DateTimeOffset.UtcNow, adapter.IsUp, adapter.LinkSpeedBitsPerSecond, -1, -1, -1, -1, -1, -1, adapter.Ipv4Address, adapter.Gateway, ex.Message); }
    }

    private async Task<ProbeResult> SafeProbeAsync(ProbeTarget target, AdapterInfo adapter, CancellationToken ct)
    {
        try { return await platform.ProbeAsync(target, adapter, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new(target.Id, ProbeOutcome.Unavailable, null, ex.Message); }
    }

    private static async Task<IReadOnlyList<ProbeTarget>> CreateTargetsAsync(SessionOptions options, CancellationToken ct)
    {
        List<ProbeTarget> targets = [];
        if (IPAddress.TryParse(options.Adapter.Gateway, out var gateway) && gateway.AddressFamily == AddressFamily.InterNetwork)
            targets.Add(new("gateway", "Local router · ICMP", gateway.ToString(), ProbeKind.GatewayIcmp));
        targets.AddRange([
            new("cloudflare-icmp", "Cloudflare · ICMP", "1.1.1.1", ProbeKind.InternetIcmp),
            new("google-icmp", "Google · ICMP", "8.8.8.8", ProbeKind.InternetIcmp),
            new("cloudflare-tcp", "Cloudflare · TCP 443", "1.1.1.1", ProbeKind.InternetTcp),
            new("google-tcp", "Google · TCP 443", "8.8.8.8", ProbeKind.InternetTcp)
        ]);
        if (!string.IsNullOrWhiteSpace(options.CustomHost))
        {
            if (options.CustomPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(options), "The custom TCP port must be between 1 and 65535.");
            var host = options.CustomHost.Trim();
            if (host.Length > 253 || Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
                throw new ArgumentException("Enter a hostname or IPv4 address, without a URL, path, or port. IPv6 is not supported in this version.", nameof(options));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            IPAddress? address;
            try { address = (await Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ArgumentException("The custom hostname lookup timed out. Try an IPv4 address or check DNS.", nameof(options)); }
            catch (SocketException) { throw new ArgumentException("The custom hostname could not be resolved. Check its spelling and your DNS connection.", nameof(options)); }
            if (address is null) throw new ArgumentException("The custom hostname has no IPv4 address. IPv6 is not supported in this version.", nameof(options));
            targets.Add(new("custom", $"{host} · TCP {options.CustomPort}", address.ToString(), ProbeKind.CustomTcp, options.CustomPort));
        }
        return targets;
    }
}
