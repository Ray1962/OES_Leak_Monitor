using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace OES_Leak_Monitor;

/// <summary>
/// Writes the batch record: one row per batch, at the sampling point the cross-batch comparison
/// is built on.
///
/// <para><b>Two files, on purpose.</b> The day file lives beside the recordings it summarises,
/// in the same <c>YYYYMM\DD</c> folder as everything else, so a day folder copied off the machine
/// still explains itself — the argument <c>ConfigSnapshot</c> and <c>SystemLogMirror</c> already
/// make. It is <em>wide</em>: one column per ratio, which is what a person opening it wants. The
/// index at the root of the data folder is <em>long</em>: one row per batch per ratio. Reading a
/// trend means reading dozens of batches across as many day folders, and a wide file cannot
/// survive that — its column set changes the moment a ratio is added, renamed or re-scoped, and a
/// year of history would end up as a pile of files with incompatible headers. The long form has
/// one schema for ever, and it is rebuildable from the day files, so it is a convenience rather
/// than a second source of truth.</para>
///
/// <para><b>The value is a median over a fixed window of one step, not an average of a batch.</b>
/// See <see cref="BatchTracker"/> for why: the viewport fouls within every batch, so two steps of
/// the same process taken at different points in it are not comparable, and averaging them
/// measures the coating.</para>
///
/// <para>The file is named with the leading <c>{prefix}_Batch_</c> so it sorts beside the ratio
/// CSV; the index is named with a leading underscore so <c>Recording.TryParse</c> rejects it and
/// the review tabs walk past, the same convention <c>_config_*.json</c> and <c>_log_*.csv</c>
/// use.</para>
/// </summary>
public sealed class BatchCsvLogger : IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>Name of the cumulative index at the root of the data folder.</summary>
    public const string IndexFileName = "_batches.csv";

    private readonly LeakMonitorEngine _engine;
    private readonly string _defaultDataDirectory;
    private readonly SystemLogger? _systemLogger;
    private readonly object _sync = new();

    private string _baseDirectory = "";
    private string _filePrefix = "P";

    private StreamWriter? _writer;
    private string _currentPath = "";
    private DateTime _sessionDate;
    private string[] _ratioKeys = Array.Empty<string>();
    private string[] _ratioLabels = Array.Empty<string>();
    private string[] _ratioClasses = Array.Empty<string>();
    private bool _openFailed;
    private bool _writeErrorLogged;
    private bool _indexErrorLogged;
    private bool _disposed;

    public BatchCsvLogger(LeakMonitorEngine engine, string defaultDataDirectory,
                          BatchTracker tracker, SystemLogger? systemLogger = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _defaultDataDirectory = defaultDataDirectory ?? "";
        _systemLogger = systemLogger;
        if (tracker is null) throw new ArgumentNullException(nameof(tracker));
        tracker.BatchCompleted += OnBatchCompleted;
        _tracker = tracker;
    }

    private readonly BatchTracker _tracker;

    /// <summary>Path of the day file in progress, or "" when none is open.</summary>
    public string CurrentFile { get { lock (_sync) return _writer is null ? "" : _currentPath; } }

    /// <summary>
    /// Mirrors the intensity logger's base directory and file prefix, so every recorder shares
    /// one folder tree. Call at start-up and on every Apply.
    /// </summary>
    public void Configure(LoggerSettings settings)
    {
        if (settings is null) return;
        lock (_sync)
        {
            _baseDirectory = settings.BaseDirectory ?? "";
            _filePrefix = string.IsNullOrWhiteSpace(settings.FilePrefix) ? "P" : settings.FilePrefix.Trim();
            _openFailed = false;
        }
    }

    /// <summary>Closes the day file; the next batch opens a new one. Safe when nothing is open.</summary>
    public void Stop()
    {
        lock (_sync)
        {
            CloseLocked();
            _openFailed = false;
        }
    }

    private void OnBatchCompleted(object? sender, BatchSummary batch)
    {
        if (_disposed || batch is null || batch.Steps.Count == 0) return;
        lock (_sync)
        {
            if (_writer is not null && batch.Start.Date != _sessionDate) CloseLocked();
            if (_writer is null) OpenLocked(batch.Start);

            var writer = _writer;
            if (writer is null) return;

            // The sampling point per ratio: the first complete step of that ratio's class. A
            // ratio with no class takes the batch's first complete step of any class — it made
            // no claim about which process it needs.
            var values = new double?[_ratioKeys.Length];
            for (int i = 0; i < _ratioKeys.Length; i++)
            {
                var step = string.IsNullOrWhiteSpace(_ratioClasses[i])
                    ? batch.Steps.FirstOrDefault(s => s.Complete)
                    : batch.FirstStepOf(_ratioClasses[i]);
                values[i] = step is not null && step.Medians.TryGetValue(_ratioKeys[i], out var v)
                    ? v
                    : null;
            }

            try
            {
                var row = new StringBuilder();
                row.Append(batch.Start.ToString("yyyy-MM-dd HH:mm:ss", Inv));
                row.Append(',').Append(batch.End.ToString("yyyy-MM-dd HH:mm:ss", Inv));
                row.Append(',').Append(((batch.End - batch.Start).TotalMinutes).ToString("0.0", Inv));
                row.Append(',').Append(batch.Steps.Count.ToString(Inv));
                row.Append(',').Append(ClassSummary(batch));
                foreach (var v in values) row.Append(',').Append(Num(v));
                writer.WriteLine(row.ToString());
                AppendIndexLocked(batch, values);
            }
            catch (Exception ex)
            {
                if (!_writeErrorLogged)
                {
                    _writeErrorLogged = true;
                    _systemLogger?.LogError("BatchCsv_WriteRow_Failed", ex, _currentPath);
                }
            }
        }
    }

    /// <summary>Compact census of the batch, e.g. <c>B×4 A×8 C×8</c>. Steps whose class was never
    /// decided are counted under <c>?</c> rather than dropped.</summary>
    private static string ClassSummary(BatchSummary batch) =>
        string.Join(" ", batch.Steps
            .GroupBy(s => string.IsNullOrEmpty(s.Class) ? "?" : s.Class, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}x{g.Count()}"));

    private void OpenLocked(DateTime start)
    {
        if (_openFailed) return;
        if (!_engine.Settings.Enabled || !(_engine.Settings.Batch?.Enabled ?? false)) return;
        try
        {
            var baseDir = string.IsNullOrWhiteSpace(_baseDirectory) ? _defaultDataDirectory : _baseDirectory;
            var folder = Path.Combine(baseDir, start.ToString("yyyyMM", Inv), start.ToString("dd", Inv));
            Directory.CreateDirectory(folder);

            _currentPath = UniquePath(folder, $"{_filePrefix}_Batch_{start.ToString("MMddHHmmss", Inv)}");
            var monitored = _engine.MonitoredRatios;
            _ratioKeys = monitored.Select(r => r.Key).ToArray();
            _ratioLabels = monitored.Select(r => r.DisplayName ?? "").ToArray();
            _ratioClasses = monitored.Select(r => r.ProcessClass ?? "").ToArray();

            _writer = new StreamWriter(_currentPath, append: false, Utf8Bom) { AutoFlush = true };
            _sessionDate = start.Date;
            _writeErrorLogged = false;

            var header = new StringBuilder("BatchStart,BatchEnd,DurationMin,Steps,Classes");
            for (int i = 0; i < _ratioKeys.Length; i++)
                header.Append(',').Append(ColumnName(_ratioLabels[i], _ratioKeys[i]));
            _writer.WriteLine(header.ToString());

            _systemLogger?.LogSystemEvent(LogSeverity.Information, "BatchCsvOpened",
                "Batch record opened (one row per batch, at each batch's sampling point)",
                related: $"Ratios={_ratioKeys.Length}", value: _currentPath);
        }
        catch (Exception ex)
        {
            _writer = null;
            _openFailed = true;
            _systemLogger?.LogError("BatchCsv_Open_Failed", ex, _currentPath);
        }
    }

    /// <summary>
    /// Appends the same batch to the long-format index at the root of the data folder. One row
    /// per batch per ratio, so the schema never changes when the ratio set does — see the class
    /// remarks. Best-effort: a failure is logged once and never stops the day file, which is the
    /// record that matters.
    /// </summary>
    private void AppendIndexLocked(BatchSummary batch, double?[] values)
    {
        var baseDir = string.IsNullOrWhiteSpace(_baseDirectory) ? _defaultDataDirectory : _baseDirectory;
        if (string.IsNullOrWhiteSpace(baseDir)) return;
        var path = Path.Combine(baseDir, IndexFileName);
        try
        {
            bool fresh = !File.Exists(path);
            using var w = new StreamWriter(path, append: true, Utf8Bom);
            if (fresh) w.WriteLine("BatchStart,BatchEnd,Steps,Classes,RatioKey,RatioLabel,ProcessClass,Value");
            for (int i = 0; i < _ratioKeys.Length; i++)
            {
                if (values[i] is null) continue;    // a ratio with no sampling point this batch
                w.Write(batch.Start.ToString("yyyy-MM-dd HH:mm:ss", Inv)); w.Write(',');
                w.Write(batch.End.ToString("yyyy-MM-dd HH:mm:ss", Inv)); w.Write(',');
                w.Write(batch.Steps.Count.ToString(Inv)); w.Write(',');
                w.Write(ClassSummary(batch)); w.Write(',');
                w.Write(Safe(_ratioKeys[i])); w.Write(',');
                w.Write(Safe(_ratioLabels[i])); w.Write(',');
                w.Write(Safe(_ratioClasses[i])); w.Write(',');
                w.WriteLine(Num(values[i]));
            }
        }
        catch (Exception ex)
        {
            if (!_indexErrorLogged)
            {
                _indexErrorLogged = true;
                _systemLogger?.LogError("BatchIndex_Append_Failed", ex, path);
            }
        }
    }

    private void CloseLocked()
    {
        if (_writer is null) return;
        try { _writer.Flush(); _writer.Dispose(); }
        catch { /* closing a broken handle is not worth a second failure */ }
        _writer = null;
        _currentPath = "";
    }

    private static string UniquePath(string folder, string stem)
    {
        var path = Path.Combine(folder, stem + ".csv");
        for (int n = 1; File.Exists(path) && n < 1000; n++)
            path = Path.Combine(folder, $"{stem}_{n.ToString(Inv)}.csv");
        return path;
    }

    private static string ColumnName(string label, string key)
    {
        var clean = Safe(label).Replace('[', '(').Replace(']', ')');
        return string.IsNullOrEmpty(clean) ? key : $"{clean} [{key}]";
    }

    private static string Safe(string? v) =>
        (v ?? "").Replace(',', ';').Replace('"', '\'').Replace('\n', ' ').Replace('\r', ' ').Trim();

    private static string Num(double? v) =>
        v is null || double.IsNaN(v.Value) ? "" : v.Value.ToString("G6", Inv);

    public void Dispose()
    {
        _disposed = true;
        _tracker.BatchCompleted -= OnBatchCompleted;
        lock (_sync) CloseLocked();
    }
}
