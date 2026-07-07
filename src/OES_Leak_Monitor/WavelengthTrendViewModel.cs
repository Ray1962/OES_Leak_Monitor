using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Aqst.OesSpectrometer.Models;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;

namespace OES_Leak_Monitor;

/// <summary>
/// View-model for the Monitor-tab intensity time-trend chart. Subscribes to the OES
/// spectrum stream and plots the peak intensity within a small window around each
/// tracked wavelength: the trigger ("threshold") wavelength configured in the
/// LoggerPanel, plus up to the first <see cref="MaxMonitoredWavelengths"/> monitored
/// wavelengths (the ones the LoggerPanel logs into the intensity CSV). A dashed
/// reference line marks the save-start threshold intensity.
///
/// <para>When <see cref="Normalize"/> is switched on, each line is divided by its value
/// at that moment, so every trend snaps to 1.0 and the chart then shows divergence from
/// the state when the box was ticked.</para>
/// </summary>
public sealed class WavelengthTrendViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>At most this many monitored wavelengths are added to the chart (the first N).</summary>
    public const int MaxMonitoredWavelengths = 5;

    /// <summary>Half-width of the window the peak is searched in, nm — absorbs slight
    /// wavelength-calibration drift / pixel jitter without picking up neighbouring lines.</summary>
    private const double PeakSearchHalfWidthNm = 0.5;

    /// <summary>How much trend history is kept on the chart (matches the Leak Monitor trend).</summary>
    private static readonly TimeSpan TrendRetention = TimeSpan.FromMinutes(30);

    /// <summary>Time constant for the optional EMA smoothing, seconds — the same
    /// α = 1 − exp(−dt/τ) scheme the Leak Monitor ratio EMA uses. Light by design: it damps
    /// per-frame noise for readability without adding much lag.</summary>
    private const double SmoothTauSeconds = 3.0;

    /// <summary>Series colours; index 0 is the trigger wavelength, the rest are monitored.</summary>
    private static readonly OxyColor[] Palette =
    {
        OxyColors.SteelBlue, OxyColors.ForestGreen, OxyColors.DarkOrange,
        OxyColors.MediumPurple, OxyColors.Teal, OxyColors.Sienna,
    };

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    private readonly List<Track> _tracked = new();
    /// <summary>Snapshot of the tracked wavelengths for the off-thread spectrum reader;
    /// swapped wholesale (atomic reference assignment) whenever the set is rebuilt.</summary>
    private volatile double[] _trackedNm = Array.Empty<double>();

    private DateTimeAxis _timeAxis = null!;
    private LinearAxis _valueAxis = null!;
    private LineAnnotation _thresholdLine = null!;
    private bool _timeAxisZoomed;

    private double _triggerNm;
    private double _thresholdIntensity;
    private IReadOnlyList<double> _monitoredNm = Array.Empty<double>();

    public WavelengthTrendViewModel(double triggerWavelengthNm,
        double thresholdIntensity, IEnumerable<double>? monitoredWavelengthsNm)
    {
        _triggerNm = triggerWavelengthNm;
        _thresholdIntensity = thresholdIntensity;
        _monitoredNm = NormalizeMonitored(monitoredWavelengthsNm);

        BuildPlot();
        RebuildSeries();
        ZoomAllCommand = new RelayCommand(ZoomAll);
    }

    public PlotModel PlotModel { get; private set; } = null!;
    public RelayCommand ZoomAllCommand { get; }

    /// <summary>Display string for the trigger ("selected") wavelength, e.g. "337.0 nm".</summary>
    public string WavelengthText => _triggerNm > 0
        ? _triggerNm.ToString("0.0", CultureInfo.InvariantCulture) + " nm"
        : "(not set)";

    private string _latestText = "Waiting for spectra…";
    public string LatestText { get => _latestText; private set => Set(ref _latestText, value); }

    /// <summary>When true, every line is divided by its value at the moment the box was
    /// ticked so all trends snap to 1.0 — useful for comparing relative change across
    /// wavelengths.</summary>
    private bool _normalize;
    public bool Normalize
    {
        get => _normalize;
        // Re-fit the y-axis: normalized vs raw scales differ by orders of magnitude.
        set { if (Set(ref _normalize, value)) ReprojectAll(resetAxes: true); }
    }

    /// <summary>When true, each line is EMA-smoothed (τ = <see cref="SmoothTauSeconds"/>) to damp
    /// per-frame noise. Off by default so the chart shows the raw detector output. Toggling
    /// re-projects the retained history, so the smoothing also applies to what's already drawn.</summary>
    private bool _smooth;
    public bool Smooth
    {
        get => _smooth;
        // Smoothing keeps the same y-scale, so don't disturb the operator's current zoom.
        set { if (Set(ref _smooth, value)) ReprojectAll(resetAxes: false); }
    }

    /// <summary>
    /// Re-points the trend at a new trigger wavelength, save threshold, and monitored-wavelength
    /// set (called when the LoggerPanel configuration is applied). Past points were measured at
    /// the old wavelengths, so any change to the tracked set rebuilds — and clears — the trends.
    /// Must be called on the UI thread.
    /// </summary>
    public void Configure(double triggerWavelengthNm, double thresholdIntensity,
        IEnumerable<double>? monitoredWavelengthsNm)
    {
        var newMonitored = NormalizeMonitored(monitoredWavelengthsNm);
        bool changed = Math.Abs(triggerWavelengthNm - _triggerNm) > 1e-6
                    || !SequenceClose(newMonitored, _monitoredNm);

        _triggerNm = triggerWavelengthNm;
        _thresholdIntensity = thresholdIntensity;
        _monitoredNm = newMonitored;

        if (changed)
        {
            RebuildSeries();
            LatestText = "Waiting for spectra…";
            OnPropertyChanged(nameof(WavelengthText));
        }
        UpdateThresholdLine();
        PlotModel.InvalidatePlot(true);
    }

    /// <summary>
    /// Clears all retained trend data so the chart restarts its time axis from the next
    /// incoming sample — used by the Monitor-tab Reset to begin a fresh experiment run.
    /// Keeps the tracked-wavelength configuration and the Normalize toggle (each line
    /// re-captures its baseline from its first post-reset sample). Must be called on the
    /// UI thread.
    /// </summary>
    public void Reset()
    {
        foreach (var t in _tracked)
        {
            t.Raw.Clear();
            t.Series.Points.Clear();
            t.Baseline = double.NaN;
            t.HasEma = false;
            t.Ema = double.NaN;
            t.LastX = 0;
        }
        LatestText = "Waiting for spectra…";
        PlotModel.ResetAllAxes();
        _timeAxisZoomed = false;
        PlotModel.InvalidatePlot(true);
    }

    /// <summary>Filters out non-positive entries and keeps only the first
    /// <see cref="MaxMonitoredWavelengths"/> monitored wavelengths.</summary>
    private static IReadOnlyList<double> NormalizeMonitored(IEnumerable<double>? src) =>
        src is null
            ? Array.Empty<double>()
            : src.Where(w => w > 0).Take(MaxMonitoredWavelengths).ToArray();

    private static bool SequenceClose(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (Math.Abs(a[i] - b[i]) > 1e-6) return false;
        return true;
    }

    // --- tracked-series management ------------------------------------------

    /// <summary>The ordered, de-duplicated wavelengths to plot: the trigger wavelength first,
    /// then the monitored wavelengths, dropping any that repeat an earlier one.</summary>
    private List<(double Nm, bool IsTrigger)> BuildDesiredList()
    {
        var list = new List<(double Nm, bool IsTrigger)>();
        void TryAdd(double nm, bool isTrigger)
        {
            if (nm <= 0) return;
            if (list.Any(e => Math.Abs(e.Nm - nm) < 1e-3)) return;
            list.Add((nm, isTrigger));
        }
        TryAdd(_triggerNm, isTrigger: true);
        foreach (var nm in _monitoredNm) TryAdd(nm, isTrigger: false);
        return list;
    }

    /// <summary>Rebuilds the chart's line series from the current tracked-wavelength set.</summary>
    private void RebuildSeries()
    {
        foreach (var t in _tracked) PlotModel.Series.Remove(t.Series);
        _tracked.Clear();

        var desired = BuildDesiredList();
        for (int i = 0; i < desired.Count; i++)
        {
            var (nm, isTrigger) = desired[i];
            string label = nm.ToString("0.0", CultureInfo.InvariantCulture) + " nm"
                         + (isTrigger ? " (selected)" : "");
            var series = new LineSeries
            {
                Title = label,
                Color = Palette[i % Palette.Length],
                StrokeThickness = isTrigger ? 2.0 : 1.5,
                CanTrackerInterpolatePoints = false,
            };
            PlotModel.Series.Add(series);
            _tracked.Add(new Track { Nm = nm, IsTrigger = isTrigger, Series = series });
        }

        _trackedNm = _tracked.Select(t => t.Nm).ToArray();
    }

    // --- spectrum ingest -----------------------------------------------------

    /// <summary>
    /// Feeds one spectrum frame into the trend. Called by <c>MainViewModel</c> at the device
    /// fan-out point (after any Test-mode CSV substitution) rather than the view-model
    /// subscribing to the device directly, so the chart sees the same effective spectrum as
    /// the intensity logger and leak engine. May be called off the UI thread; the heavy
    /// peak-search runs on the caller's thread and the chart mutation is marshalled via the
    /// dispatcher (as before).
    /// </summary>
    public void OnSpectrum(SpectrumSample sample)
    {
        if (sample is null) return;
        double[] nms = _trackedNm;
        if (nms.Length == 0) return;

        var wl = sample.Wavelengths;
        var inten = sample.Intensities;
        var values = new double[nms.Length];
        for (int i = 0; i < nms.Length; i++)
            values[i] = PeakInWindow(wl, inten,
                nms[i] - PeakSearchHalfWidthNm, nms[i] + PeakSearchHalfWidthNm);

        var ts = sample.Timestamp;
        _dispatcher.BeginInvoke(() => Append(ts, nms, values));
    }

    private void Append(DateTime timestamp, double[] nms, double[] values)
    {
        // The values were computed against this exact array; if the tracked set has
        // since been rebuilt (a config change), the reference no longer matches — drop
        // the stale frame rather than misalign values to series.
        if (!ReferenceEquals(nms, _trackedNm)) return;

        double x = DateTimeAxis.ToDouble(timestamp);
        double cutoff = DateTimeAxis.ToDouble(timestamp - TrendRetention);

        for (int i = 0; i < _tracked.Count; i++)
        {
            double v = values[i];
            if (double.IsNaN(v)) continue;

            var t = _tracked[i];
            // Advance the running EMA every frame (O(1)) so it stays live whether or not
            // smoothing is currently shown; the display just picks the smoothed or raw value.
            UpdateEma(t, x, v);
            double shown = _smooth ? t.Ema : v;

            // The normalize baseline is captured in the display domain, so a smoothed line
            // also snaps to 1.0. A line with no baseline yet — Normalize just switched on, or
            // the track was freshly rebuilt — captures it from this first sample.
            if (_normalize && double.IsNaN(t.Baseline)) t.Baseline = shown;
            double disp = _normalize && t.Baseline > 0 ? shown / t.Baseline : shown;

            t.Raw.Add(new DataPoint(x, v));
            t.Series.Points.Add(new DataPoint(x, disp));

            // Drop points older than the retention window from raw + display in lockstep.
            int drop = 0;
            while (drop < t.Raw.Count && t.Raw[drop].X < cutoff) drop++;
            if (drop > 0)
            {
                t.Raw.RemoveRange(0, drop);
                t.Series.Points.RemoveRange(0, drop);
            }

            if (t.IsTrigger)
                LatestText = v.ToString("N0", CultureInfo.InvariantCulture)
                           + (_thresholdIntensity > 0 && v >= _thresholdIntensity
                                ? "  ▲ above save threshold"
                                : "");
        }

        // Once the operator has zoomed / panned, keep the window sliding with live data.
        if (_timeAxisZoomed)
        {
            double width = _timeAxis.ActualMaximum - _timeAxis.ActualMinimum;
            if (width > 0) _timeAxis.Zoom(x - width, x);
        }
        PlotModel.InvalidatePlot(true);
    }

    /// <summary>Advances one track's running EMA with a new (x, value). <paramref name="x"/> is
    /// the OxyPlot time-axis value (an OLE date, i.e. days), converted to seconds for the
    /// α = 1 − exp(−dt/τ) step so the smoothing is frame-rate independent.</summary>
    private static void UpdateEma(Track t, double x, double v)
    {
        if (!t.HasEma) { t.Ema = v; t.HasEma = true; }
        else
        {
            double dt = (x - t.LastX) * 86400.0;
            if (dt <= 0) dt = 0.001;
            double alpha = 1.0 - Math.Exp(-dt / SmoothTauSeconds);
            t.Ema += alpha * (v - t.Ema);
        }
        t.LastX = x;
    }

    /// <summary>Re-projects every series from its retained raw data under the current
    /// Smooth / Normalize settings, so a toggle applies to the history already on screen — not
    /// just new points — and updates the value axis / threshold line to match.</summary>
    private void ReprojectAll(bool resetAxes)
    {
        foreach (var t in _tracked) Reproject(t);
        UpdateValueAxis();
        UpdateThresholdLine();
        if (resetAxes)
        {
            PlotModel.ResetAllAxes();
            _timeAxisZoomed = false;
        }
        PlotModel.InvalidatePlot(true);
    }

    /// <summary>Rebuilds one track's plotted points from its raw data: replays the EMA across
    /// the retained points to redraw smoothed history, re-captures the normalize baseline in the
    /// display domain (latest value, so the most recent point snaps to 1.0), and leaves the live
    /// EMA state consistent so the next <see cref="Append"/> continues it seamlessly.</summary>
    private void Reproject(Track t)
    {
        int n = t.Raw.Count;
        var shown = new double[n];
        double ema = double.NaN; bool has = false; double lastX = 0;
        for (int i = 0; i < n; i++)
        {
            var p = t.Raw[i];
            if (!has) { ema = p.Y; has = true; }
            else
            {
                double dt = (p.X - lastX) * 86400.0;
                if (dt <= 0) dt = 0.001;
                double alpha = 1.0 - Math.Exp(-dt / SmoothTauSeconds);
                ema += alpha * (p.Y - ema);
            }
            lastX = p.X;
            shown[i] = _smooth ? ema : p.Y;
        }
        t.HasEma = has; t.Ema = ema; t.LastX = lastX;

        // Baseline = latest shown value when normalizing; NaN otherwise so a later Normalize
        // re-captures from the next sample.
        t.Baseline = _normalize && n > 0 ? shown[n - 1] : double.NaN;

        var pts = t.Series.Points;
        pts.Clear();
        for (int i = 0; i < n; i++)
        {
            double d = _normalize && t.Baseline > 0 ? shown[i] / t.Baseline : shown[i];
            pts.Add(new DataPoint(t.Raw[i].X, d));
        }
    }

    /// <summary>Largest intensity whose wavelength falls in [lo, hi]; NaN if the window is empty.
    /// Assumes the wavelength array is ascending (standard for the OES SDK).</summary>
    private static double PeakInWindow(float[]? wavelengths, float[]? intensities, double lo, double hi)
    {
        if (wavelengths is null || intensities is null) return double.NaN;
        int n = Math.Min(wavelengths.Length, intensities.Length);
        double peak = double.NaN;
        for (int i = 0; i < n; i++)
        {
            double w = wavelengths[i];
            if (w < lo) continue;
            if (w > hi) break;
            double v = intensities[i];
            if (double.IsNaN(peak) || v > peak) peak = v;
        }
        return peak;
    }

    // --- plot ----------------------------------------------------------------

    private void BuildPlot()
    {
        PlotModel = new PlotModel
        {
            Title = "Intensity trend",
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

        _valueAxis = new LinearAxis
        {
            Position = AxisPosition.Left,
            AbsoluteMinimum = 0,
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = OxyColor.FromRgb(0xE8, 0xE8, 0xE8),
        };
        PlotModel.Axes.Add(_valueAxis);
        UpdateValueAxis();

        _thresholdLine = new LineAnnotation
        {
            Type = LineAnnotationType.Horizontal,
            Color = OxyColors.Firebrick,
            LineStyle = LineStyle.Dash,
            Text = "save threshold",
            TextColor = OxyColors.Firebrick,
            FontSize = 10,
        };
        PlotModel.Annotations.Add(_thresholdLine);
        UpdateThresholdLine();

        PlotModel.Legends.Add(new Legend
        {
            LegendPosition = LegendPosition.LeftTop,
            LegendPlacement = LegendPlacement.Inside,
            LegendFontSize = 11,
            LegendBackground = OxyColor.FromAColor(0xC0, OxyColors.White),
        });
    }

    private void UpdateValueAxis() =>
        _valueAxis.Title = (_normalize ? "Relative to start value" : "Intensity (counts)")
                         + (_smooth ? " · smoothed" : "");

    /// <summary>Positions (or hides) the dashed save-start threshold reference line. The line
    /// is an absolute intensity, so it is meaningless — and hidden — while normalized.</summary>
    private void UpdateThresholdLine() =>
        _thresholdLine.Y = (!_normalize && _thresholdIntensity > 0)
            ? _thresholdIntensity
            : double.NaN;

    /// <summary>Tracks whether the operator has zoomed / panned, so live updates can keep
    /// a zoomed window following the latest data.</summary>
    private void OnTimeAxisChanged(object? sender, AxisChangedEventArgs e) =>
        _timeAxisZoomed = e.ChangeType != AxisChangeTypes.Reset;

    private void ZoomAll()
    {
        PlotModel.ResetAllAxes();
        _timeAxisZoomed = false;
        PlotModel.InvalidatePlot(true);
    }

    public void Dispose()
    {
#pragma warning disable CS0618 // AxisChanged: still the supported zoom/pan hook in OxyPlot 2.2.
        _timeAxis.AxisChanged -= OnTimeAxisChanged;
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

    /// <summary>One tracked wavelength: its raw trend data, its normalization baseline
    /// (first recorded value), and the OxyPlot series showing the current display values.</summary>
    private sealed class Track
    {
        public double Nm;
        public bool IsTrigger;
        public LineSeries Series = null!;
        /// <summary>Raw (timestamp, intensity) points within the retention window.</summary>
        public readonly List<DataPoint> Raw = new();
        /// <summary>Normalization reference — the line's value when Normalize was switched
        /// on; NaN when not normalized or not yet captured.</summary>
        public double Baseline = double.NaN;

        // --- running EMA state for the optional Smooth mode ---
        /// <summary>Current EMA of the raw intensity; NaN until the first sample.</summary>
        public double Ema = double.NaN;
        public bool HasEma;
        /// <summary>Time-axis value (OLE date, days) of the last EMA update, for the dt step.</summary>
        public double LastX;
    }
}
