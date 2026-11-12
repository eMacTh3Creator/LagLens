using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using LagLens.Core;

namespace LagLens.Windows;

internal static class SmokeTests
{
    public static int Run(string[] args)
    {
        var output = ReadArgument(args, "--output") ?? Path.Combine(Path.GetTempPath(), "LagLens-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        try
        {
            if (args.Contains("--licenses")) ExportLicenses(output);
            else if (args.Contains("--ui-smoke")) UiSmoke(output);
            else if (args.Contains("--live-test")) LiveTestAsync(output, int.TryParse(ReadArgument(args, "--seconds"), out var seconds) ? Math.Clamp(seconds, 1, 120) : 25).GetAwaiter().GetResult();
            else SelfTestAsync(output).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(output, "PASS.txt"), $"PASS {DateTimeOffset.UtcNow:O}\nProcess architecture: {RuntimeInformation.ProcessArchitecture}\nOS: {RuntimeInformation.OSDescription}\n");
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "FAIL.txt"), ex.ToString()); return 1; }
    }

    private static string? ReadArgument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static async Task SelfTestAsync(string output)
    {
        var checks = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("Self-test failed: " + name); checks.Add("PASS " + name); }
        Check(RuntimeInformation.ProcessArchitecture == Architecture.X64, "Windows x64 process");
        var licenses = ExportLicenses(Path.Combine(output, "licenses"));
        Check(licenses == 4, "Project/runtime licenses and third-party notices embedded in EXE");
        var platform = new WindowsDiagnostics();
        var adapters = await platform.GetAdaptersAsync(CancellationToken.None);
        File.WriteAllText(Path.Combine(output, "adapters.json"), JsonSerializer.Serialize(adapters, JsonSessionRecorder.JsonOptions));
        var loopback = NetworkInterface.GetAllNetworkInterfaces().First(n => n.NetworkInterfaceType == NetworkInterfaceType.Loopback);
        var adapter = new AdapterInfo(loopback.Id, "Loopback test", loopback.Description, "Loopback", true, loopback.Speed,
            loopback.GetIPProperties().GetIPv4Properties()!.Index, "127.0.0.1", "127.0.0.1", false);
        var icmp = await platform.ProbeAsync(new("loopback", "Loopback", "127.0.0.1", ProbeKind.GatewayIcmp), adapter, CancellationToken.None);
        Check(icmp.Outcome == ProbeOutcome.Success, "Native source-bound ICMP loopback: " + icmp.Detail);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var target = new ProbeTarget("tcp-loopback", "TCP loopback", "127.0.0.1", ProbeKind.CustomTcp, ((IPEndPoint)listener.LocalEndpoint).Port);
            var accept = listener.AcceptTcpClientAsync();
            var tcp = await platform.ProbeAsync(target, adapter, CancellationToken.None);
            Check(tcp.Outcome == ProbeOutcome.Success, "Source-bound TCP loopback: " + tcp.Detail);
            using var accepted = await accept.WaitAsync(TimeSpan.FromSeconds(3));
            var wrongRoute = await platform.ProbeAsync(target, adapter with { InterfaceIndex = int.MaxValue }, CancellationToken.None);
            Check(wrongRoute.Outcome == ProbeOutcome.RouteMismatch, "Different adapter route is skipped");
        }
        finally { listener.Stop(); }
        var unavailable = await platform.ProbeAsync(new("invalid", "Invalid", "bad address", ProbeKind.CustomTcp), adapter, CancellationToken.None);
        Check(unavailable.Outcome == ProbeOutcome.Unavailable, "Invalid target is unavailable");
        var view = Demo();
        Check(view.Assessment.Verdict == Verdict.InternetPathSuspected, "Synthetic WAN fault classification");
        using (var recorder = new JsonSessionRecorder(Path.Combine(output, "recording")))
        {
            view = view with { StoragePath = recorder.StoragePath };
            recorder.WriteHeader(view with { Rounds = [], Metrics = [], Markers = [], EndedAt = null });
            foreach (var round in view.Rounds) recorder.WriteRound(round);
            foreach (var marker in view.Markers) recorder.WriteMarker(marker);
            recorder.WriteFinal(view);
            recorder.Dispose();
            var lines = File.ReadAllLines(Path.Combine(recorder.StoragePath, "session.jsonl"));
            Check(lines.Length == view.Rounds.Count + view.Markers.Count + 2, "Recorder persisted every sample and marker");
            foreach (var line in lines) { using var parsed = JsonDocument.Parse(line); }
            var report = ReportExporter.Export(view, Path.Combine(output, "export"));
            Check(File.Exists(report) && File.Exists(Path.Combine(output, "export", "samples.csv")) && File.Exists(Path.Combine(output, "export", "session.jsonl")), "HTML, CSV, JSON and raw-log export");
            var unsafeView = view with { Driver = new("<script>alert(1)</script>", "date", "provider", "file", "notes") };
            var escaped = File.ReadAllText(ReportExporter.Export(unsafeView, Path.Combine(output, "escape-check")));
            Check(!escaped.Contains("<script>", StringComparison.OrdinalIgnoreCase) && escaped.Contains("&lt;script&gt;"), "HTML escapes local metadata");
        }
        if (adapters.FirstOrDefault(a => a.IsUp && a.Ipv4Address is not null) is { } active)
        {
            var snapshot = await platform.GetSnapshotAsync(active, CancellationToken.None);
            Check(snapshot.Error is null, "Real Windows adapter statistics");
            var driver = await platform.GetAdapterDetailsAsync(active, CancellationToken.None);
            File.WriteAllText(Path.Combine(output, "driver.json"), JsonSerializer.Serialize(driver, JsonSessionRecorder.JsonOptions));
            checks.Add("INFO Driver inventory: " + driver.DriverVersion + " / " + driver.Notes);
        }
        File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
    }

    private static async Task LiveTestAsync(string output, int seconds)
    {
        var platform = new WindowsDiagnostics();
        var adapters = await platform.GetAdaptersAsync(CancellationToken.None);
        var adapter = adapters.FirstOrDefault(a => a.IsDefaultRoute && a.IsUp) ?? adapters.First(a => a.IsUp && a.Ipv4Address is not null);
        using var recorder = new JsonSessionRecorder(output);
        var engine = new SessionEngine(platform, recorder);
        var task = engine.RunAsync(new(adapter, TimeSpan.FromSeconds(seconds)), CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(3, seconds)));
        engine.Mark("Automated live smoke marker");
        await task;
        if (engine.Current is not { EndedAt: not null, Rounds.Count: > 0 } view) throw new InvalidOperationException("Live test did not save samples.");
        File.WriteAllText(Path.Combine(output, "live-result.json"), JsonSerializer.Serialize(new { adapter, samples = view.Rounds.Count, view.Assessment, view.Metrics, storage = recorder.StoragePath }, new JsonSerializerOptions(JsonSessionRecorder.JsonOptions) { WriteIndented = true }));
    }

    private static void UiSmoke(string output)
    {
        using var form = new MainForm();
        form.ShowDemo(Demo());
        form.Show();
        Application.DoEvents();
        foreach (var size in new[] { new Size(1140, 850), new Size(1000, 760) })
        {
            form.Size = size;
            form.PerformLayout();
            Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(output, $"LagLens-{size.Width}x{size.Height}.png"));
        }
        var controls = Flatten(form).Select(c => new { c.Name, c.AccessibleName, type = c.GetType().Name, c.Text, c.Left, c.Top, c.Width, c.Height }).ToArray();
        File.WriteAllText(Path.Combine(output, "controls.json"), JsonSerializer.Serialize(controls, new JsonSerializerOptions { WriteIndented = true }));
        if (!controls.Any(c => c.Text == "Start test") || !controls.Any(c => c.type == "ChartControl")) throw new InvalidOperationException("Main UI controls are missing.");
        form.Close();
    }

    private static IEnumerable<Control> Flatten(Control parent)
    {
        foreach (Control child in parent.Controls) { yield return child; foreach (var descendant in Flatten(child)) yield return descendant; }
    }

    private static int ExportLicenses(string output)
    {
        Directory.CreateDirectory(output);
        const string prefix = "LagLens.Licenses.";
        var assembly = typeof(Program).Assembly;
        var names = assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        foreach (var name in names)
        {
            using var source = assembly.GetManifestResourceStream(name)!;
            using var destination = File.Create(Path.Combine(output, name[prefix.Length..]));
            source.CopyTo(destination);
        }
        return names.Length;
    }

    private static SessionView Demo()
    {
        var adapter = new AdapterInfo("demo", "Ethernet", "Intel Ethernet Controller (synthetic demo)", "Ethernet", true, 1_000_000_000, 8, "192.168.1.100", "192.168.1.1", true);
        ProbeTarget[] targets = [new("gateway", "Local router · ICMP", "192.168.1.1", ProbeKind.GatewayIcmp), new("cloudflare-icmp", "Cloudflare · ICMP", "1.1.1.1", ProbeKind.InternetIcmp), new("google-icmp", "Google · ICMP", "8.8.8.8", ProbeKind.InternetIcmp), new("cloudflare-tcp", "Cloudflare · TCP 443", "1.1.1.1", ProbeKind.InternetTcp), new("google-tcp", "Google · TCP 443", "8.8.8.8", ProbeKind.InternetTcp)];
        var start = DateTimeOffset.UtcNow.AddSeconds(-61);
        var rounds = Enumerable.Range(0, 60).Select(i => new SampleRound(i + 1, start.AddSeconds(i), new(start.AddSeconds(i), true, 1_000_000_000, 10000 + i * 1000, 8000 + i * 800, 0, 0, 0, 0, adapter.Ipv4Address, adapter.Gateway), targets.Select(t => new ProbeResult(t.Id, ProbeOutcome.Success, t.Kind == ProbeKind.GatewayIcmp ? 1 + i % 3 : i is >= 30 and <= 36 ? 220 + i % 5 * 12 : 16 + i % 7)).ToArray())).ToArray();
        return new(start, DateTimeOffset.UtcNow, adapter, new("1.2.3 (demo)", "2026-01-01", "Demo provider", "ethernet.sys", "Synthetic UI and report fixture"), targets, rounds, [new(start.AddSeconds(33), "Lag / stream drop (demo)")], Analysis.CalculateMetrics(targets, rounds), Analysis.Assess(adapter, targets, rounds));
    }
}
