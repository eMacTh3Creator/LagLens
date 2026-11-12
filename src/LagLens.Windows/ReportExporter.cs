using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using LagLens.Core;

namespace LagLens.Windows;

public static class ReportExporter
{
    private static string H(string? text) => WebUtility.HtmlEncode(text ?? "Unavailable");
    private static string N(double? value) => value?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—";
    private static string Csv(string? text)
    {
        text ??= "";
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0])) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    public static string Export(SessionView view, string destinationDirectory)
    {
        var directory = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(directory);
        var options = new JsonSerializerOptions(JsonSessionRecorder.JsonOptions) { WriteIndented = true };
        File.WriteAllText(Path.Combine(directory, "session.json"), JsonSerializer.Serialize(view, options), new UTF8Encoding(false));
        var csv = new StringBuilder("timestamp_utc,sequence,target,address,protocol,outcome,latency_ms,detail,link_up,link_mbps,rx_bytes,tx_bytes,rx_errors,tx_errors,rx_discards,tx_discards,scheduling_delay_ms,adapter_error,source_ipv4,gateway\r\n");
        foreach (var round in view.Rounds)
        foreach (var probe in round.Probes)
        {
            var target = view.Targets.FirstOrDefault(t => t.Id == probe.TargetId);
            csv.AppendLine(string.Join(",", Csv(round.Timestamp.ToString("O")), round.Sequence.ToString(CultureInfo.InvariantCulture), Csv(target?.Label ?? probe.TargetId), Csv(target?.Address), Csv(target?.Kind.ToString()), Csv(probe.Outcome.ToString()), probe.Milliseconds?.ToString("0.###", CultureInfo.InvariantCulture) ?? "", Csv(probe.Detail), round.Adapter.Error is not null ? "" : round.Adapter.IsUp ? "true" : "false", (round.Adapter.LinkSpeedBitsPerSecond / 1_000_000d).ToString("0.###", CultureInfo.InvariantCulture), round.Adapter.ReceivedBytes.ToString(CultureInfo.InvariantCulture), round.Adapter.SentBytes.ToString(CultureInfo.InvariantCulture), round.Adapter.ReceiveErrors.ToString(CultureInfo.InvariantCulture), round.Adapter.SendErrors.ToString(CultureInfo.InvariantCulture), round.Adapter.ReceiveDiscards.ToString(CultureInfo.InvariantCulture), round.Adapter.SendDiscards.ToString(CultureInfo.InvariantCulture), round.SchedulingDelayMs.ToString("0.###", CultureInfo.InvariantCulture), Csv(round.Adapter.Error), Csv(round.Adapter.Ipv4Address), Csv(round.Adapter.Gateway)));
        }
        File.WriteAllText(Path.Combine(directory, "samples.csv"), csv.ToString(), new UTF8Encoding(false));
        if (view.StoragePath is { } source)
        {
            var sourceFile = Path.Combine(source, "session.jsonl");
            var destinationFile = Path.Combine(directory, "session.jsonl");
            if (!string.Equals(Path.GetFullPath(sourceFile), destinationFile, StringComparison.OrdinalIgnoreCase) && File.Exists(sourceFile))
                File.Copy(sourceFile, destinationFile, true);
        }
        var html = new StringBuilder("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>LagLens session report</title><style>body{background:#101827;color:#e8eef7;font:16px/1.6 system-ui,sans-serif;max-width:1100px;margin:auto;padding:32px}h1,h2{line-height:1.2}h1{color:#62dfca}section{background:#192538;border:1px solid #30425c;border-radius:12px;padding:22px;margin:20px 0}p,li{overflow-wrap:anywhere}table{border-collapse:collapse;width:100%;font-size:14px}th,td{text-align:left;padding:9px;border-bottom:1px solid #30425c}small,.muted{color:#b2c0d2}.scroll{overflow:auto}svg{width:100%;height:auto}a{color:#62dfca}@media print{body{background:white;color:#152135}section{background:white}small,.muted{color:#35455b}}</style></head><body><h1>LagLens</h1><p class=\"muted\">Windows network latency evidence · v1.0.0 · All timestamps below are UTC.</p>");
        html.Append($"<section><h2>{H(view.Assessment.Title)}</h2><p>{H(view.Assessment.Summary)}</p><ul>");
        foreach (var evidence in view.Assessment.Evidence) html.Append($"<li>{H(evidence)}</li>");
        html.Append("</ul><h3>Next steps</h3><ol>");
        foreach (var step in view.Assessment.NextSteps) html.Append($"<li>{H(step)}</li>");
        html.Append("</ol></section>");
        html.Append($"<section><h2>Session and Ethernet adapter</h2><p>Started: {H(view.StartedAt.ToString("O"))}<br>Ended: {H(view.EndedAt?.ToString("O") ?? "Still running — partial export")}<br>Rounds: {view.Rounds.Count}<br>Adapter: {H(view.Adapter.Name)} — {H(view.Adapter.Description)}<br>Type: {H(view.Adapter.Kind)}<br>Source IPv4: {H(view.Adapter.Ipv4Address)} · Gateway: {H(view.Adapter.Gateway)}<br>Interface index: {view.Adapter.InterfaceIndex} · Initial link speed: {N(view.Adapter.LinkSpeedBitsPerSecond / 1_000_000d)} Mbps<br>Driver version: {H(view.Driver?.DriverVersion)}<br>Driver date: {H(view.Driver?.DriverDate)}<br>Driver provider: {H(view.Driver?.DriverProvider)}<br>Driver file: {H(view.Driver?.DriverFile)}<br>{H(view.Driver?.Notes)}</p>");
        if (view.Rounds.Count > 0)
        {
            var first = view.Rounds[0].Adapter;
            var last = view.Rounds[^1].Adapter;
            string Delta(long initial, long final) => first.Error is not null || last.Error is not null || initial < 0 || final < 0 ? "Unavailable — inspect samples.csv" : final >= initial ? (final - initial).ToString(CultureInfo.InvariantCulture) : "Counter reset — inspect samples.csv";
            html.Append($"<p>First-to-last counter change: RX errors {Delta(first.ReceiveErrors, last.ReceiveErrors)} · TX errors {Delta(first.SendErrors, last.SendErrors)} · RX discards {Delta(first.ReceiveDiscards, last.ReceiveDiscards)} · TX discards {Delta(first.SendDiscards, last.SendDiscards)}. Cumulative counter values are in samples.csv; resets and intermediate increases are evaluated separately.</p>");
        }
        html.Append("</section><section><h2>Latency and connection attempts</h2><p>Loss is the fraction of attempted probes that timed out or errored. Skipped probes (routing / missing address) are excluded. TCP failures can also reflect endpoint policy. p95 is the 95th percentile; jitter is the mean absolute difference of consecutive successful samples, with gaps excluded.</p><div class=\"scroll\"><table><thead><tr><th>Target</th><th>Address / protocol</th><th>Attempts</th><th>Skipped</th><th>Loss</th><th>Average</th><th>p95</th><th>Max</th><th>Jitter</th></tr></thead><tbody>");
        foreach (var m in view.Metrics)
        {
            var target = view.Targets.FirstOrDefault(t => t.Id == m.TargetId);
            html.Append($"<tr><td>{H(m.Label)}</td><td>{H(target?.Address)}{(target?.Kind is ProbeKind.InternetTcp or ProbeKind.CustomTcp ? ":" + target.Port : "")} · {H(target?.Kind.ToString())}</td><td>{m.Attempts}</td><td>{m.Skipped}</td><td>{N(m.LossPercent)}%</td><td>{N(m.AverageMs)} ms</td><td>{N(m.P95Ms)} ms</td><td>{N(m.MaxMs)} ms</td><td>{N(m.JitterMs)} ms</td></tr>");
        }
        html.Append("</tbody></table></div></section><section><h2>Latency timeline</h2><p>Last 300 rounds. Router is teal; Cloudflare TCP is blue; Google TCP is gold. Missing replies leave gaps. Spikes above 500 ms are clipped to the chart ceiling.</p>");
        html.Append(ChartSvg(view));
        html.Append("</section><section><h2>Lag and stream-drop markers</h2><ul>");
        if (view.Markers.Count == 0) html.Append("<li>No manual markers recorded.</li>");
        foreach (var marker in view.Markers) html.Append($"<li>{H(marker.Timestamp.ToString("O"))} — {H(marker.Label)}</li>");
        html.Append("</ul></section><section><h2>How to interpret this report</h2><p>This is an observation of the selected IPv4 network path. It cannot certify that an Ethernet port, cable, router, driver, or ISP is fault-free. Gateway ICMP may be blocked or deprioritized. Zero adapter errors do not rule out a driver fault. Internet TCP handshakes are independent controls, not Fortnite UDP or the stream ingest connection. IPv6, game server load, OBS encoding overload and GPU/CPU stalls require separate checks.</p><p>Compare an idle run with a run during gaming and streaming. A clean router with simultaneous degradation to two public TCP endpoints points beyond the local link, including router WAN queues / bufferbloat, the ISP, or upstream transit. Swap one cable / router port at a time and repeat before assigning blame. Driver dates alone do not show a driver is faulty or outdated.</p><p>Report contains local IP addresses, adapter and driver details, and timestamps. Review it before sharing. No credentials, packet contents or browsing history are collected. Tests contact Cloudflare and Google; a custom TCP destination may also be contacted.</p></section></body></html>");
        var path = Path.Combine(directory, "report.html");
        File.WriteAllText(path, html.ToString(), new UTF8Encoding(false));
        return path;
    }

    private static string ChartSvg(SessionView view)
    {
        var rounds = view.Rounds.TakeLast(300).ToArray();
        var svg = new StringBuilder("<svg viewBox=\"0 0 1000 260\" role=\"img\" aria-label=\"Latency over the final 300 sample rounds\"><rect x=\"0\" y=\"0\" width=\"1000\" height=\"260\" fill=\"#101827\"/>");
        foreach (var ms in new[] { 0, 100, 250, 500 })
        {
            var y = 230 - ms / 500d * 210;
            svg.Append(CultureInfo.InvariantCulture, $"<line x1=\"50\" y1=\"{y:0.0}\" x2=\"990\" y2=\"{y:0.0}\" stroke=\"#30425c\"/><text x=\"4\" y=\"{y + 5:0.0}\" fill=\"#b2c0d2\" font-size=\"13\">{ms}</text>");
        }
        var targets = view.Targets.Where(t => t.Kind == ProbeKind.GatewayIcmp).Concat(view.Targets.Where(t => t.Kind == ProbeKind.InternetTcp)).Take(3).ToArray();
        for (var t = 0; t < targets.Length; t++)
        {
            var color = targets[t].Kind == ProbeKind.GatewayIcmp ? "#62dfca" : targets[t].Address == "1.1.1.1" ? "#75b5ff" : "#f7ce70";
            var segment = new List<string>();
            void Flush() { if (segment.Count > 0) svg.Append($"<polyline fill=\"none\" stroke=\"{color}\" stroke-width=\"2\" points=\"{string.Join(" ", segment)}\"/>"); segment.Clear(); }
            for (var i = 0; i < rounds.Length; i++)
            {
                var value = rounds[i].Probes.FirstOrDefault(p => p.TargetId == targets[t].Id);
                if (value is { Outcome: ProbeOutcome.Success, Milliseconds: { } ms })
                    segment.Add(FormattableString.Invariant($"{50 + 940d * i / Math.Max(1, rounds.Length - 1):0.0},{230 - Math.Min(ms, 500) / 500d * 210:0.0}"));
                else Flush();
            }
            Flush();
        }
        return svg.Append("</svg>").ToString();
    }
}
