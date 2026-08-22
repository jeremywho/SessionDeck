using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Text;

namespace ClaudeSessionMonitor;

/// <summary>Low-rate telemetry for instrumented builds. No session names or ids are recorded.</summary>
internal static class IsolationTelemetry
{
    static readonly object Gate = new();
    static readonly Dictionary<string, int> Properties = new(StringComparer.Ordinal);
    static StreamWriter? _writer;
    static int _viewAdds;
    static int _viewRemoves;
    static int _viewMoves;
    static int _viewResets;
    static int _rowsLoaded;
    static int _rowsUnloaded;

    internal static void Start()
    {
        if (!ExperimentOptions.TelemetryActive) return;
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ExperimentOptions.LogPath)!);
            _writer = new StreamWriter(ExperimentOptions.LogPath, false, new UTF8Encoding(false)) { AutoFlush = true };
            _writer.WriteLine("timestamp\tkind\tmode\tdetail");
            WriteLocked("start", $"pid={Environment.ProcessId}");
        }
    }

    internal static void PropertyChanged(string name)
    {
        if (_writer == null) return;
        lock (Gate) Properties[name] = Properties.GetValueOrDefault(name) + 1;
    }

    internal static void CollectionChanged(NotifyCollectionChangedAction action)
    {
        if (_writer == null) return;
        lock (Gate)
        {
            switch (action)
            {
                case NotifyCollectionChangedAction.Add: _viewAdds++; break;
                case NotifyCollectionChangedAction.Remove: _viewRemoves++; break;
                case NotifyCollectionChangedAction.Move: _viewMoves++; break;
                case NotifyCollectionChangedAction.Reset: _viewResets++; break;
            }
        }
    }

    internal static void RowLoaded() { if (_writer != null) lock (Gate) _rowsLoaded++; }
    internal static void RowUnloaded() { if (_writer != null) lock (Gate) _rowsUnloaded++; }

    internal static void FlushRefresh(int sessions, int claudeDiscovered, int codexDiscovered,
                                      int claudeRows, int codexRows, double applyMs)
    {
        if (_writer == null) return;
        lock (Gate)
        {
            string changed = Properties.Count == 0
                ? "none"
                : string.Join(",", Properties.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));
            WriteLocked("refresh",
                $"sessions={sessions};claudeRaw={claudeDiscovered};codexRaw={codexDiscovered};" +
                $"claudeRows={claudeRows};codexRows={codexRows};" +
                $"applyMs={applyMs.ToString("F3", CultureInfo.InvariantCulture)};" +
                $"properties={changed};viewAdd={_viewAdds};viewRemove={_viewRemoves};viewMove={_viewMoves};" +
                $"viewReset={_viewResets};rowLoad={_rowsLoaded};rowUnload={_rowsUnloaded}");
            Properties.Clear();
            _viewAdds = _viewRemoves = _viewMoves = _viewResets = 0;
            _rowsLoaded = _rowsUnloaded = 0;
        }
    }

    internal static void UiaSweep(long elapsedMs, int sessions, int resolved)
    {
        if (_writer == null) return;
        lock (Gate) WriteLocked("terminal-uia", $"elapsedMs={elapsedMs};sessions={sessions};resolved={resolved}");
    }

    internal static void CodexProbe(long elapsedMs, CodexProbeStats stats)
    {
        if (_writer == null) return;
        lock (Gate) WriteLocked("codex-probe",
            $"elapsedMs={elapsedMs};files={stats.Files};candidateChecks={stats.CandidateChecks};" +
            $"verifyChecks={stats.VerificationChecks};owned={stats.Owned};unowned={stats.Unowned};" +
            $"indeterminate={stats.Indeterminate};forgotten={stats.Forgotten};" +
            $"knownBefore={stats.KnownBefore};knownAfter={stats.KnownAfter};errors={stats.ErrorSummary}");
    }

    static void WriteLocked(string kind, string detail) =>
        _writer?.WriteLine($"{DateTimeOffset.Now:O}\t{kind}\t{ExperimentOptions.Mode}\t{detail}");
}
