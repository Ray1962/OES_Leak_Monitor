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
/// <para>All callbacks arrive on the acquisition thread (intensity logger then leak engine,
/// in order), so the single <see cref="StreamWriter"/> is never touched concurrently.</para>
/// </summary>
public sealed class RatioCsvLogger : IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    private readonly DualIntensityLogger _intensityLogger;
    private readonly LeakMonitorEngine _engine;
    private readonly SystemLogger? _systemLogger;
    private readonly string[] _ratioKeys;

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
        _ratioKeys = engine.MonitoredRatios.Select(r => r.Key).ToArray();

        _intensityLogger.FilesChanged += OnIntensityFilesChanged;
        _engine.SampleProcessed += OnSampleProcessed;
    }

    /// <summary>Opens / closes the ratio CSV in step with the intensity logger's save state.</summary>
    private void OnIntensityFilesChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        bool saving = _intensityLogger.State is LoggerState.Saving or LoggerState.WaitingToStop;
        if (saving && _writer is null)
        {
            var files = _intensityLogger.CurrentFiles;
            OpenSession(files.Count > 0 ? files[0] : "");
        }
        else if (!saving && _writer is not null)
        {
            CloseSession();
        }
    }

    /// <summary>Appends one ratio row per frame while a session is open.</summary>
    private void OnSampleProcessed(object? sender, LeakMonitorSnapshot snap)
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

    private void OpenSession(string intensityFilePath)
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
            _writer = new StreamWriter(_currentPath, append: false, Utf8Bom) { AutoFlush = true };
            _writeErrorLogged = false;

            var header = new StringBuilder("Timestamp");
            foreach (var key in _ratioKeys)
                header.Append(',').Append(key).Append(',').Append(key).Append("_pctBaseline");
            header.Append(",OverallState");
            _writer.WriteLine(header.ToString());

            _systemLogger?.LogSystemEvent(LogSeverity.Information, "RatioCsvOpened",
                "Ratio-trend CSV opened", value: _currentPath);
        }
        catch (Exception ex)
        {
            _writer = null;
            _systemLogger?.LogError("RatioCsv_Open_Failed", ex, intensityFilePath);
        }
    }

    private void CloseSession()
    {
        if (_writer is null) return;
        try { _writer.Flush(); _writer.Dispose(); }
        catch { /* swallowed: shutdown */ }
        _writer = null;
        _systemLogger?.LogSystemEvent(LogSeverity.Information, "RatioCsvClosed",
            "Ratio-trend CSV closed", value: _currentPath);
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
        _engine.SampleProcessed -= OnSampleProcessed;
        CloseSession();
    }
}
