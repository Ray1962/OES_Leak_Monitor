using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;

namespace OES_Leak_Monitor;

/// <summary>
/// View-model for the Leak Monitor tab. Wraps <see cref="LeakMonitorEngine"/>, exposes the
/// per-ratio rows, the composite alarm banner, the % -of-baseline trend chart, and the
/// Golden Run capture / select / acknowledge commands.
/// </summary>
public sealed class LeakMonitorViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan TrendRetention = TimeSpan.FromMinutes(30);
    private static readonly OxyColor[] TrendPalette =
    {
        OxyColors.SteelBlue, OxyColors.ForestGreen, OxyColors.DarkOrange, OxyColors.MediumPurple,
    };

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly LeakMonitorEngine _engine;
    private readonly SystemLogger? _systemLogger;
    private readonly Dictionary<string, RatioViewModel> _ratioByKey = new();
    private readonly Dictionary<string, LineSeries> _seriesByKey = new();
    private DateTimeAxis _timeAxis = null!;
    private bool _timeAxisZoomed;

    private bool _operatorPlus, _engineerPlus;
    private bool _suppressGoldenRunSelection;

    public LeakMonitorViewModel(LeakMonitorEngine engine, SystemLogger? systemLogger = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _systemLogger = systemLogger;

        Ratios = new ObservableCollection<RatioViewModel>();
        GoldenRunNames = new ObservableCollection<string>();

        BuildPlot();

        BuildRatioRows();

        RefreshGoldenRunNames();
        _selectedGoldenRun = _engine.Settings.ActiveGoldenRun;

        CaptureGoldenRunCommand = new RelayCommand(CaptureGoldenRun,
            () => _engineerPlus && !_captureActive);
        CancelCaptureCommand = new RelayCommand(() => _engine.CancelGoldenRunCapture(),
            () => _captureActive);
        AcknowledgeCommand = new RelayCommand(() => _engine.Acknowledge(),
            () => _operatorPlus && _overallState == LeakAlarmLevel.Alarm);
        ZoomAllCommand = new RelayCommand(ZoomAll);

        _engine.SampleProcessed   += OnSampleProcessed;
        _engine.AlarmStateChanged += OnAlarmStateChanged;
        _engine.GoldenRunCaptured += OnGoldenRunCaptured;
        _engine.RatiosReloaded    += OnRatiosReloaded;
    }

    /// <summary>Builds one bindable row and one trend series per monitored ratio.</summary>
    private void BuildRatioRows()
    {
        int idx = 0;
        foreach (var def in _engine.MonitoredRatios)
        {
            var rvm = new RatioViewModel(def.Key, def.DisplayName);
            _ratioByKey[def.Key] = rvm;
            Ratios.Add(rvm);
            AddSeries(def, idx++);
            _seriesByKey[def.Key].IsVisible = def.Enabled;
        }
    }

    /// <summary>Rebuilds the rows and trend series after the engine reloads its ratio set
    /// (a Ratio Setup edit applied on acquisition restart).</summary>
    private void RebuildRatios()
    {
        Ratios.Clear();
        _ratioByKey.Clear();
        foreach (var s in _seriesByKey.Values) PlotModel.Series.Remove(s);
        _seriesByKey.Clear();
        BuildRatioRows();
        PlotModel.InvalidatePlot(true);
        StatusMessage = "Ratio configuration applied.";
    }

    public ObservableCollection<RatioViewModel> Ratios { get; }
    public ObservableCollection<string> GoldenRunNames { get; }
    public PlotModel PlotModel { get; private set; } = null!;

    public RelayCommand CaptureGoldenRunCommand { get; }
    public RelayCommand CancelCaptureCommand { get; }
    public RelayCommand AcknowledgeCommand { get; }
    public RelayCommand ZoomAllCommand { get; }

    // --- composite status ----------------------------------------------------

    private LeakAlarmLevel _overallState = LeakAlarmLevel.Idle;
    private bool _testMode;

    public string OverallText => _overallState switch
    {
        LeakAlarmLevel.Alarm   => "ALARM — suspected O₂ / air leak",
        LeakAlarmLevel.Warning => "WARNING — oxygen ratio rising",
        LeakAlarmLevel.Normal  => "OK — within baseline",
        _                      => "Idle — waiting for plasma / baseline",
    };

    public Brush OverallBrush => _overallState switch
    {
        LeakAlarmLevel.Alarm   => Brushes.Firebrick,
        LeakAlarmLevel.Warning => Brushes.DarkOrange,
        LeakAlarmLevel.Normal  => Brushes.ForestGreen,
        _                      => Brushes.SlateGray,
    };

    public bool TestMode
    {
        get => _testMode;
        private set { if (Set(ref _testMode, value)) OnPropertyChanged(nameof(TestModeNote)); }
    }

    public string TestModeNote => _testMode
        ? "TEST MODE — synthetic spectra; alarms are suppressed."
        : "";

    private string _statusMessage = "Leak monitor ready.";
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }

    // --- quantitative leak rate (from the active calibration) ----------------

    private string _leakRateText = "Leak rate: not calibrated";
    public string LeakRateText { get => _leakRateText; private set => Set(ref _leakRateText, value); }

    private Brush _leakRateBrush = Brushes.SlateGray;
    public Brush LeakRateBrush { get => _leakRateBrush; private set => Set(ref _leakRateBrush, value); }

    /// <summary>Formats the per-frame leak-rate estimate (and its validity) for the readout
    /// above the trend.</summary>
    private void ApplyLeakRate(LeakMonitorSnapshot snap)
    {
        switch (snap.CalibrationStatus)
        {
            case CalibrationStatus.NotCalibrated:
                LeakRateText = "Leak rate: not calibrated";
                LeakRateBrush = Brushes.SlateGray;
                return;

            case CalibrationStatus.BaselineMismatch:
                // A calibration is selected but the active baseline isn't the one it was
                // captured against — estimation is suspended rather than silently wrong.
                LeakRateText = $"Leak rate: calibration “{snap.ActiveCalibration}” needs its " +
                               "baseline — select that Golden Run to enable estimation";
                LeakRateBrush = Brushes.DarkOrange;
                return;

            case CalibrationStatus.Active:
                var est = snap.LeakRate;
                if (est is not { HasEstimate: true })
                {
                    LeakRateText = "Leak rate: — (waiting for plasma / baseline)";
                    LeakRateBrush = Brushes.SlateGray;
                    return;
                }
                string s = $"Leak rate ≈ {est.LeakRate:G3} ± {est.Sigma:G2} mbar·L/s" +
                           $"   ·   {est.Confidence * 100:0}% confidence";
                if (est.OutOfCalibratedRange) s += "   ·   extrapolated";
                LeakRateText = s;
                LeakRateBrush = est.OutOfCalibratedRange ? Brushes.DarkOrange : Brushes.MidnightBlue;
                return;
        }
    }

    // --- Golden Run ----------------------------------------------------------

    private string? _selectedGoldenRun;
    public string? SelectedGoldenRun
    {
        get => _selectedGoldenRun;
        set
        {
            if (!Set(ref _selectedGoldenRun, value)) return;
            if (_suppressGoldenRunSelection) return;
            _engine.SelectGoldenRun(value);
            StatusMessage = value is null ? "No baseline selected." : $"Baseline: {value}";
            _systemLogger?.LogSystemEvent(LogSeverity.Information, "GoldenRunSelected",
                $"Active leak-monitor baseline set to {value ?? "(none)"}");
        }
    }

    private bool _captureActive;
    public bool CaptureActive
    {
        get => _captureActive;
        private set { if (Set(ref _captureActive, value)) RaiseCanExec(); }
    }

    private double _captureProgressPercent;
    public double CaptureProgressPercent
    {
        get => _captureProgressPercent;
        private set => Set(ref _captureProgressPercent, value);
    }

    // --- role gating ---------------------------------------------------------

    /// <summary>Propagates the signed-in role: Operator+ may acknowledge, Engineer+ may capture.</summary>
    public void SetRole(bool operatorOrHigher, bool engineerOrHigher)
    {
        _operatorPlus = operatorOrHigher;
        _engineerPlus = engineerOrHigher;
        RaiseCanExec();
    }

    // --- engine events -------------------------------------------------------

    private void OnSampleProcessed(object? sender, LeakMonitorSnapshot snap) =>
        _dispatcher.BeginInvoke(() => ApplySnapshot(snap));

    private void OnAlarmStateChanged(object? sender, LeakAlarmEventArgs e) =>
        _dispatcher.BeginInvoke(() =>
        {
            if (e.NewLevel == LeakAlarmLevel.Alarm)
                StatusMessage = "ALARM raised — check for an air / O₂ leak.";
            else if (e.NewLevel == LeakAlarmLevel.Normal && e.OldLevel != LeakAlarmLevel.Idle)
                StatusMessage = "Recovered to baseline.";
        });

    private void OnGoldenRunCaptured(object? sender, GoldenRun run) =>
        _dispatcher.BeginInvoke(() =>
        {
            // Suppress for the whole refresh: ObservableCollection.Clear() makes the
            // bound ComboBox null its SelectedItem, which would otherwise round-trip
            // through the setter as SelectGoldenRun(null) and wipe the baseline that
            // was just captured (the "100% appears then vanishes" bug).
            _suppressGoldenRunSelection = true;
            RefreshGoldenRunNames();
            SelectedGoldenRun = run.Name;
            _suppressGoldenRunSelection = false;
            StatusMessage =
                $"Golden Run “{run.Name}” captured — {run.Baselines.Count} ratio baseline(s).";
        });

    private void ApplySnapshot(LeakMonitorSnapshot snap)
    {
        TestMode = snap.TestMode;
        CaptureActive = snap.CaptureActive;
        CaptureProgressPercent = snap.CaptureProgress01 * 100.0;
        ApplyLeakRate(snap);

        if (_overallState != snap.Overall)
        {
            _overallState = snap.Overall;
            OnPropertyChanged(nameof(OverallText));
            OnPropertyChanged(nameof(OverallBrush));
            RaiseCanExec();
        }

        double x = DateTimeAxis.ToDouble(snap.Timestamp);
        double cutoff = DateTimeAxis.ToDouble(snap.Timestamp - TrendRetention);
        foreach (var rs in snap.Ratios)
        {
            if (_ratioByKey.TryGetValue(rs.Key, out var rvm)) rvm.Apply(rs);
            if (_seriesByKey.TryGetValue(rs.Key, out var series) &&
                !double.IsNaN(rs.PercentOfBaseline))
            {
                var pts = series.Points;
                pts.Add(new DataPoint(x, rs.PercentOfBaseline));
                int drop = 0;
                while (drop < pts.Count && pts[drop].X < cutoff) drop++;
                if (drop > 0) pts.RemoveRange(0, drop);
            }
        }

        // Once the user zooms or pans the time axis, OxyPlot stops auto-scaling it.
        // Keep the zoomed window sliding forward with live data so the newest samples
        // stay visible; the Zoom All button clears the zoom to fit all retained data.
        if (_timeAxisZoomed)
        {
            double width = _timeAxis.ActualMaximum - _timeAxis.ActualMinimum;
            if (width > 0) _timeAxis.Zoom(x - width, x);
        }
        PlotModel.InvalidatePlot(true);
    }

    /// <summary>Tracks whether the operator has zoomed / panned the time axis, so live
    /// updates can keep a zoomed window following the latest data.</summary>
    private void OnTimeAxisChanged(object? sender, AxisChangedEventArgs e) =>
        _timeAxisZoomed = e.ChangeType != AxisChangeTypes.Reset;

    /// <summary>Resets the trend chart axes to fit all retained data.</summary>
    private void ZoomAll()
    {
        PlotModel.ResetAllAxes();
        _timeAxisZoomed = false;
        PlotModel.InvalidatePlot(true);
    }

    // --- commands ------------------------------------------------------------

    private void OnRatiosReloaded(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(RebuildRatios);

    private void CaptureGoldenRun()
    {
        double seconds = _engine.Settings.GoldenRunCaptureSeconds;
        var dlg = new GoldenRunCaptureDialog(_selectedGoldenRun ?? "Recipe 1", seconds)
        {
            Owner = Application.Current?.MainWindow,
        };
        if (dlg.ShowDialog() != true) return;

        _engine.BeginGoldenRunCapture(dlg.RunName, seconds);
        StatusMessage = $"Capturing Golden Run “{dlg.RunName}” — keep the process steady…";
        _systemLogger?.LogSystemEvent(LogSeverity.Information, "GoldenRunCaptureStarted",
            $"Golden Run capture started: {dlg.RunName}", value: $"Seconds={seconds:F0}");
        RaiseCanExec();
    }

    // --- plot setup ----------------------------------------------------------

    private void BuildPlot()
    {
        PlotModel = new PlotModel
        {
            Title = "Ratio trend (% of baseline)",
            TitleFontSize = 13,
            Background = OxyColors.White,
            PlotAreaBorderColor = OxyColor.FromRgb(0xCC, 0xCC, 0xCC),
        };
        _timeAxis = new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            StringFormat = "HH:mm:ss",
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = OxyColor.FromRgb(0xE8, 0xE8, 0xE8),
        };
        PlotModel.Axes.Add(_timeAxis);
#pragma warning disable CS0618 // AxisChanged: still the supported zoom/pan hook in OxyPlot 2.2.
        _timeAxis.AxisChanged += OnTimeAxisChanged;
#pragma warning restore CS0618
        PlotModel.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Title = "% of baseline",
            // Auto-scales to the data, but never shows a window narrower than 10
            // percentage points, and never drops below 0 (% of baseline can't be negative).
            AbsoluteMinimum = 0,
            MinimumRange = 10,
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = OxyColor.FromRgb(0xE8, 0xE8, 0xE8),
        });
        AddThresholdLine(100, OxyColors.Gray,       "baseline");
        AddThresholdLine(120, OxyColors.DarkOrange, "warn ~120%");
        AddThresholdLine(150, OxyColors.Firebrick,  "alarm ~150%");
        PlotModel.Legends.Add(new Legend
        {
            LegendPosition = LegendPosition.LeftTop,
            LegendPlacement = LegendPlacement.Inside,
            LegendFontSize = 11,
            LegendBackground = OxyColor.FromAColor(0xC0, OxyColors.White),
        });
    }

    private void AddThresholdLine(double y, OxyColor color, string text) =>
        PlotModel.Annotations.Add(new LineAnnotation
        {
            Type = LineAnnotationType.Horizontal,
            Y = y,
            Color = color,
            LineStyle = LineStyle.Dash,
            Text = text,
            TextColor = color,
            FontSize = 10,
        });

    private void AddSeries(RatioDefinition def, int index)
    {
        var series = new LineSeries
        {
            Title = def.DisplayName,
            Color = TrendPalette[index % TrendPalette.Length],
            StrokeThickness = 1.5,
            CanTrackerInterpolatePoints = false,
        };
        PlotModel.Series.Add(series);
        _seriesByKey[def.Key] = series;
    }

    private void RefreshGoldenRunNames()
    {
        GoldenRunNames.Clear();
        foreach (var run in _engine.Settings.GoldenRuns)
            GoldenRunNames.Add(run.Name);
    }

    private void RaiseCanExec()
    {
        CaptureGoldenRunCommand.RaiseCanExecuteChanged();
        CancelCaptureCommand.RaiseCanExecuteChanged();
        AcknowledgeCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _engine.SampleProcessed   -= OnSampleProcessed;
        _engine.AlarmStateChanged -= OnAlarmStateChanged;
        _engine.GoldenRunCaptured -= OnGoldenRunCaptured;
        _engine.RatiosReloaded    -= OnRatiosReloaded;
#pragma warning disable CS0618 // AxisChanged: still the supported zoom/pan hook in OxyPlot 2.2.
        _timeAxis.AxisChanged     -= OnTimeAxisChanged;
#pragma warning restore CS0618
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
