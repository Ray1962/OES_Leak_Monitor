using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace OES_Leak_Monitor;

/// <summary>
/// Logs the leak-monitor ratio trend to its own CSV, independent of the threshold-triggered
/// intensity logger. One row per spectrum frame for as long as the OES is acquiring.
///
/// <para><b>Why it is independent.</b> It used to run in lockstep with the intensity logger's
/// save sessions, which tied the leak record to the raw-spectrum recorder. Those two are not
/// the same kind of data: a full-spectrum intensity row is ~22 KB (~400 MB/hour at 5 Hz) and
/// is recorded on demand, while a ratio row is ~200 B (~4 MB/hour) and is the leak monitor's
/// entire reason to exist. Coupling them meant that disarming the recorder — or simply
/// running below its trigger threshold — silently threw away the leak history, so Ratio
/// Review had nothing to replay. The ratio CSV is cheap enough to always write.</para>
///
/// <para><b>Session lifecycle.</b> A file opens lazily on the first frame after the logger is
/// idle and closes on <see cref="Stop"/> — called when acquisition stops and on Reset Run.
/// A session that crosses local midnight rolls into a new file so each day's data stays in
/// that day's <c>YYYYMM\DD</c> folder; this also bounds a continuously-acquiring file to one
/// day's rows. Open / close / skip are written to the system log
/// (<c>RatioCsvOpened</c> / <c>RatioCsvClosed</c> / <c>RatioCsvSkipped</c>).</para>
///
/// <para><b>File naming.</b> <c>{prefix}_Ratio_{MMddHHmmss}[_N].csv</c> under
/// <c>{baseDir}\YYYYMM\DD</c> — the scheme <see cref="Recording.TryParse"/> understands and
/// the Ratio Review tab filters on by its <c>Ratio</c> tag. The <c>_N</c> suffix only appears
/// if a same-second file already exists (two sessions inside one second, e.g. a quick double
/// Reset Run), so a new session can never truncate an existing file. Note the ratio file no
/// longer shares a session timestamp with an intensity CSV — the two recorders now start and
/// stop independently.</para>
///
/// <para>Sample callbacks arrive on the acquisition thread; <see cref="Stop"/> and
/// <see cref="Configure"/> arrive on the UI thread. A lock guards the writer so they never
/// collide.</para>
/// </summary>
public sealed class RatioCsvLogger : IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    private readonly LeakMonitorEngine _engine;
    private readonly string _defaultDataDirectory;
    private readonly SystemLogger? _systemLogger;

    // Guards the writer and its session state against the acquisition thread (row writes)
    // racing a UI-thread Stop/Configure.
    private readonly object _sync = new();

    // Output location, mirrored from the intensity logger's settings so both recorders write
    // into the same folder tree. Refreshed by Configure (startup + every Apply).
    private string _baseDirectory = "";
    private string _filePrefix = "P";

    // Re-derived at the start of every session so a Ratio Setup edit applied between
    // sessions is reflected in the next file's columns.
    private string[] _ratioKeys = Array.Empty<string>();
    // Aligned with _ratioKeys: whether that ratio's percent column is a σ-score (see the header).
    private bool[] _pedestal = Array.Empty<bool>();

    private StreamWriter? _writer;
    private string _currentPath = "";
    private DateTime _sessionDate;          // local date of the open file, for the midnight roll
    private bool _disposed;
    private bool _disabledLogged;
    private bool _openFailed;               // stop retrying a broken path until Stop/Configure
    private bool _writeErrorLogged;

    public RatioCsvLogger(LeakMonitorEngine engine, string defaultDataDirectory,
        SystemLogger? systemLogger = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        if (string.IsNullOrWhiteSpace(defaultDataDirectory))
            throw new ArgumentException("Default data directory is required.", nameof(defaultDataDirectory));
        _defaultDataDirectory = defaultDataDirectory;
        _systemLogger = systemLogger;

        _engine.SampleProcessed += OnSampleProcessed;
    }

    /// <summary>Path of the open ratio CSV, or an empty string when no session is open.</summary>
    public string CurrentFile { get { lock (_sync) return _writer is null ? "" : _currentPath; } }

    /// <summary>
    /// Point the logger at the intensity logger's output folder and file prefix, so both
    /// recorders land in the same tree. Takes effect on the next session — an open file keeps
    /// writing where it is rather than being cut in half by an unrelated Apply.
    /// </summary>
    public void Configure(LoggerSettings settings)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        lock (_sync)
        {
            _baseDirectory = LoggerSettings.ResolveBaseDirectory(settings.BaseDirectory, _defaultDataDirectory);
            _filePrefix    = string.IsNullOrWhiteSpace(settings.FilePrefix) ? "P" : settings.FilePrefix.Trim();
            // A new destination (or a settings change generally) deserves a fresh attempt and
            // a fresh "why nothing is being written" log line.
            _openFailed    = false;
            _disabledLogged = false;
        }
    }

    /// <summary>
    /// End the current session; the next frame opens a new file. Called when acquisition
    /// stops and from Reset Run, and safe to call when nothing is open.
    /// </summary>
    public void Stop()
    {
        lock (_sync)
        {
            CloseSessionLocked();
            _openFailed = false;
        }
    }

    /// <summary>Appends one ratio row per frame, opening or rolling the file as needed.</summary>
    private void OnSampleProcessed(object? sender, LeakMonitorSnapshot snap)
    {
        if (_disposed) return;
        lock (_sync)
        {
            if (_writer is not null && snap.Timestamp.Date != _sessionDate)
            {
                // Crossed local midnight: close so the new day's rows land in the new day's
                // folder (and yesterday's file is complete).
                CloseSessionLocked();
            }
            if (_writer is null) OpenSessionLocked(snap.Timestamp);

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

    /// <summary>Opens a new ratio CSV for <paramref name="sessionStart"/>. Caller holds <see cref="_sync"/>.</summary>
    private void OpenSessionLocked(DateTime sessionStart)
    {
        if (_openFailed) return;
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
        try
        {
            var baseDir = string.IsNullOrWhiteSpace(_baseDirectory) ? _defaultDataDirectory : _baseDirectory;
            var folder = Path.Combine(baseDir, sessionStart.ToString("yyyyMM", Inv),
                                               sessionStart.ToString("dd", Inv));
            Directory.CreateDirectory(folder);

            _currentPath = UniquePath(folder,
                $"{_filePrefix}_Ratio_{sessionStart.ToString("MMddHHmmss", Inv)}");
            var monitored = _engine.MonitoredRatios;
            _ratioKeys = monitored.Select(r => r.Key).ToArray();
            _pedestal = monitored.Select(r => r.ValueHasPedestal).ToArray();
            _writer = new StreamWriter(_currentPath, append: false, Utf8Bom) { AutoFlush = true };
            _sessionDate = sessionStart.Date;
            _writeErrorLogged = false;
            _disabledLogged = false;

            // The second column per ratio is named for what it actually holds: a percentage of
            // the baseline, or — where the value carries a continuum pedestal — a σ-score on the
            // same 100/120/150 scale. Both are plotted the same way, but they are not the same
            // number, and a file that doesn't say which is a file nobody can re-read in a year.
            var header = new StringBuilder("Timestamp");
            for (int i = 0; i < _ratioKeys.Length; i++)
                header.Append(',').Append(_ratioKeys[i])
                      .Append(',').Append(_ratioKeys[i])
                      .Append(_pedestal[i] ? "_sigmaScore" : "_pctBaseline");
            header.Append(",OverallState,LeakRate,LeakRateSigma");
            _writer.WriteLine(header.ToString());

            _systemLogger?.LogSystemEvent(LogSeverity.Information, "RatioCsvOpened",
                "Ratio-trend CSV opened (new acquisition session)",
                related: $"Ratios={_ratioKeys.Length}",
                value: _currentPath);
        }
        catch (Exception ex)
        {
            _writer = null;
            // Don't retry once per frame on a path that just failed — one log line, then wait
            // for a Stop or a settings change.
            _openFailed = true;
            _systemLogger?.LogError("RatioCsv_Open_Failed", ex, _currentPath);
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
            "Ratio-trend CSV closed", value: _currentPath);
    }

    /// <summary>
    /// <c>{stem}.csv</c>, or <c>{stem}_1.csv</c>, <c>{stem}_2.csv</c>, … if that exists.
    /// Two sessions can share a start second (a quick double Reset Run); without this the
    /// second one would truncate the first, since the writer opens with append:false.
    /// </summary>
    private static string UniquePath(string folder, string stem)
    {
        var path = Path.Combine(folder, stem + ".csv");
        for (int n = 1; File.Exists(path) && n < 1000; n++)
            path = Path.Combine(folder, $"{stem}_{n.ToString(Inv)}.csv");
        return path;
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
        _engine.SampleProcessed -= OnSampleProcessed;
        lock (_sync) CloseSessionLocked();
    }
}
