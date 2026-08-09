using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace OES_Leak_Monitor;

/// <summary>
/// View-model for the Replay tab: pick a recorded full-spectrum CSV and play it back through
/// the whole live pipeline (leak monitor, trends, recorders) as if the plasma were running,
/// so the detection algorithms can be validated against real data with no spectrometer
/// attached. Transport controls, a progress readout, and a preview of the frame currently
/// being delivered — on the recording's own wavelength axis, which is the point of the
/// exercise (see <see cref="SpectrumReplaySource"/>).
///
/// <para>The view-model owns no state of its own beyond the display strings: it polls
/// <see cref="SpectrumReplaySource.Snapshot"/> on a timer rather than subscribing per frame,
/// since frames arrive on the acquisition thread at up to the fast-forward rate and binding
/// to them would be nothing but dispatcher churn.</para>
/// </summary>
public sealed class ReplayViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>Fast-forward factors offered in the UI. 20× is the ceiling because the
    /// recorders write at the same multiple — a full-spectrum row is ~22 KB, so 20× of a 5 Hz
    /// recording is ~2 MB/s of sustained CSV writing, at which point the disk, not the
    /// algorithm, decides the rate.</summary>
    public static readonly double[] SpeedOptions = { 1, 5, 20 };

    private readonly SpectrumReplaySource _replay;
    private readonly LeakMonitorSettings _leakSettings;
    private readonly Action _persistLeakSettings;
    private readonly Func<string> _initialDirectory;
    private readonly SystemLogger? _systemLogger;
    private readonly DispatcherTimer _timer;

    private LineSeries _previewSeries = null!;
    private bool _isEngineer;

    /// <summary>Raised just before playback starts (UI thread), so the host can switch the
    /// recorders over to replay output before the first frame lands.</summary>
    public event EventHandler? ReplayStarting;

    /// <summary>Raised after the operator stops playback (UI thread). A run that reaches the
    /// end of the file does not raise this — the host hears that from
    /// <see cref="SpectrumReplaySource.PlaybackFinished"/> instead.</summary>
    public event EventHandler? ReplayStopped;

    /// <summary>Raised when the loaded file changes (or is cleared), so the host can persist
    /// the selection. The argument is the new path, or null when cleared.</summary>
    public event EventHandler<string?>? FileChanged;

    public ReplayViewModel(SpectrumReplaySource replay, LeakMonitorSettings leakSettings,
        Action persistLeakSettings, Func<string> initialDirectory, SystemLogger? systemLogger = null)
    {
        _replay = replay ?? throw new ArgumentNullException(nameof(replay));
        _leakSettings = leakSettings ?? throw new ArgumentNullException(nameof(leakSettings));
        _persistLeakSettings = persistLeakSettings ?? throw new ArgumentNullException(nameof(persistLeakSettings));
        _initialDirectory = initialDirectory ?? throw new ArgumentNullException(nameof(initialDirectory));
        _systemLogger = systemLogger;

        BuildPlot();

        ChooseFileCommand = new RelayCommand(ChooseFile, () => _isEngineer && !IsTransportBusy);
        ClearFileCommand  = new RelayCommand(ClearFile,  () => _isEngineer && _replay.IsLoaded && !IsTransportBusy);
        PlayCommand       = new RelayCommand(Play,       () => _isEngineer && _replay.IsLoaded && _state != ReplayState.Playing);
        PauseCommand      = new RelayCommand(Pause,      () => _isEngineer && _state == ReplayState.Playing);
        RestartCommand    = new RelayCommand(Restart,    () => _isEngineer && _replay.IsLoaded);
        StopCommand       = new RelayCommand(Stop,       () => _isEngineer && IsTransportBusy);

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Refresh();
    }

    public RelayCommand ChooseFileCommand { get; }
    public RelayCommand ClearFileCommand { get; }
    public RelayCommand PlayCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand RestartCommand { get; }
    public RelayCommand StopCommand { get; }

    public PlotModel PlotModel { get; private set; } = null!;

    /// <summary>True while the replay owns the spectrum stream — playing, paused, or holding
    /// on the last frame after finishing. Loading a different file mid-run is refused rather
    /// than swapping the data out from under the running algorithms.</summary>
    private bool IsTransportBusy =>
        _state is ReplayState.Playing or ReplayState.Paused or ReplayState.Finished;

    private ReplayState _state = ReplayState.NoFile;

    public void SetRole(bool isEngineerOrHigher)
    {
        _isEngineer = isEngineerOrHigher;
        RaiseCanExec();
    }

    // --- transport -----------------------------------------------------------------

    private void ChooseFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select a recorded full-spectrum CSV to replay",
            Filter = "Spectrum CSV (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        var dir = _initialDirectory();
        if (Directory.Exists(dir)) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog() != true) return;

        try
        {
            _replay.Load(dlg.FileName);
            FileChanged?.Invoke(this, dlg.FileName);
            _systemLogger?.LogSystemEvent(LogSeverity.Information, "ReplayFileSelected",
                "Replay source recording loaded",
                value: $"Path={dlg.FileName},Frames={_replay.FrameCount}");
            StatusText = "Loaded. Connect and Start the OES (test mode), then press Play.";
        }
        catch (Exception ex)
        {
            StatusText = "Could not load the recording: " + ex.Message;
            _systemLogger?.LogError("Replay_Load_Failed", ex, dlg.FileName);
        }
        Refresh();
    }

    private void ClearFile()
    {
        _replay.Clear();
        FileChanged?.Invoke(this, null);
        StatusText = "Replay cleared — test mode is back on the built-in synthetic spectra.";
        _systemLogger?.LogSystemEvent(LogSeverity.Information, "ReplayFileCleared",
            "Replay source recording cleared");
        Refresh();
    }

    private void Play()
    {
        bool resuming = _state == ReplayState.Paused;
        if (!resuming) ReplayStarting?.Invoke(this, EventArgs.Empty);
        _replay.Play();
        StatusText = resuming
            ? "Playing."
            : $"Playing at {FormatSpeed(_replay.Speed)} — the leak monitor is seeing the recording's own frame intervals.";
        Refresh();
    }

    private void Pause()
    {
        _replay.Pause();
        StatusText = "Paused — no frames are being delivered.";
        Refresh();
    }

    private void Restart()
    {
        // A restart is a fresh run: the host rolls the recorders so the previous run's rows
        // don't continue into this one's file.
        ReplayStarting?.Invoke(this, EventArgs.Empty);
        _replay.Restart();
        StatusText = "Restarted from the first frame.";
        Refresh();
    }

    private void Stop()
    {
        _replay.Stop();
        ReplayStopped?.Invoke(this, EventArgs.Empty);
        StatusText = "Stopped — test mode is back on the built-in synthetic spectra.";
        Refresh();
    }

    // --- alarm gate ----------------------------------------------------------------

    /// <summary>
    /// Whether the leak monitor raises alarm transitions while the frames are test-mode ones.
    /// Off by default (<see cref="LeakMonitorSettings.SuppressAlarmsInTestMode"/>) because
    /// synthetic spectra would otherwise fill the system log with meaningless alarms — but
    /// replaying a real recording is exactly the case where the alarms are the thing being
    /// checked, so it is switchable here. Persisted immediately, like every other leak-monitor
    /// setting. Note this gates the logged alarm transitions; the Leak Monitor tab shows the
    /// computed state either way.
    /// </summary>
    public bool AlarmsEnabledInTestMode
    {
        get => !_leakSettings.SuppressAlarmsInTestMode;
        set
        {
            if (AlarmsEnabledInTestMode == value) return;
            _leakSettings.SuppressAlarmsInTestMode = !value;
            _persistLeakSettings();
            _systemLogger?.LogSystemEvent(LogSeverity.Information, "ReplayAlarmGateChanged",
                value
                    ? "Leak-monitor alarms enabled for test-mode frames (replay validation)"
                    : "Leak-monitor alarms suppressed for test-mode frames");
            OnPropertyChanged();
            OnPropertyChanged(nameof(AlarmGateNote));
        }
    }

    public string AlarmGateNote => AlarmsEnabledInTestMode
        ? "Alarm transitions are logged and latched, exactly as on real hardware."
        : "Alarm transitions are not logged while the frames are test-mode ones.";

    // --- speed ---------------------------------------------------------------------

    public double SelectedSpeed
    {
        get => _replay.Speed;
        set
        {
            if (Math.Abs(_replay.Speed - value) < 1e-9) return;
            _replay.Speed = value;
            OnPropertyChanged();
            Refresh();
        }
    }

    // --- polled status -------------------------------------------------------------

    private string _statusText = "No recording loaded.";
    /// <summary>One sentence about the last action taken, or what to do next.</summary>
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private string _stateText = "NO FILE";
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }

    private Brush _stateBrush = Brushes.Gray;
    public Brush StateBrush { get => _stateBrush; private set => Set(ref _stateBrush, value); }

    private string _fileText = "(no recording selected)";
    /// <summary>Selected file, its frame count and length.</summary>
    public string FileText { get => _fileText; private set => Set(ref _fileText, value); }

    private string _axisText = "";
    /// <summary>The recording's own wavelength axis — the reason replay exists, so it is worth
    /// stating: this is the range and resolution the algorithms are being fed.</summary>
    public string AxisText { get => _axisText; private set => Set(ref _axisText, value); }

    private string _positionText = "0:00 / 0:00";
    public string PositionText { get => _positionText; private set => Set(ref _positionText, value); }

    private string _frameText = "";
    public string FrameText { get => _frameText; private set => Set(ref _frameText, value); }

    private double _progressPercent;
    public double ProgressPercent { get => _progressPercent; private set => Set(ref _progressPercent, value); }

    private string _speedWarning = "";
    /// <summary>Non-empty while the achieved rate falls short of the requested one — the disk
    /// or CPU is the limit, and a silent shortfall would read as "20× is fine here".</summary>
    public string SpeedWarning { get => _speedWarning; private set => Set(ref _speedWarning, value); }

    private void Refresh()
    {
        var s = _replay.Snapshot();
        _state = s.State;

        (StateText, StateBrush) = s.State switch
        {
            ReplayState.Playing  => ("PLAYING",  (Brush)Brushes.ForestGreen),
            ReplayState.Paused   => ("PAUSED",   Brushes.DarkOrange),
            ReplayState.Finished => ("FINISHED", Brushes.SteelBlue),
            ReplayState.Ready    => ("READY",    Brushes.SlateGray),
            _                    => ("NO FILE",  Brushes.Gray),
        };

        FileText = s.FilePath is null
            ? "(no recording selected)"
            : $"{Path.GetFileName(s.FilePath)} · {s.FrameCount} frames · {FormatDuration(s.DurationSeconds)}";

        PositionText = $"{FormatDuration(s.PositionSeconds)} / {FormatDuration(s.DurationSeconds)}";
        FrameText = s.FrameCount > 0 ? $"frame {s.FrameIndex} / {s.FrameCount}" : "";
        ProgressPercent = s.DurationSeconds > 0
            ? Math.Clamp(s.PositionSeconds / s.DurationSeconds * 100, 0, 100)
            : 0;

        SpeedWarning = s.State == ReplayState.Playing && s.AchievedSpeed > 0 && s.AchievedSpeed < s.Speed * 0.8
            ? $"keeping up at only {FormatSpeed(s.AchievedSpeed)} — the recorders or the CPU are the limit"
            : "";

        UpdatePreview(s.LastFrame);
        RaiseCanExec();
    }

    private void UpdatePreview(Aqst.OesSpectrometer.Models.SpectrumSample? frame)
    {
        if (frame is null)
        {
            if (_previewSeries.Points.Count == 0) return;
            _previewSeries.Points.Clear();
            AxisText = "";
            PlotModel.InvalidatePlot(true);
            return;
        }

        var wl = frame.Wavelengths;
        var inten = frame.Intensities;
        int n = Math.Min(wl.Length, inten.Length);

        var points = _previewSeries.Points;
        points.Clear();
        if (points.Capacity < n) points.Capacity = n;
        for (int i = 0; i < n; i++) points.Add(new DataPoint(wl[i], inten[i]));

        if (n > 0)
            AxisText = string.Format(CultureInfo.InvariantCulture,
                "recording axis: {0:0.0}–{1:0.0} nm · {2} points", wl[0], wl[n - 1], n);

        PlotModel.InvalidatePlot(true);
    }

    private void BuildPlot()
    {
        PlotModel = new PlotModel
        {
            Title = "Frame being delivered",
            TitleFontSize = 12,
            PlotAreaBorderColor = OxyColor.FromRgb(200, 200, 200),
        };

        PlotModel.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Bottom,
            Title = "Wavelength (nm)",
            MajorGridlineStyle = LineStyle.Dot,
            MajorGridlineColor = OxyColor.FromRgb(230, 230, 230),
        });
        PlotModel.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Title = "Intensity (counts)",
            MajorGridlineStyle = LineStyle.Dot,
            MajorGridlineColor = OxyColor.FromRgb(230, 230, 230),
        });

        _previewSeries = new LineSeries
        {
            Color = OxyColors.SteelBlue,
            StrokeThickness = 1,
        };
        PlotModel.Series.Add(_previewSeries);
    }

    private static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    private static string FormatSpeed(double speed) =>
        speed.ToString(speed < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + "×";

    private void RaiseCanExec()
    {
        ChooseFileCommand.RaiseCanExecuteChanged();
        ClearFileCommand.RaiseCanExecuteChanged();
        PlayCommand.RaiseCanExecuteChanged();
        PauseCommand.RaiseCanExecuteChanged();
        RestartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Called by the host when playback reached the end of the recording, so the tab
    /// says so rather than leaving the operator to read it off a frozen progress bar.</summary>
    public void NotifyFinished()
    {
        StatusText = "Finished — the whole recording has been replayed. Press Restart to run it again.";
        Refresh();
    }

    public void Dispose()
    {
        _timer.Stop();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
