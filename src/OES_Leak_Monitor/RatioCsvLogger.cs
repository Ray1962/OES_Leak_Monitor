using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace OES_Leak_Monitor;

/// <summary>
/// Logs the leak-monitor ratio trend to a CSV that runs in lockstep with the intensity
/// logger's save sessions. While the threshold logger is saving — i.e. the plasma
/// intensity at its trigger wavelength is above the threshold — one ratio row is written
/// per spectrum frame. The file is a sibling of the intensity CSV: same
/// <c>{baseDir}\YYYYMM\DD</c> folder, the <c>OES1</c> tag swapped for <c>Ratio</c>, so
/// the two files share a recording-group key and the Recordings tab ignores the ratio one.
///
/// <para>Session lifecycle is bound to the intensity logger's two events:
/// <list type="bullet">
/// <item><b>Open</b> follows <see cref="DualIntensityLogger.FilesChanged"/> — it is raised
/// only after the intensity writer has actually opened its file, so the ratio file name
/// can be derived from a valid path.</item>
/// <item><b>Close</b> follows <see cref="DualIntensityLogger.StateChanged"/> reaching
/// <see cref="LoggerState.Idle"/>. The intensity logger raises <c>FilesChanged</c> while
/// still in <c>Saving</c>/<c>WaitingToStop</c> (it closes its writers, then transitions),
/// so the only reliable end-of-session signal is the Idle transition — covering both a
/// natural below-threshold stop and a forced stop (OES acquisition stopped / logger
/// disabled). This guarantees each Start→threshold cycle gets its own ratio file.</item>
/// </list></para>
///
/// <para>Open and close are also written to the system log (<c>RatioCsvOpened</c> /
/// <c>RatioCsvClosed</c>).</para>
///
/// <para>Sample callbacks arrive on the acquisition thread; a forced close can arrive on
/// the UI thread (MainViewModel stopping the logger). A lock guards the writer so the two
/// never collide.</para>
/// </summary>
public sealed class RatioCsvLogger : IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    private readonly DualIntensityLogger _intensityLogger;
    private readonly LeakMonitorEngine _engine;
    private readonly SystemLogger? _systemLogger;

    // Guards _writer / _currentPath / _ratioKeys against the acquisition thread (row
    // writes) racing a UI-thread forced close.
    private readonly object _sync = new();

    // Re-derived at the start of every session so a Ratio Setup edit applied between
    // sessions is reflected in the next file's columns.
    private string[] _ratioKeys = Array.Empty<string>();

    private StreamWriter? _writer;
    private string _currentPath = "";
    private bool _disposed;
    private bool _disabledLogged;
    private bool _writeErrorLogged;

    public RatioCsvLogger(DualIntensityLogger intensityLogger, LeakMonitorEngine engine,
        SystemLogger? systemLogger = null)
    {
        _intensityLogger = intensityLogger ?? throw new ArgumentNullException(nameof(intensityLogger));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _systemLogger = systemLogger;

        _intensityLogger.FilesChanged += OnIntensityFilesChanged;
        _intensityLogger.StateChanged += OnIntensityStateChanged;
        _engine.SampleProcessed += OnSampleProcessed;
    }

    /// <summary>
    /// Opens the ratio CSV once the intensity writer has opened its file. Raised on every
    /// writer open/close, so a duplicate event with a session already open is ignored.
    /// </summary>
    private void OnIntensityFilesChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        lock (_sync)
        {
            if (_writer is not null) return;   // session already open (or this is a close)
            // Only open while the intensity logger is actively saving.
            if (_intensityLogger.State is not (LoggerState.Saving or LoggerState.WaitingToStop))
                return;
            var files = _intensityLogger.CurrentFiles;
            OpenSessionLocked(files.Count > 0 ? files[0] : "");
        }
    }

    /// <summary>
    /// Closes the ratio CSV when the intensity logger's session truly ends. The logger
    /// reaches <see cref="LoggerState.Idle"/> only after it has closed its own writers,
    /// so this is the synchronized end-of-session signal — for a below-threshold stop and
    /// for a forced stop alike.
    /// </summary>
    private void OnIntensityStateChanged(object? sender, LoggerStateChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.NewState != LoggerState.Idle) return;
        lock (_sync)
        {
            if (_writer is not null) CloseSessionLocked();
        }
    }

    /// <summary>Appends one ratio row per frame while a session is open.</summary>
    private void OnSampleProcessed(object? sender, LeakMonitorSnapshot snap)
    {
        lock (_sync)
        {
            var writer = _writer;
            if (writer is null) return;
            try
            {
                var row = new StringBuilder(snap.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", Inv));
                foreach (var key in _ratioKeys)
                {
                    var rs = FindRatio(snap, key);
                    row.Append(',').Append(Num(rs?.RawRatio));
                    row.Append(',').Append(Num(rs?.PercentOfBaseline));
                }
                row.Append(',').Append(snap.Overall);
                // Quantitative leak rate (blank when no calibration / no usable estimate).
                bool hasRate = snap.LeakRate is { HasEstimate: true };
                row.Append(',').Append(Num(hasRate ? snap.LeakRate!.LeakRate : (double?)null));
                row.Append(',').Append(Num(hasRate ? snap.LeakRate!.Sigma : (double?)null));
                writer.WriteLine(row.ToString());
            }
            catch (Exception ex)
            {
                // Log only the first failure of a session — a persistent fault (disk full,
                // file locked) would otherwise write one log row per spectrum frame.
                if (!_writeErrorLogged)
                {
                    _writeErrorLogged = true;
                    _systemLogger?.LogError("RatioCsv_WriteRow_Failed", ex, _currentPath);
                }
            }
        }
    }

    /// <summary>Opens a new ratio CSV. Caller holds <see cref="_sync"/>.</summary>
    private void OpenSessionLocked(string intensityFilePath)
    {
        if (!_engine.Settings.Enabled || !_engine.Settings.RatioCsvEnabled)
        {
            // Log once: an operator expecting ratio CSVs and finding none can see why.
            if (!_disabledLogged)
            {
                _disabledLogged = true;
                _systemLogger?.LogSystemEvent(LogSeverity.Information, "RatioCsvSkipped",
                    "Ratio-trend CSV not written — leak monitoring or ratio CSV logging is " +
                    "disabled in settings.",
                    value: $"Enabled={_engine.Settings.Enabled}," +
                           $"RatioCsvEnabled={_engine.Settings.RatioCsvEnabled}");
            }
            return;
        }
        if (string.IsNullOrWhiteSpace(intensityFilePath))
        {
            _systemLogger?.LogSystemEvent(LogSeverity.Warning, "RatioCsvSkipped",
                "Ratio-trend CSV not written — the intensity logger opened a save session " +
                "but reported no file path to derive the ratio file name from.");
            return;
        }
        try
        {
            _currentPath = DeriveRatioPath(intensityFilePath);
            _ratioKeys = _engine.MonitoredRatios.Select(r => r.Key).ToArray();
            _writer = new StreamWriter(_currentPath, append: false, Utf8Bom) { AutoFlush = true };
            _writeErrorLogged = false;

            var header = new StringBuilder("Timestamp");
            foreach (var key in _ratioKeys)
                header.Append(',').Append(key).Append(',').Append(key).Append("_pctBaseline");
            header.Append(",OverallState,LeakRate,LeakRateSigma");
            _writer.WriteLine(header.ToString());

            _systemLogger?.LogSystemEvent(LogSeverity.Information, "RatioCsvOpened",
                "Ratio-trend CSV opened (new save session)",
                related: $"IntensityFile={Path.GetFileName(intensityFilePath)}",
                value: _currentPath);
        }
        catch (Exception ex)
        {
            _writer = null;
            _systemLogger?.LogError("RatioCsv_Open_Failed", ex, intensityFilePath);
        }
    }

    /// <summary>Closes the open ratio CSV. Caller holds <see cref="_sync"/>.</summary>
    private void CloseSessionLocked()
    {
        if (_writer is null) return;
        try { _writer.Flush(); _writer.Dispose(); }
        catch { /* swallowed: shutdown */ }
        _writer = null;
        _systemLogger?.LogSystemEvent(LogSeverity.Information, "RatioCsvClosed",
            "Ratio-trend CSV closed (save session ended)", value: _currentPath);
    }

    /// <summary>Sibling of the intensity file: same folder, the "OES1" tag swapped for "Ratio".</summary>
    private static string DeriveRatioPath(string intensityFilePath)
    {
        var dir = Path.GetDirectoryName(intensityFilePath) ?? "";
        var name = Path.GetFileName(intensityFilePath);
        var ratioName = name.Contains("_OES1_", StringComparison.Ordinal)
            ? name.Replace("_OES1_", "_Ratio_", StringComparison.Ordinal)
            : Path.GetFileNameWithoutExtension(name) + "_Ratio.csv";
        return Path.Combine(dir, ratioName);
    }

    private static RatioSnapshot? FindRatio(LeakMonitorSnapshot snap, string key)
    {
        foreach (var r in snap.Ratios)
            if (r.Key == key) return r;
        return null;
    }

    private static string Num(double? v) =>
        v is null || double.IsNaN(v.Value) ? "" : v.Value.ToString("G6", Inv);

    public void Dispose()
    {
        _disposed = true;
        _intensityLogger.FilesChanged -= OnIntensityFilesChanged;
        _intensityLogger.StateChanged -= OnIntensityStateChanged;
        _engine.SampleProcessed -= OnSampleProcessed;
        lock (_sync) CloseSessionLocked();
    }
}
