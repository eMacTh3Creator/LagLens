using System.Diagnostics;
using LagLens.Core;

namespace LagLens.Windows;

public sealed class MainForm : Form
{
    private static readonly Color BackgroundColor = Color.FromArgb(12, 20, 32);
    private static readonly Color CardColor = Color.FromArgb(20, 30, 45);
    private static readonly Color InputColor = Color.FromArgb(29, 43, 60);
    private static readonly Color TextColor = Color.FromArgb(231, 239, 249);
    private static readonly Color MutedColor = Color.FromArgb(164, 183, 205);
    private static readonly Color AccentColor = Color.FromArgb(48, 219, 184);
    private readonly WindowsDiagnostics _platform = new();
    private readonly ComboBox _adapter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "Network adapter" };
    private readonly ComboBox _duration = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "Test duration" };
    private readonly TextBox _customHost = new() { PlaceholderText = "Optional: your stream endpoint or host", Dock = DockStyle.Fill, AccessibleName = "Optional custom TCP hostname" };
    private readonly NumericUpDown _customPort = new() { Minimum = 1, Maximum = 65535, Value = 443, Dock = DockStyle.Fill, AccessibleName = "Custom TCP port" };
    private readonly Button _refresh = MakeButton("Refresh", false);
    private readonly Button _start = MakeButton("Start test", true);
    private readonly Button _stop = MakeButton("Stop", false);
    private readonly Button _mark = MakeButton("Mark lag / stream drop", false);
    private readonly Button _export = MakeButton("Export report", false);
    private readonly Button _sessions = MakeButton("Open sessions", false);
    private readonly Label _status = MakeLabel("Ready to test", 10, MutedColor);
    private readonly Label _elapsed = MakeLabel("0 rounds · 0 markers", 9, MutedColor);
    private readonly Label _verdict = MakeLabel("Find where the lag starts", 19, TextColor, true);
    private readonly Label _summary = MakeLabel("Choose the Ethernet adapter, then start a test. Allow at least 20 rounds for an initial assessment.", 10, MutedColor);
    private readonly Label _adapterInfo = MakeLabel("Adapter and driver details will appear when a test starts.", 9, MutedColor);
    private readonly Label _evidence = MakeLabel("• Router latency is compared with two independent internet endpoints.\n\n• Ethernet link status and error counters are recorded alongside each round.", 10, TextColor);
    private readonly Label _nextSteps = MakeLabel("1. Test for a few minutes while idle.\n\n2. Start Fortnite and your usual stream.\n\n3. Click Mark lag / stream drop when a problem happens, then export the report.", 10, MutedColor);
    private readonly Label _activity = MakeLabel("No test running", 9, MutedColor);
    private readonly ChartControl _chart = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _metrics = new();
    private SessionEngine? _engine;
    private JsonSessionRecorder? _recorder;
    private SessionView? _view;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private bool _closingAfterStop;
    private bool _demo;
    private bool _loadingAdapters;

    public MainForm()
    {
        Text = "LagLens — Ethernet & Internet Diagnostics";
        BackColor = BackgroundColor;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(1000, 760);
        ClientSize = new Size(1140, 850);
        StartPosition = FormStartPosition.CenterScreen;
        BuildInterface();
        _duration.Items.AddRange(["5 minutes", "15 minutes", "30 minutes", "Monitor (up to 4 hours)"]);
        _duration.SelectedIndex = 1;
        _stop.Enabled = _mark.Enabled = _export.Enabled = false;
        _start.Enabled = false;
        _refresh.Click += async (_, _) => await LoadAdaptersAsync();
        _start.Click += async (_, _) => await StartAsync();
        _stop.Click += (_, _) => { _stop.Enabled = false; _status.Text = "Stopping and saving…"; _runCancellation?.Cancel(); };
        _mark.Click += (_, _) =>
        {
            try
            {
                _engine?.Mark("Lag / stream drop");
                _activity.Text = $"Marker recorded at {DateTime.Now:t}";
            }
            catch (Exception ex)
            {
                _runCancellation?.Cancel();
                ShowError("Could not save the lag marker", ex);
            }
        };
        _export.Click += (_, _) => ExportReport();
        _sessions.Click += (_, _) => OpenSessions();
        Shown += async (_, _) => { if (!_demo) await LoadAdaptersAsync(); };
        FormClosing += OnClosing;
        _adapter.SelectedIndexChanged += (_, _) => UpdateSelectedAdapter();
    }

    internal void ShowDemo(SessionView view)
    {
        _demo = true;
        _adapter.Items.Clear();
        _adapter.Items.Add(view.Adapter);
        _adapter.SelectedIndex = 0;
        ApplyView(view);
        _status.Text = "Demo session · saved";
        _start.Enabled = true;
        _export.Enabled = true;
    }

    internal SessionView? SessionForSmoke => _view;

    private void BuildInterface()
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(22, 16, 22, 12), BackColor = BackgroundColor };
        var root = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 7, Margin = Padding.Empty, Padding = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 454));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 67));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        scroll.Controls.Add(root);
        Controls.Add(scroll);

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 74));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        var brand = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        brand.Controls.Add(MakeLabel("LagLens", 25, TextColor, true));
        brand.Controls.Add(MakeLabel("ETHERNET & INTERNET DIAGNOSTICS", 8, AccentColor, true));
        header.Controls.Add(brand, 0, 0);
        var right = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(0, 9, 0, 0), Margin = Padding.Empty };
        right.Controls.Add(MakeLabel("WINDOWS x64  /  v1.0.0", 9, MutedColor, true));
        right.Controls.Add(_elapsed);
        right.Controls.Add(_status);
        header.Controls.Add(right, 1, 0);
        root.Controls.Add(header, 0, 0);

        var intro = MakeLabel("Test while gaming and streaming. Compare your router with the internet, then mark the moments you feel lag.", 10, MutedColor);
        intro.Dock = DockStyle.Fill;
        root.Controls.Add(intro, 0, 1);
        root.Controls.Add(BuildSetup(), 0, 2);

        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(0, 7, 0, 6), WrapContents = false };
        controls.Controls.AddRange([_start, _stop, _mark, _export, _sessions]);
        root.Controls.Add(controls, 0, 3);

        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 12, 0) };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 236));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var chartCard = Card();
        var chartGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10, 8, 10, 4) };
        chartGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        chartGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        chartGrid.Controls.Add(MakeLabel("LIVE LATENCY  ·  LAST 120 ROUNDS", 9, MutedColor, true), 0, 0);
        chartGrid.Controls.Add(_chart, 0, 1);
        chartCard.Controls.Add(chartGrid);
        left.Controls.Add(chartCard, 0, 0);
        var tableCard = Card();
        tableCard.Margin = new Padding(0, 10, 0, 0);
        var tableGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10, 8, 10, 4) };
        tableGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        tableGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        tableGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        tableGrid.Controls.Add(MakeLabel("SESSION TOTALS", 9, MutedColor, true), 0, 0);
        ConfigureMetrics();
        tableGrid.Controls.Add(_metrics, 0, 1);
        var tableNote = MakeLabel("P95: 95% of replies are faster. Failed probes; skips excluded.", 8, MutedColor);
        tableGrid.Controls.Add(tableNote, 0, 2);
        tableCard.Controls.Add(tableGrid);
        left.Controls.Add(tableCard, 0, 1);
        content.Controls.Add(left, 0, 0);
        content.Controls.Add(BuildAssessment(), 1, 0);
        root.Controls.Add(content, 0, 4);

        var detailCard = Card();
        detailCard.Margin = new Padding(0, 10, 0, 0);
        _adapterInfo.Dock = DockStyle.Fill;
        _adapterInfo.AutoSize = false;
        _adapterInfo.Padding = new Padding(11, 8, 11, 3);
        detailCard.Controls.Add(_adapterInfo);
        root.Controls.Add(detailCard, 0, 5);
        var limitations = MakeLabel("Results narrow down the likely area; they cannot prove a specific cable, port or driver fault. Public probes do not test Fortnite servers or your actual stream transport. Reports stay on this PC until you share them.", 8, MutedColor);
        limitations.Dock = DockStyle.Fill;
        limitations.Padding = new Padding(0, 9, 0, 0);
        root.Controls.Add(limitations, 0, 6);
    }

    private Control BuildSetup()
    {
        var setup = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 2, BackColor = CardColor, Padding = new Padding(10, 8, 10, 7), Margin = Padding.Empty };
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 83));
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 81));
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        setup.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        setup.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        setup.Controls.Add(MakeLabel("Adapter", 9, MutedColor), 0, 0);
        setup.Controls.Add(_adapter, 1, 0);
        setup.Controls.Add(MakeLabel("Duration", 9, MutedColor), 2, 0);
        setup.Controls.Add(_duration, 3, 0);
        _refresh.Dock = DockStyle.Fill;
        _refresh.Margin = new Padding(6, 0, 0, 4);
        setup.Controls.Add(_refresh, 4, 0);
        setup.Controls.Add(MakeLabel("Extra host", 9, MutedColor), 0, 1);
        setup.Controls.Add(_customHost, 1, 1);
        setup.Controls.Add(MakeLabel("TCP port", 9, MutedColor), 2, 1);
        setup.Controls.Add(_customPort, 3, 1);
        foreach (var input in new Control[] { _adapter, _duration, _customHost, _customPort })
        {
            input.BackColor = InputColor;
            input.ForeColor = TextColor;
            input.Margin = new Padding(0, 0, 7, 5);
            input.Font = new Font("Segoe UI", 9);
        }
        ConfigureCombo(_adapter);
        ConfigureCombo(_duration);
        return setup;
    }

    private Control BuildAssessment()
    {
        var card = Card();
        var assessmentScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(15, 12, 15, 10) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var labels = new Label[] { MakeLabel("LIKELY SOURCE", 9, MutedColor, true), _verdict, _summary, MakeLabel("EVIDENCE", 9, AccentColor, true), _evidence, MakeLabel("WHAT TO TRY NEXT", 9, AccentColor, true), _nextSteps, _activity };
        for (int i = 0; i < labels.Length; i++)
        {
            var label = labels[i];
            label.Dock = DockStyle.Top;
            label.Margin = new Padding(0, i is 3 or 5 ? 15 : 0, 0, i == 0 ? 7 : 10);
            label.MaximumSize = new Size(360, 0);
            layout.Controls.Add(label, 0, i);
        }
        assessmentScroll.Resize += (_, _) =>
        {
            var width = Math.Max(180, assessmentScroll.ClientSize.Width - assessmentScroll.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 2);
            foreach (var label in labels) label.MaximumSize = new Size(width, 0);
        };
        assessmentScroll.Controls.Add(layout);
        card.Controls.Add(assessmentScroll);
        return card;
    }

    private void ConfigureMetrics()
    {
        _metrics.Dock = DockStyle.Fill;
        _metrics.BackgroundColor = CardColor;
        _metrics.BorderStyle = BorderStyle.None;
        _metrics.ReadOnly = true;
        _metrics.AllowUserToAddRows = _metrics.AllowUserToDeleteRows = _metrics.AllowUserToResizeRows = false;
        _metrics.RowHeadersVisible = false;
        _metrics.MultiSelect = false;
        _metrics.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _metrics.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _metrics.EnableHeadersVisualStyles = false;
        _metrics.GridColor = Color.FromArgb(38, 53, 72);
        _metrics.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _metrics.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _metrics.DefaultCellStyle = new DataGridViewCellStyle { BackColor = CardColor, ForeColor = TextColor, SelectionBackColor = InputColor, SelectionForeColor = TextColor, Font = new Font("Segoe UI", 8.5f), Padding = new Padding(2, 0, 2, 0) };
        _metrics.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = CardColor, ForeColor = MutedColor, Font = new Font("Segoe UI", 8, FontStyle.Bold), Padding = new Padding(2) };
        _metrics.ColumnHeadersHeight = 26;
        _metrics.RowTemplate.Height = 24;
        _metrics.AccessibleName = "Latency and failed probes by target";
        foreach (var pair in new[] { ("Target", 39f), ("P50 ms", 15f), ("P95 ms", 15f), ("Jitter", 15f), ("Fail %", 16f) })
        {
            _metrics.Columns.Add(new DataGridViewTextBoxColumn { Name = pair.Item1, HeaderText = pair.Item1, FillWeight = pair.Item2, SortMode = DataGridViewColumnSortMode.NotSortable });
        }
    }

    private async Task LoadAdaptersAsync()
    {
        if (_loadingAdapters || _runCancellation is not null) return;
        _loadingAdapters = true;
        _refresh.Enabled = _start.Enabled = false;
        _status.Text = "Finding adapters…";
        try
        {
            var previousId = (_adapter.SelectedItem as AdapterInfo)?.Id;
            var adapters = await _platform.GetAdaptersAsync(CancellationToken.None);
            if (IsDisposed) return;
            _adapter.Items.Clear();
            foreach (var adapter in adapters.OrderByDescending(a => a.IsDefaultRoute).ThenByDescending(a => a.IsUp)) _adapter.Items.Add(adapter);
            if (_adapter.Items.Count > 0)
            {
                var index = adapters.Any(a => a.Id == previousId) ? _adapter.Items.Cast<AdapterInfo>().ToList().FindIndex(a => a.Id == previousId) : 0;
                _adapter.SelectedIndex = Math.Max(0, index);
                _status.Text = "Ready";
            }
            else _status.Text = "No IPv4 adapter";
        }
        catch (Exception ex)
        {
            _status.Text = "Adapter scan failed";
            ShowError("Could not find network adapters", ex);
        }
        finally
        {
            _loadingAdapters = false;
            if (!IsDisposed) { _refresh.Enabled = true; _start.Enabled = _adapter.SelectedItem is AdapterInfo; }
        }
    }

    private void UpdateSelectedAdapter()
    {
        if (_adapter.SelectedItem is not AdapterInfo adapter || _runCancellation is not null) return;
        var caution = adapter.Kind.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) ? "Select the adapter used for your game and stream." : "This adapter may be Wi-Fi or virtual. Select physical Ethernet to investigate the wired path.";
        if (_view is not null && _view.Adapter.Id != adapter.Id)
        {
            _activity.Text = $"Selected {adapter.Name} for the next test. Results above still belong to {_view.Adapter.Name}.";
            caution = $"Results above belong to {_view.Adapter.Name}. Start a new test to collect evidence for {adapter.Name}.";
        }
        _adapterInfo.Text = $"{adapter.Description}  ·  {adapter.Kind}  ·  IPv4 {adapter.Ipv4Address ?? "unavailable"}  ·  Router {adapter.Gateway ?? "unavailable"}\n{caution}";
    }

    private async Task StartAsync()
    {
        if (_runCancellation is not null || _adapter.SelectedItem is not AdapterInfo adapter) return;
        var host = _customHost.Text.Trim();
        if (host.Length > 253 || host.Contains('/') || host.Contains(':') || host.Any(char.IsWhiteSpace))
        {
            MessageBox.Show(this, "Enter a hostname or IPv4 address without a URL, path or port. Set the port in the TCP port box.", "Check custom host", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _customHost.Focus();
            return;
        }
        if (host.Length > 0 && Uri.CheckHostName(host) is not UriHostNameType.Dns and not UriHostNameType.IPv4)
        {
            MessageBox.Show(this, "Enter a valid hostname or IPv4 address for the optional TCP test.", "Check custom host", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _view = null;
        UpdateSelectedAdapter();
        _runCancellation = new CancellationTokenSource();
        SetRunning(true);
        _status.Text = "Starting…";
        _verdict.Text = "Collecting evidence";
        _verdict.ForeColor = TextColor;
        _summary.Text = "Keep this running while you play and stream. At least 20 rounds are needed for an initial assessment.";
        _evidence.Text = "Router and internet probes will appear as each simultaneous round completes.";
        _nextSteps.Text = "1. Play and stream as usual.\n\n2. Mark the moments you feel lag, then stop and export the report.";
        _elapsed.Text = "0 rounds · 0 markers";
        _activity.Text = "Use Mark lag / stream drop to timestamp a problem.";
        _metrics.Rows.Clear();
        _chart.SetSession(null);
        try
        {
            _recorder = new JsonSessionRecorder();
            _engine = new SessionEngine(_platform, _recorder);
            _engine.Updated += OnSessionUpdated;
            TimeSpan? duration = _duration.SelectedIndex switch { 0 => TimeSpan.FromMinutes(5), 1 => TimeSpan.FromMinutes(15), 2 => TimeSpan.FromMinutes(30), _ => null };
            _runTask = _engine.RunAsync(new SessionOptions(adapter, duration, string.IsNullOrEmpty(host) ? null : host, (int)_customPort.Value), _runCancellation.Token);
            await _runTask;
            if (_engine.Current is SessionView current)
            {
                ApplyView(current);
                _status.Text = current.Rounds.Count > 0 ? "Saved" : "Stopped before samples";
                _activity.Text = current.Rounds.Count > 0 ? "Session saved locally. Export a report to review or share." : "No samples were recorded. Start another test when ready.";
            }
            else ShowStoppedBeforeSamples();
        }
        catch (OperationCanceledException)
        {
            if (_engine?.Current is SessionView current)
            {
                ApplyView(current);
                _status.Text = current.Rounds.Count > 0 ? "Stopped · saved" : "Stopped before samples";
            }
            else ShowStoppedBeforeSamples();
        }
        catch (Exception ex)
        {
            if (_engine?.Current is SessionView current) ApplyView(current);
            _status.Text = "Test stopped";
            _activity.Text = "The test stopped early. Any recorded rounds remain in the session folder.";
            if (!_closingAfterStop) ShowError("Could not complete this test", ex);
        }
        finally
        {
            if (_engine is not null) _engine.Updated -= OnSessionUpdated;
            _recorder?.Dispose();
            _recorder = null;
            _runCancellation.Dispose();
            _runCancellation = null;
            _runTask = null;
            if (!IsDisposed) SetRunning(false);
            if (_closingAfterStop && !IsDisposed) BeginInvoke(Close);
        }
    }

    private void OnSessionUpdated(SessionView view)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(() => { if (!IsDisposed) ApplyView(view); }); }
            catch (InvalidOperationException) { }
            return;
        }
        ApplyView(view);
    }

    private void ApplyView(SessionView view)
    {
        _view = view;
        _verdict.Text = view.Assessment.Title;
        _verdict.ForeColor = view.Assessment.Verdict switch
        {
            Verdict.NoIssueObserved => AccentColor,
            Verdict.InternetPathSuspected => ChartControl.CloudColor,
            Verdict.LocalNetworkSuspected or Verdict.Mixed => Color.FromArgb(250, 163, 110),
            _ => TextColor
        };
        _summary.Text = view.Assessment.Summary;
        _evidence.Text = string.Join("\n\n", view.Assessment.Evidence.Select(item => $"• {item}"));
        _nextSteps.Text = string.Join("\n\n", view.Assessment.NextSteps.Select((item, i) => $"{i + 1}. {item}"));
        var end = view.EndedAt ?? DateTimeOffset.Now;
        var elapsed = end - view.StartedAt;
        _elapsed.Text = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00} elapsed · {view.Rounds.Count:N0} rounds · {view.Markers.Count} markers";
        if (_runCancellation is not null) _status.Text = "Recording";
        _metrics.Rows.Clear();
        foreach (var metric in view.Metrics)
        {
            var target = view.Targets.FirstOrDefault(t => t.Id == metric.TargetId);
            var shortLabel = target is null ? metric.Label : target.Kind switch
            {
                ProbeKind.GatewayIcmp => "Router · ping",
                ProbeKind.InternetTcp => target.Address == "1.1.1.1" ? "Cloudflare · TCP" : "Google · TCP",
                ProbeKind.InternetIcmp => target.Address == "1.1.1.1" ? "Cloudflare · ping" : "Google · ping",
                _ => "Custom · TCP"
            };
            var loss = metric.Attempts > 0 ? $"{metric.LossPercent:0.#}%" : "—";
            var index = _metrics.Rows.Add(shortLabel, FormatMs(metric.P50Ms), FormatMs(metric.P95Ms), FormatMs(metric.JitterMs), loss);
            _metrics.Rows[index].Cells[0].ToolTipText = metric.Label;
            _metrics.Rows[index].Cells[4].ToolTipText = $"{metric.Failures} failed / {metric.Attempts} attempts; {metric.Skipped} skipped. Failures include timeouts and network errors.";
            if (metric.Failures > 0) _metrics.Rows[index].Cells[4].Style.ForeColor = Color.FromArgb(250, 163, 110);
        }
        _metrics.ClearSelection();
        _chart.SetSession(view);
        var last = view.Rounds.LastOrDefault()?.Adapter;
        var speed = last?.LinkSpeedBitsPerSecond ?? view.Adapter.LinkSpeedBitsPerSecond;
        var driver = view.Driver is null ? "Driver details pending" : $"Driver {view.Driver.DriverVersion} · {view.Driver.DriverProvider} · {view.Driver.DriverDate}";
        _adapterInfo.Text = $"{view.Adapter.Name}  ·  {view.Adapter.Kind}  ·  {speed / 1_000_000d:0.#} Mbps link  ·  IPv4 {last?.Ipv4Address ?? view.Adapter.Ipv4Address ?? "unavailable"}  ·  Router {last?.Gateway ?? view.Adapter.Gateway ?? "unavailable"}\n{driver}";
        _export.Enabled = view.Rounds.Count > 0 && _runCancellation is null;
        if (view.Markers.Count > 0 && _runCancellation is not null)
            _activity.Text = $"Last marker: {view.Markers[^1].Timestamp.LocalDateTime:t} · {view.Markers[^1].Label}";
    }

    private void SetRunning(bool running)
    {
        _adapter.Enabled = _duration.Enabled = _customHost.Enabled = _customPort.Enabled = _refresh.Enabled = !running;
        _start.Enabled = !running && _adapter.SelectedItem is AdapterInfo;
        _stop.Enabled = _mark.Enabled = running;
        _export.Enabled = !running && _view?.Rounds.Count > 0;
    }

    private void ShowStoppedBeforeSamples()
    {
        _status.Text = "Stopped before samples";
        _verdict.Text = "No samples recorded";
        _summary.Text = "The test stopped during setup. Start another test when ready.";
        _activity.Text = "No diagnostic conclusion is available for this attempt.";
    }

    private void ExportReport()
    {
        if (_view is null || _view.Rounds.Count == 0) return;
        using var dialog = new FolderBrowserDialog { Description = "Choose a folder for the LagLens HTML report, CSV and raw session data", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var path = ReportExporter.Export(_view, dialog.SelectedPath);
            _activity.Text = $"Report exported to {Path.GetDirectoryName(path)}";
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError("Could not export the report", ex); }
    }

    private void OpenSessions()
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LagLens", "Sessions");
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError("Could not open the session folder", ex); }
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_runCancellation is null) return;
        e.Cancel = true;
        _closingAfterStop = true;
        _runCancellation.Cancel();
        _status.Text = "Saving before close…";
        _stop.Enabled = _mark.Enabled = false;
    }

    private void ShowError(string title, Exception ex) => MessageBox.Show(this, $"{ex.Message}\n\nExisting session files are kept under Local AppData > LagLens > Sessions. If this was a network test, refresh the adapter list before trying again.", title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    private static string FormatMs(double? value) => value.HasValue ? value.Value.ToString("0.#") : "—";
    private static Label MakeLabel(string text, float size, Color color, bool bold = false)
    {
        var label = new Label
        {
            Text = text, AutoSize = true, ForeColor = color, BackColor = Color.Transparent,
            Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = Padding.Empty,
            AccessibleName = text
        };
        label.TextChanged += (_, _) => label.AccessibleName = label.Text;
        return label;
    }

    private static void ConfigureCombo(ComboBox combo)
    {
        combo.DrawMode = DrawMode.OwnerDrawFixed;
        combo.FlatStyle = FlatStyle.Flat;
        combo.ItemHeight = 22;
        combo.DrawItem += (_, e) =>
        {
            using var background = new SolidBrush(InputColor);
            e.Graphics.FillRectangle(background, e.Bounds);
            var text = e.Index >= 0 && e.Index < combo.Items.Count ? combo.GetItemText(combo.Items[e.Index]) : combo.Text;
            TextRenderer.DrawText(e.Graphics, text, combo.Font, Rectangle.Inflate(e.Bounds, -4, 0), combo.Enabled ? TextColor : MutedColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        };
    }
    private static Button MakeButton(string text, bool primary) => new()
    {
        Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Height = 34, MinimumSize = new Size(65, 34),
        FlatStyle = FlatStyle.Flat, BackColor = primary ? AccentColor : InputColor, ForeColor = primary ? BackgroundColor : TextColor,
        Font = new Font("Segoe UI", 9, primary ? FontStyle.Bold : FontStyle.Regular), Margin = new Padding(0, 0, 8, 0), Padding = new Padding(9, 3, 9, 3),
        AccessibleName = text, Cursor = Cursors.Hand, UseVisualStyleBackColor = false
    };
    private static Panel Card() => new() { Dock = DockStyle.Fill, BackColor = CardColor, Margin = Padding.Empty };
}
