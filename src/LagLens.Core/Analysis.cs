namespace LagLens.Core;

/// <summary>Conservative correlation of observations. These findings are hypotheses, never hardware certification.</summary>
public static class Analysis
{
    public const int MinimumRounds = 20;

    public static IReadOnlyList<TargetMetrics> CalculateMetrics(IReadOnlyList<ProbeTarget> targets, IReadOnlyList<SampleRound> rounds)
    {
        return targets.Select(target =>
        {
            // Keep the round positions: a missing result must break a jitter pair just like a timeout.
            var results = rounds.Select(r => r.Probes.FirstOrDefault(p => p.TargetId == target.Id)).ToArray();
            var values = results.Where(IsSuccess).Select(p => p!.Milliseconds!.Value).OrderBy(v => v).ToArray();
            var failures = results.Count(p => p?.Outcome is ProbeOutcome.Timeout or ProbeOutcome.Error);
            var skipped = results.Count(p => p is null || p.Outcome is ProbeOutcome.RouteMismatch or ProbeOutcome.Unavailable || p.Outcome == ProbeOutcome.Success && !IsSuccess(p));
            var differences = new List<double>();
            for (var i = 1; i < results.Length; i++)
            {
                if (IsSuccess(results[i - 1]) && IsSuccess(results[i]))
                    differences.Add(Math.Abs(results[i]!.Milliseconds!.Value - results[i - 1]!.Milliseconds!.Value));
            }
            var attempts = values.Length + failures;
            return new TargetMetrics(target.Id, target.Label, attempts, values.Length, failures, skipped,
                values.Length == 0 ? null : values.Average(), Percentile(values, .5), Percentile(values, .95),
                values.Length == 0 ? null : values[^1], differences.Count == 0 ? null : differences.Average(),
                attempts == 0 ? 0 : 100.0 * failures / attempts);
        }).ToArray();
    }

    public static Assessment Assess(AdapterInfo adapter, IReadOnlyList<ProbeTarget> targets, IReadOnlyList<SampleRound> rounds)
        => AssessWithMetrics(adapter, targets, rounds, CalculateMetrics(targets, rounds));

