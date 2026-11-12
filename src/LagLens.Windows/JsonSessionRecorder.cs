using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LagLens.Core;

namespace LagLens.Windows;

public sealed class JsonSessionRecorder : ISessionRecorder
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false
    };
    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private bool _disposed;
    public string StoragePath { get; }

    public JsonSessionRecorder(string? parentDirectory = null)
    {
        var parent = parentDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LagLens", "Sessions");
        StoragePath = Path.Combine(parent, DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(StoragePath);
        _writer = new StreamWriter(new FileStream(Path.Combine(StoragePath, "session.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
    }

    private void Write(string type, object data)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _writer.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, type, data }, JsonOptions));
        }
    }
    public void WriteHeader(SessionView view) => Write("header", view);
    public void WriteRound(SampleRound round) => Write("sample", round);
    public void WriteMarker(SessionMarker marker) => Write("marker", marker);
    public void WriteFinal(SessionView view)
    {
        Write("final", new { view.EndedAt, view.Assessment, view.Metrics });
        ReportExporter.Export(view, StoragePath);
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Dispose();
        }
    }
}
