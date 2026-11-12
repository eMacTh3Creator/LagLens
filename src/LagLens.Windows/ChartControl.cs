using System.Drawing.Drawing2D;
using LagLens.Core;

namespace LagLens.Windows;

internal sealed class ChartControl : Control
{
    private SessionView? _view;
    internal static readonly Color LocalColor = Color.FromArgb(48, 219, 184);
    internal static readonly Color CloudColor = Color.FromArgb(165, 147, 255);
    internal static readonly Color GoogleColor = Color.FromArgb(246, 192, 96);

    public ChartControl()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(20, 30, 45);
        ForeColor = Color.FromArgb(170, 187, 208);
        AccessibleName = "Latency timeline";
        AccessibleDescription = "Last 120 rounds. Teal is router ping, violet is Cloudflare TCP, amber is Google TCP. Red marks indicate failed probes.";
        MinimumSize = new Size(340, 160);
    }

    public void SetSession(SessionView? view) { _view = view; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var plot = new Rectangle(48, 17, Math.Max(20, ClientSize.Width - 66), Math.Max(20, ClientSize.Height - 67));
        var rounds = _view?.Rounds.TakeLast(120).ToArray() ?? [];
        var targets = _view?.Targets.Where(t => t.Kind is ProbeKind.GatewayIcmp or ProbeKind.InternetTcp).ToArray() ?? [];
        var values = rounds.SelectMany(r => r.Probes).Where(p => targets.Any(t => t.Id == p.TargetId) && p.Outcome == ProbeOutcome.Success && p.Milliseconds.HasValue).Select(p => p.Milliseconds!.Value).ToArray();
        var max = values.Length == 0 ? 100 : Math.Max(50, Math.Ceiling(values.Max() / 50) * 50);
        using var axisPen = new Pen(Color.FromArgb(42, 57, 76));
        using var axisText = new SolidBrush(ForeColor);
        using var smallFont = new Font("Segoe UI", 8);
        for (int row = 0; row <= 4; row++)
        {
            var y = plot.Bottom - plot.Height * row / 4;
            g.DrawLine(axisPen, plot.Left, y, plot.Right, y);
            g.DrawString($"{max * row / 4:0}", smallFont, axisText, 3, y - 6);
        }
        g.DrawString("ms", smallFont, axisText, 3, 1);
        g.DrawString("Older", smallFont, axisText, plot.Left, plot.Bottom + 6);
        var now = "Latest";
        g.DrawString(now, smallFont, axisText, plot.Right - g.MeasureString(now, smallFont).Width, plot.Bottom + 6);
        if (rounds.Length == 0)
        {
            using var emptyFont = new Font("Segoe UI", 10);
            var text = "Start a test to see live latency";
            var sz = g.MeasureString(text, emptyFont);
            g.DrawString(text, emptyFont, axisText, plot.Left + (plot.Width - sz.Width) / 2, plot.Top + (plot.Height - sz.Height) / 2);
        }
        for (int series = 0; series < targets.Length; series++)
        {
            var target = targets[series];
            var color = target.Kind == ProbeKind.GatewayIcmp ? LocalColor : target.Address == "1.1.1.1" ? CloudColor : GoogleColor;
            using var line = new Pen(color, 2f);
            using var pointBrush = new SolidBrush(color);
            using var failBrush = new SolidBrush(Color.FromArgb(250, 112, 121));
            PointF? previous = null;
            for (int i = 0; i < rounds.Length; i++)
            {
                var result = rounds[i].Probes.FirstOrDefault(p => p.TargetId == target.Id);
                var x = plot.Left + plot.Width * (i / (float)Math.Max(1, rounds.Length - 1));
                if (result?.Outcome == ProbeOutcome.Success && result.Milliseconds is double ms)
                {
                    var p = new PointF(x, plot.Bottom - (float)(ms / max * plot.Height));
                    if (previous.HasValue) g.DrawLine(line, previous.Value, p);
                    else g.FillEllipse(pointBrush, p.X - 2, p.Y - 2, 4, 4);
                    previous = p;
                }
                else
                {
                    previous = null;
                    if (result?.Outcome is ProbeOutcome.Timeout or ProbeOutcome.Error)
                        g.FillEllipse(failBrush, x - 2, plot.Bottom - 4 - series * 5, 4, 4);
                }
            }
        }
        if (_view is not null && rounds.Length > 1)
        {
            using var markerPen = new Pen(Color.FromArgb(106, 153, 176)) { DashStyle = DashStyle.Dash };
            foreach (var marker in _view.Markers.Where(m => m.Timestamp >= rounds[0].Timestamp && m.Timestamp <= rounds[^1].Timestamp))
            {
                var ratio = (marker.Timestamp - rounds[0].Timestamp).TotalMilliseconds / Math.Max(1, (rounds[^1].Timestamp - rounds[0].Timestamp).TotalMilliseconds);
                var x = plot.Left + (float)ratio * plot.Width;
                g.DrawLine(markerPen, x, plot.Top, x, plot.Bottom);
            }
        }
        var legendY = ClientSize.Height - 20;
        DrawLegend(g, "Router", LocalColor, plot.Left, legendY, smallFont);
        DrawLegend(g, "Cloudflare TCP", CloudColor, plot.Left + 76, legendY, smallFont);
        DrawLegend(g, "Google TCP", GoogleColor, plot.Left + 207, legendY, smallFont);
    }

    private static void DrawLegend(Graphics g, string text, Color color, int x, int y, Font font)
    {
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, x, y + 4, 6, 6);
        g.DrawString(text, font, brush, x + 11, y);
    }
}
