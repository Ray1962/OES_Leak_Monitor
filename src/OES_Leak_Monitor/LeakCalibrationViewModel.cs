using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace OES_Leak_Monitor;

/// <summary>One captured calibration point, as a bindable grid row.</summary>
public sealed class CalPointRowViewModel
{
    public CalPointRowViewModel(LeakCalPoint point) => Point = point;

    /// <summary>The underlying captured point (fed back into the fit on Save).</summary>
    public LeakCalPoint Point { get; }

    public double LeakRate => Point.LeakRate;
    public string LeakRateText => Point.LeakRate.ToString("G3", CultureInfo.InvariantCulture);
    public string Label => Point.Label;
    public int RatioCount => Point.Measurements.Count;

    /// <summary>Per-ratio rise summary, e.g. "R_O x=0.42±0.03; R_NO x=1.10±0.06".</summary>
    public string Summary => Point.Measurements.Count == 0
        ? "(no ratios — plasma off or no baseline?)"
        : string.Join("; ", Point.Measurements.Select(m =>
            $"{m.Key} x={m.X.ToString("0.000", CultureInfo.InvariantCulture)}" +
            $"±{m.Sigma.ToString("0.000", CultureInfo.InvariantCulture)}"));
}

/// <summary>
/// Backs the Leak Calibration tab — a guided wizard that builds a "ratio rise ↔ known leak rate"
/// calibration. The operator captures one point per calibrated-leak element (averaging each
/// ratio's rise relative to the active Golden Run baseline), then Save fits a per-ratio
/// sensitivity (<see cref="LeakRateEstimator.FitAll"/>) and persists a <see cref="LeakCalibration"/>
/// into <c>settings.json</c>. Engineer+ only. Capture reuses the engine's Golden Run machinery.
/// </summary>
public sealed class LeakCalibrationViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly LeakMonitorEngine _engine;
    private readonly Action _persistSettings;
    private readonly SystemLogger? _log;

    private bool _engineerPlus;
    private bool _suppressBaselineSelection;

    public LeakCalibrationViewModel(LeakMonitorEngine engine, Action persistSettings,
        SystemLogger? log = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _persistSettings = persistSettings ?? throw new ArgumentNullException(nameof(persistSettings));
        _log = log;

        Points = new ObservableCollection<CalPointRowViewModel>();
        GoldenRunNames = new ObservableCollection<string>();
        RefreshGoldenRunNames();
        _selectedGoldenRun = _engine.Settings.ActiveGoldenRun;
        _calibrationName = "Recipe 1 cal";

        CapturePointCommand = new RelayCommand(CapturePoint,
            () => _engineerPlus && !_captureActive && HasBaseline);
        CancelCaptureCommand = new RelayCommand(CancelCapture, () => _captureActive);
        RemovePointCommand = new RelayCommand(RemovePoint,
            () => _engineerPlus && _selectedPoint is not null);
        ClearPointsCommand = new RelayCommand(ClearPoints,
            () => _engineerPlus && Points.Count > 0 && !_captureActive);
        SaveCommand = new RelayCommand(Save,
            () => _engineerPlus && Points.Count > 0 && !_captureActive);

        _engine.SampleProcessed += OnSampleProcessed;
        _engine.CalibrationPointCaptured += OnCalibrationPointCaptured;
        _engine.GoldenRunCaptured += OnGoldenRunCaptured;
        _engine.RatiosReloaded += OnRatiosReloaded;
    }

    public ObservableCollection<CalPointRowViewModel> Points { get; }
    public ObservableCollection<string> GoldenRunNames { get; }

    public RelayCommand CapturePointCommand { get; }
    public RelayCommand CancelCaptureCommand { get; }
    public RelayCommand RemovePointCommand { get; }
    public RelayCommand ClearPointsCommand { get; }
    public RelayCommand SaveCommand { get; }

    private string _calibrationName;
    public string CalibrationName
    {
        get => _calibrationName;
        set { if (Set(ref _calibrationName, value)) SaveCommand.RaiseCanExecuteChanged(); }
    }

    private string? _selectedGoldenRun;
    public string? SelectedGoldenRun
    {
        get => _selectedGoldenRun;
        set
        {
            if (!Set(ref _selectedGoldenRun, value)) return;
            if (_suppressBaselineSelection) return;
            // Calibration rise is measured against the active baseline, so selecting one here
            // points the engine at it (same call the Leak Monitor tab makes).
            _engine.SelectGoldenRun(value);
            OnPropertyChanged(nameof(HasBaseline));
            OnPropertyChanged(nameof(BaselineNote));
            CapturePointCommand.RaiseCanExecuteChanged();
            StatusMessage = value is null
                ? "Pick a baseline (Golden Run) to calibrate against."
                : $"Calibrating against baseline “{value}”.";
        }
    }

    /// <summary>True when a baseline is active — required before any point can be captured.</summary>
    public bool HasBaseline => _engine.Settings.ActiveGoldenRun is not null;

    public string BaselineNote => HasBaseline
        ? ""
        : "No baseline selected. Capture or select a Golden Run on the Leak Monitor tab first.";

    private bool _captureActive;
    public bool CaptureActive
    {
        get => _captureActive;
        private set
        {
            if (!Set(ref _captureActive, value)) return;
            CapturePointCommand.RaiseCanExecuteChanged();
            CancelCaptureCommand.RaiseCanExecuteChanged();
            ClearPointsCommand.RaiseCanExecuteChanged();
            SaveCommand.RaiseCanExecuteChanged();
        }
    }

    private double _captureProgressPercent;
    public double CaptureProgressPercent
    {
        get => _captureProgressPercent;
        private set => Set(ref _captureProgressPercent, value);
    }

    private string _captureLeakRateText = "";
    public string CaptureLeakRateText
    {
        get => _captureLeakRateText;
        private set => Set(ref _captureLeakRateText, value);
    }

    private CalPointRowViewModel? _selectedPoint;
    public CalPointRowViewModel? SelectedPoint
    {
        get => _selectedPoint;
        set { if (Set(ref _selectedPoint, value)) RemovePointCommand.RaiseCanExecuteChanged(); }
    }

    private string _statusMessage = "Capture one point per calibrated leak, then Save.";
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }

    private string _fitSummary = "";
    public string FitSummary { get => _fitSummary; private set => Set(ref _fitSummary, value); }

    public string PointCountText => $"{Points.Count} calibration point(s)";

    /// <summary>Propagates the signed-in role: only Engineer+ may capture / save calibrations.</summary>
    public void SetRole(bool engineerOrHigher)
    {
        _engineerPlus = engineerOrHigher;
        CapturePointCommand.RaiseCanExecuteChanged();
        RemovePointCommand.RaiseCanExecuteChanged();
        ClearPointsCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
    }

    // --- engine events -------------------------------------------------------

    private void OnSampleProcessed(object? sender, LeakMonitorSnapshot snap) =>
        _dispatcher.BeginInvoke(() =>
        {
            CaptureActive = snap.CalibrationCaptureActive;
            CaptureProgressPercent = snap.CalibrationCaptureProgress01 * 100.0;
            if (snap.CalibrationCaptureActive)
                CaptureLeakRateText = snap.CalibrationLeakRate.ToString("G3", CultureInfo.InvariantCulture);
        });

    private void OnCalibrationPointCaptured(object? sender, LeakCalPoint point) =>
        _dispatcher.BeginInvoke(() =>
        {
            var row = new CalPointRowViewModel(point);
            Points.Add(row);
            SelectedPoint = row;
            OnPropertyChanged(nameof(PointCountText));
            ClearPointsCommand.RaiseCanExecuteChanged();
            SaveCommand.RaiseCanExecuteChanged();
            StatusMessage = point.Measurements.Count > 0
                ? $"Captured point at {row.LeakRateText} mbar·L/s ({point.Measurements.Count} ratio(s))."
                : $"Captured point at {row.LeakRateText} mbar·L/s but no ratios responded — check plasma / baseline.";
        });

    private void OnGoldenRunCaptured(object? sender, GoldenRun run) =>
        _dispatcher.BeginInvoke(() =>
        {
            _suppressBaselineSelection = true;
            RefreshGoldenRunNames();
            SelectedGoldenRun = run.Name;
            _suppressBaselineSelection = false;
            OnPropertyChanged(nameof(HasBaseline));
            OnPropertyChanged(nameof(BaselineNote));
            CapturePointCommand.RaiseCanExecuteChanged();
        });

    private void OnRatiosReloaded(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(() =>
        {
            // The ratio set changed — previously captured points may no longer line up, so
            // clear them rather than silently fitting a stale mix.
            if (Points.Count > 0)
            {
                Points.Clear();
                OnPropertyChanged(nameof(PointCountText));
                StatusMessage = "Ratio configuration changed — captured points cleared. Re-capture.";
            }
        });

    // --- commands ------------------------------------------------------------

    private void CapturePoint()
    {
        if (!HasBaseline)
        {
            StatusMessage = BaselineNote;
            return;
        }
        var dlg = new CalibrationPointDialog(SuggestNextLabel())
        {
            Owner = Application.Current?.MainWindow,
        };
        if (dlg.ShowDialog() != true) return;

        double seconds = _engine.Settings.CalibrationPointCaptureSeconds;
        _engine.BeginCalibrationPointCapture(dlg.LeakRate, dlg.Label, seconds);
        // Reflect capture immediately so Cancel is reachable even before the first sample
        // arrives (snapshots keep it set; finalize clears it). Without live acquisition no
        // samples flow and the point never completes — Cancel backs out cleanly.
        CaptureActive = true;
        CaptureProgressPercent = 0;
        CaptureLeakRateText = dlg.LeakRate.ToString("G3", CultureInfo.InvariantCulture);
        StatusMessage = $"Capturing point at {CaptureLeakRateText} mbar·L/s — keep the leak steady…";
        _log?.LogSystemEvent(LogSeverity.Information, "LeakCalPointStarted",
            $"Leak calibration point capture started at {CaptureLeakRateText} mbar·L/s",
            value: $"Seconds={seconds:F0}");
    }

    private string SuggestNextLabel() => $"Leak {Points.Count + 1}";

    private void CancelCapture()
    {
        _engine.CancelCalibrationCapture();
        // Clear the local flag directly: with acquisition idle no further snapshot arrives
        // to reset it (snapshots are what normally drive CaptureActive false on finalize).
        CaptureActive = false;
        CaptureProgressPercent = 0;
        StatusMessage = "Calibration point capture cancelled.";
    }

    private void RemovePoint()
    {
        if (_selectedPoint is null) return;
        int at = Points.IndexOf(_selectedPoint);
        Points.Remove(_selectedPoint);
        SelectedPoint = Points.Count > 0 ? Points[Math.Min(at, Points.Count - 1)] : null;
        OnPropertyChanged(nameof(PointCountText));
        ClearPointsCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
    }

    private void ClearPoints()
    {
        Points.Clear();
        SelectedPoint = null;
        FitSummary = "";
        OnPropertyChanged(nameof(PointCountText));
        ClearPointsCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        StatusMessage = "Points cleared.";
    }

    private void Save()
    {
        string name = string.IsNullOrWhiteSpace(CalibrationName) ? "Calibration 1" : CalibrationName.Trim();
        var pts = Points.Select(p => p.Point).Where(p => p.Measurements.Count > 0).ToList();
        if (pts.Count == 0)
        {
            StatusMessage = "No usable calibration points (none had responding ratios).";
            return;
        }

        var refLabels = _engine.CurrentReferenceLabels();
        var modes = _engine.CurrentMonitorModes();
        var fits = LeakRateEstimator.FitAll(pts, refLabels, modes);

        var cal = new LeakCalibration
        {
            Name = name,
            GoldenRunName = _engine.Settings.ActiveGoldenRun ?? "",
            CapturedUtc = DateTime.UtcNow,
            Points = pts,
            Fits = fits,
        };

        _engine.Settings.Calibrations.RemoveAll(c => c.Name == name);
        _engine.Settings.Calibrations.Add(cal);
        _engine.Settings.ActiveCalibration = name;

        try
        {
            _persistSettings();
        }
        catch (Exception ex)
        {
            StatusMessage = "Save failed: " + ex.Message;
            return;
        }

        // Activate the new calibration for the live leak-rate estimate immediately
        // (no acquisition restart needed — unlike a ratio-set change).
        _engine.ReloadCalibration();

        FitSummary = BuildFitSummary(fits);
        StatusMessage = $"Saved calibration “{name}” ({pts.Count} point(s), {fits.Count} ratio fit(s)).";
        _log?.LogSystemEvent(LogSeverity.Information, "LeakCalibrationSaved",
            $"Leak-rate calibration “{name}” saved with {pts.Count} point(s) against baseline " +
            $"{cal.GoldenRunName}.",
            value: FitSummary);
    }

    private static string BuildFitSummary(IReadOnlyList<RatioSensitivity> fits)
    {
        if (fits.Count == 0) return "No ratio fits.";
        return string.Join("\n", fits.Select(f =>
            $"{f.Key}: s={f.Slope.ToString("G3", CultureInfo.InvariantCulture)} per mbar·L/s " +
            $"(±{f.SlopeError.ToString("G2", CultureInfo.InvariantCulture)}), " +
            $"R²={f.RSquared.ToString("0.000", CultureInfo.InvariantCulture)}, " +
            $"{f.PointCount} pt, max {f.MaxCalibratedLeakRate.ToString("G3", CultureInfo.InvariantCulture)}"));
    }

    private void RefreshGoldenRunNames()
    {
        GoldenRunNames.Clear();
        foreach (var run in _engine.Settings.GoldenRuns)
            GoldenRunNames.Add(run.Name);
    }

    public void Dispose()
    {
        _engine.SampleProcessed -= OnSampleProcessed;
        _engine.CalibrationPointCaptured -= OnCalibrationPointCaptured;
        _engine.GoldenRunCaptured -= OnGoldenRunCaptured;
        _engine.RatiosReloaded -= OnRatiosReloaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(n);
        return true;
    }
}