    internal static Assessment AssessWithMetrics(AdapterInfo adapter, IReadOnlyList<ProbeTarget> targets, IReadOnlyList<SampleRound> rounds, IReadOnlyList<TargetMetrics> metrics)
    {
        var evidence = new List<string>();
        var next = new List<string>();
        if (rounds.Count < MinimumRounds)
            return new(Verdict.NeedMoreData, "Collecting evidence", $"Collect at least {MinimumRounds} samples before drawing a conclusion. Keep the test running while gaming and streaming.",
                new[] { $"{rounds.Count} of {MinimumRounds} minimum samples collected." },
                new[] { "Mark a lag spike or stream drop when it happens. Run for 10–15 minutes to catch intermittent problems." });

        var gateway = targets.FirstOrDefault(t => t.Kind == ProbeKind.GatewayIcmp);
        var internetTcp = targets.Where(t => t.Kind == ProbeKind.InternetTcp).GroupBy(t => t.Address).Select(g => g.First()).ToArray();
        var routeMismatch = rounds.Any(r => r.Probes.Any(p => p.Outcome == ProbeOutcome.RouteMismatch));
        // A disconnected link may temporarily lose its address/gateway; that is a local link event,
        // not evidence that traffic successfully moved to a different path.
        var addressChanged = rounds.Any(r => r.Adapter.Error is null && r.Adapter.IsUp && (r.Adapter.Ipv4Address != adapter.Ipv4Address || r.Adapter.Gateway != adapter.Gateway));
        var unavailable = rounds.Any(r => r.Adapter.Error is not null || r.Adapter.ReceiveErrors < 0 || r.Adapter.SendErrors < 0 || r.Adapter.ReceiveDiscards < 0 || r.Adapter.SendDiscards < 0);
        long receiveErrors = 0, sendErrors = 0, receiveDiscards = 0, sendDiscards = 0;
        var counterReset = false;
        var localHardwareRounds = new HashSet<int>();
        for (var i = 0; i < rounds.Count; i++)
            if (rounds[i].Adapter.Error is null && !rounds[i].Adapter.IsUp) localHardwareRounds.Add(i);
        for (var i = 1; i < rounds.Count; i++)
        {
            var before = rounds[i - 1].Adapter;
            var after = rounds[i].Adapter;
            // A failed query often returns placeholder zero/down fields. Those are not observations.
            if (before.Error is not null || after.Error is not null) continue;
            if (before.IsUp != after.IsUp) localHardwareRounds.Add(i);
            if (!after.IsUp) localHardwareRounds.Add(i);
            if (before.LinkSpeedBitsPerSecond > 0 && after.LinkSpeedBitsPerSecond > 0 && before.LinkSpeedBitsPerSecond != after.LinkSpeedBitsPerSecond)
                localHardwareRounds.Add(i);
            var deltas = new[]
            {
                Delta(before.ReceiveErrors, after.ReceiveErrors), Delta(before.SendErrors, after.SendErrors),
                Delta(before.ReceiveDiscards, after.ReceiveDiscards), Delta(before.SendDiscards, after.SendDiscards)
            };
            if (deltas.Any(d => d < 0)) counterReset = true;
            receiveErrors += Math.Max(0, deltas[0]); sendErrors += Math.Max(0, deltas[1]);
            receiveDiscards += Math.Max(0, deltas[2]); sendDiscards += Math.Max(0, deltas[3]);
            if (deltas.Any(d => d > 0)) localHardwareRounds.Add(i);
        }
        if (rounds.Any(r => r.Adapter.Error is null && !r.Adapter.IsUp)) evidence.Add("The selected adapter was disconnected during this session.");
        if (receiveErrors + sendErrors > 0)
            evidence.Add($"Adapter error counters increased: receive +{receiveErrors}, send +{sendErrors}. This implicates the local adapter/driver/link path, but does not identify the failed part.");
        if (receiveDiscards + sendDiscards > 0)
            evidence.Add($"Adapter discard counters increased: receive +{receiveDiscards}, send +{sendDiscards}. Local buffering, load, driver behavior, or network conditions can cause discards.");
        if (counterReset) evidence.Add("One or more counters reset or became unavailable; negative deltas were excluded.");
        if (adapter.Kind.Contains("wireless", StringComparison.OrdinalIgnoreCase) || adapter.Kind.Contains("wi-fi", StringComparison.OrdinalIgnoreCase))
            evidence.Add("This session uses Wi-Fi. It cannot test the Ethernet port; select the wired adapter and rerun.");
        if (rounds.Any(r => r.SchedulingDelayMs >= 1500))
            evidence.Add("Sampling execution or its schedule stalled by at least 1.5 seconds beyond the expected interval. PC load or sleep can confound latency readings; compare CPU load and OBS rendering/encoding statistics.");
        if (rounds.Where(r => r.Adapter.Error is null && r.Adapter.LinkSpeedBitsPerSecond > 0).Select(r => r.Adapter.LinkSpeedBitsPerSecond).Distinct().Count() > 1)
            evidence.Add("The adapter's reported link speed changed during testing. Inspect the cable, connector, switch port, and driver.");

        if (routeMismatch || addressChanged)
        {
            evidence.Add(routeMismatch ? "A probe's route did not use the selected adapter. VPN, multi-adapter routing, or a route change prevents reliable attribution." : "The selected adapter's IPv4 address or gateway changed. This session spans different network paths.");
            return new(Verdict.Inconclusive, "Network path changed", "The test cannot reliably separate Ethernet from internet latency across a route or address change.", evidence,
                new[] { "Restart the test on the adapter that carries the game and stream. If appropriate, compare a session with the VPN disconnected.", "Check the recorded adapter errors and link events independently; a route change does not rule them out." });
        }

        var localEvents = new HashSet<int>(localHardwareRounds);
        var internetEvents = new HashSet<int>();
        var gatewayOnlyEvents = 0;
        var endpointOnlyEvents = 0;
        var confoundedEvents = 0;
        var gatewayMetric = gateway is null ? null : metrics.First(m => m.TargetId == gateway.Id);
        var thresholds = internetTcp.ToDictionary(t => t.Id, t => InternetThreshold(rounds, t.Id));
        var localCorrelated = new HashSet<int>();
        for (var i = 0; i < rounds.Count; i++)
        {
            var round = rounds[i];
            var g = gateway is null ? null : round.Probes.FirstOrDefault(p => p.TargetId == gateway.Id);
            var gatewayBad = IsBad(g, 20);
            var gatewayClean = IsSuccess(g) && g!.Milliseconds < 20;
            var tcp = internetTcp.Select(t => round.Probes.FirstOrDefault(p => p.TargetId == t.Id)).ToArray();
            var badCount = internetTcp.Where((t, j) => IsBad(tcp[j], thresholds[t.Id])).Count();
            var allBad = internetTcp.Length >= 2 && badCount == internetTcp.Length;
            if (round.SchedulingDelayMs >= 1500 && (gatewayBad || badCount > 0))
            {
                confoundedEvents++;
                continue;
            }
            if (gatewayBad && allBad) localCorrelated.Add(i);
            else if (gatewayBad) gatewayOnlyEvents++;
            if (gatewayClean && allBad && !localHardwareRounds.Contains(i)) internetEvents.Add(i);
            if (badCount > 0 && !allBad) endpointOnlyEvents++;
        }
        var gatewayUsable = gatewayMetric is { Attempts: >= MinimumRounds, Successes: >= MinimumRounds / 2 };
        var tcpUsable = internetTcp.Length >= 2 && internetTcp.All(t => metrics.First(m => m.TargetId == t.Id) is { Attempts: >= MinimumRounds, Successes: >= MinimumRounds / 2 });
        if (localCorrelated.Count >= 2 && gatewayUsable && tcpUsable)
        {
            localEvents.UnionWith(localCorrelated);
            evidence.Add($"{localCorrelated.Count} samples show gateway degradation at the same time as both independent internet TCP probes. The local path is implicated.");
        }
        if (internetEvents.Count >= 2)
            evidence.Add($"{internetEvents.Count} samples show both independent internet TCP probes degraded while the gateway replied below 20 ms. The shared path beyond the gateway is implicated.");
        if (gatewayOnlyEvents > 0)
            evidence.Add($"{gatewayOnlyEvents} samples show gateway ICMP degradation without matching degradation at both TCP endpoints. Router ICMP response priority can cause this; it is not proof of a cable or driver fault.");
        if (endpointOnlyEvents > 0)
            evidence.Add($"{endpointOnlyEvents} samples show degradation at just one public TCP endpoint. Endpoint filtering, server behavior, or an individual route may explain this.");
        if (confoundedEvents > 0)
            evidence.Add($"{confoundedEvents} degraded samples coincided with sampling stalls and were excluded from latency-path attribution. They may reflect PC load, sleep, or a delayed measurement.");

        var totalTcpHealthy = tcpUsable && internetTcp.All(t => metrics.First(m => m.TargetId == t.Id).Failures == 0);
        var publicIcmpTrouble = targets.Where(t => t.Kind == ProbeKind.InternetIcmp).Any(t => metrics.First(m => m.TargetId == t.Id).Failures > 0);
        if (publicIcmpTrouble && totalTcpHealthy)
            evidence.Add("Public ICMP probes failed while TCP connections succeeded. ICMP filtering or response priority may explain this; ICMP loss alone is not counted as an internet outage.");
        if (!gatewayUsable)
            evidence.Add("The gateway did not provide enough ICMP replies to verify the local path. Some routers block or deprioritize ping.");
        if (!tcpUsable)
            evidence.Add("One or both public TCP endpoints did not provide enough successful connections to establish a trustworthy baseline. Blocking or service behavior cannot be excluded.");
        if (unavailable) evidence.Add("Adapter telemetry was unavailable for part of the session; zero reported errors cannot certify a healthy port or driver.");
        evidence.Add($"{rounds.Count} simultaneous samples analyzed. Local spike threshold: 20 ms. Public TCP threshold: 100–150 ms, adjusted from the session's lower-latency samples. Failures count as degradation; skipped probes do not.");
        var localSuspected = localEvents.Count > 0;
        // Established responsive baselines are required before interpreting coincident failures as WAN evidence.
        var internetSuspected = internetEvents.Count >= 2 && gatewayUsable && tcpUsable;
        if (localSuspected)
        {
            next.Add("Swap the Ethernet cable and router/switch port, then repeat with the same workload. Compare results one change at a time.");
            next.Add("Get the Ethernet driver from the PC or motherboard manufacturer's support page. Record the current version before testing an update or rollback.");
            next.Add("Compare a second PC on the same cable/port, or a USB Ethernet adapter on this PC. This separates the PC adapter/driver from the external link.");
            if (internetSuspected)
            {
                next.Add("Separate internet-path events were also observed. Compare idle versus streaming upload and share the time-stamped report with the ISP if they recur.");
                return new(Verdict.Mixed, "Local and internet-path symptoms", "The session contains local link/adapter evidence and separate internet degradation with a responsive gateway. More than one problem may be present.", evidence, next);
            }
            return new(Verdict.LocalNetworkSuspected, "Local network suspected", "The adapter or path between this PC and the router showed symptoms. The evidence cannot distinguish the Ethernet port, driver, cable, switch, or router by itself.", evidence, next);
        }
        if (internetSuspected)
        {
            next.Add("Repeat once while idle and once while streaming. Degradation only during upload suggests congestion or bufferbloat in the router/uplink.");
            next.Add("Compare another wired PC on the same router. If both show the same events, share this report and the exact timestamps with the ISP.");
            next.Add("Check Fortnite's matchmaking region and OBS network dropped-frame count. These public endpoints do not test the actual game server or streaming ingest route.");
            return new(Verdict.InternetPathSuspected, "Internet path suspected", "Both public TCP paths degraded together while the local gateway remained responsive. Router WAN queuing, the modem, ISP, or upstream routing may be involved; an ISP fault is not proven.", evidence, next);
        }
        var requiredTargets = targets.Where(t => t.Kind is ProbeKind.GatewayIcmp or ProbeKind.InternetTcp).ToArray();
        var incomplete = !gatewayUsable || !tcpUsable || unavailable || requiredTargets.Any(t => metrics.First(m => m.TargetId == t.Id).Skipped > 0);
        if (incomplete || confoundedEvents > 0 || gatewayOnlyEvents > 0 || endpointOnlyEvents > 0 || internetEvents.Count > 0 || localCorrelated.Count > 0)
        {
            next.Add("Run for 10–15 minutes while reproducing the lag. Mark the exact moment and compare the gateway and both TCP timelines.");
            next.Add("If gateway ping is blocked, enable ICMP replies on your own router if supported, or use a separate LAN test to a known responsive wired device. A silent gateway cannot certify Ethernet health.");
            next.Add("Compare OBS network dropped frames against encoding/rendering lag. PC load can cause stream stutter without network loss.");
            return new(Verdict.Inconclusive, "More evidence needed", "The observations do not reliably isolate the local Ethernet path from the internet path. Endpoint-specific failures and ICMP behavior can resemble network faults.", evidence, next);
        }
        next.Add("Keep monitoring during gameplay and streaming, and mark any lag or stream drop. A quiet session does not rule out intermittent faults.");
        next.Add("If Fortnite or OBS still lags with healthy probes, inspect the specific game/ingest route, region, CPU/GPU load, and OBS rendering/encoding statistics.");
        return new(Verdict.NoIssueObserved, "No shared network issue observed", "The sampled local and public TCP paths did not show a correlated fault. This does not certify the Ethernet hardware or rule out a game-server, streaming-ingest, or intermittent issue.", evidence, next);
    }

    private static bool IsSuccess(ProbeResult? result) => result?.Outcome == ProbeOutcome.Success && result.Milliseconds is >= 0 && double.IsFinite(result.Milliseconds.Value);
    private static bool IsBad(ProbeResult? result, double threshold) => result is not null && (result.Outcome is ProbeOutcome.Timeout or ProbeOutcome.Error || IsSuccess(result) && result.Milliseconds >= threshold);
    private static long Delta(long before, long after) => before < 0 || after < 0 || after < before ? -1 : after - before;
    private static double InternetThreshold(IReadOnlyList<SampleRound> rounds, string id)
    {
        var times = rounds.SelectMany(r => r.Probes).Where(p => p.TargetId == id && IsSuccess(p)).Select(p => p.Milliseconds!.Value).OrderBy(x => x).ToArray();
        return Math.Max(100, Math.Min(150, (Percentile(times, .2) ?? 50) + 50));
    }
    private static double? Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return null;
        var index = (sorted.Length - 1) * percentile;
        var lower = (int)Math.Floor(index); var upper = (int)Math.Ceiling(index);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (index - lower);
    }
}
