using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace OES_Leak_Monitor;

/// <summary>
/// Bindable, read-only row for one monitored ratio in the Leak Monitor panel. The ratio
/// definition (lines, reference, enable flag, thresholds) is edited on the Ratio Setup
/// tab; this row only displays the live state the engine reports.
/// </summary>
public sealed class RatioViewModel : INotifyPropertyChanged
{
    public RatioViewModel(string key, string displayName)
    {
        Key = key;
        DisplayName = displayName;
    }

    public string Key { get; }
    public string DisplayName { get; }

    private bool _isSelected;
    /// <summary>The row whose thresholds the trend chart draws (#104).</summary>
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    private RatioState _state = RatioState.NoBaseline;
    public RatioState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(StateBrush));
            }
        }
    }

    private JudgingPaths _enabledPaths = JudgingPaths.GoldenRun, _raisedBy;
    private bool _warmingUp;

    /// <summary>
    /// The state, and for Warning / Alarm on an entry that runs step judging, the path that
    /// raised it ("ALARM · Step", "WARNING · GR", "ALARM · GR+Step") — an Alarm whose % of
    /// Golden Run looks unremarkable has to say where it came from, or the operator looks in
    /// the wrong column. An entry on the Golden Run path alone has only one possible source and
    /// keeps the plain word.
    /// </summary>
    public string StateText => _state switch
    {
        RatioState.Normal     => "Normal",
        RatioState.Warning    => "WARNING" + SourceSuffix,
        RatioState.Alarm      => "ALARM" + SourceSuffix,
        RatioState.NoPlasma   => "No Plasma",
        RatioState.LowSignal  => "Low Signal",
        // A step-judged entry gathering its rolling baseline is not missing anything.
        RatioState.NoBaseline => _warmingUp ? "Warming up" : "No Baseline",
        RatioState.Disabled   => "Disabled",
        // Says which of the three quiet states this is. "Disabled" is a decision someone made,
        // "No Plasma" is the tool idle or a fault, and this one is neither — the tool is running
        // a process step this entry does not measure.
        RatioState.NotApplicable => "Not this process",
        // Recorded, not judged. "Normal" would be a reassurance nobody measured.
        RatioState.Observing  => "Observing",
        _                     => _state.ToString(),
    };

    private string SourceSuffix =>
        _enabledPaths.HasFlag(JudgingPaths.Step) && _raisedBy != JudgingPaths.None
            ? " · " + JudgingPathsText.Label(_raisedBy)
            : "";

    public Brush StateBrush => _state switch
    {
        RatioState.Normal  => Brushes.ForestGreen,
        RatioState.Warning => Brushes.DarkOrange,
        RatioState.Alarm   => Brushes.Firebrick,
        RatioState.NoPlasma => Brushes.Gray,
        RatioState.LowSignal => Brushes.SlateBlue,
        RatioState.Disabled => Brushes.Silver,
        RatioState.NotApplicable => Brushes.DarkSlateGray,
        RatioState.Observing => Brushes.SteelBlue,
        _                  => Brushes.SlateGray,
    };

    private string _valueText = "—";
    public string ValueText { get => _valueText; private set => Set(ref _valueText, value); }

    private string _baselineText = "—";
    public string BaselineText { get => _baselineText; private set => Set(ref _baselineText, value); }

    private string _percentText = "—";
    public string PercentText { get => _percentText; private set => Set(ref _percentText, value); }

    private string _percentCaption = "% of Golden Run";
    /// <summary>Caption over the percent: marked "not judged" when the Golden Run path is off,
    /// because the number is still worth reading (it shows the drift) but is not why the entry
    /// is in any state.</summary>
    public string PercentCaption { get => _percentCaption; private set => Set(ref _percentCaption, value); }

    private string _stepText = "—";
    /// <summary>The step path's reading: the rise over the rolling baseline, "warming up N s",
    /// the rise marked "(frozen)" while the reference is held, or "—" when the path is off.</summary>
    public string StepText { get => _stepText; private set => Set(ref _stepText, value); }

    private string _detailText = "—";
    /// <summary>Tooltip detail: the raw signal / reference intensities and the thresholds.</summary>
    public string DetailText { get => _detailText; private set => Set(ref _detailText, value); }

    private string _slopeText = "—";
    public string SlopeText { get => _slopeText; private set => Set(ref _slopeText, value); }

    private string _snrText = "—";
    /// <summary>Worst-of signal/reference SNR, shown so a low-emission ratio is visibly weak
    /// rather than silently green.</summary>
    public string SnrText { get => _snrText; private set => Set(ref _snrText, value); }

    /// <summary>Applies one engine snapshot to the bindable fields.</summary>
    public void Apply(RatioSnapshot s)
    {
        bool sourceChanged = _enabledPaths != s.EnabledPaths || _raisedBy != s.RaisedBy;
        _enabledPaths = s.EnabledPaths;
        _raisedBy = s.RaisedBy;
        bool warming = s.EnabledPaths.HasFlag(JudgingPaths.Step) && s.StepWarmupSecondsRemaining > 0;
        sourceChanged |= _warmingUp != warming;
        _warmingUp = warming;
        State = s.State;
        if (sourceChanged) OnPropertyChanged(nameof(StateText));
        StepText = FormatStep(s);
        PercentCaption = s.EnabledPaths.HasFlag(JudgingPaths.GoldenRun)
            ? "% of Golden Run"
            : "% of GR (not judged)";
        ValueText = double.IsNaN(s.SmoothedRatio) ? "—" : s.SmoothedRatio.ToString("G4");
        BaselineText = s.HasBaseline
            ? $"{s.BaselineMean:G4} ± {s.BaselineSigma:G3}"
            : "—";
        PercentText = double.IsNaN(s.PercentOfBaseline)
            ? "—"
            : $"{s.PercentOfBaseline:F0} %";
        SnrText = FormatSnr(s);
        DetailText =
            (s.Mode == MonitorMode.AbsoluteIntensity
                ? $"intensity {Fmt(s.NumeratorIntensity)} (SNR {FmtSnr(s.NumeratorSnr)})  ·  " +
                  "plasma gate: logger save trigger"
                : $"signal {Fmt(s.NumeratorIntensity)} (SNR {FmtSnr(s.NumeratorSnr)})  ÷  " +
                  $"reference {Fmt(s.DenominatorIntensity)} (SNR {FmtSnr(s.DenominatorSnr)})") +
            "\n" +
            (s.HasBaseline
                ? $"warn {s.WarnThreshold:G4}  ·  alarm {s.AlarmThreshold:G4}" +
                  (s.EnabledPaths.HasFlag(JudgingPaths.GoldenRun) ? "" : "  (Golden Run not judged)")
                : s.EnabledPaths.HasFlag(JudgingPaths.Step)
                    ? "no Golden Run baseline — judged by step only"
                    : "no baseline — capture a Golden Run") +
            (s.EnabledPaths.HasFlag(JudgingPaths.Step) && !double.IsNaN(s.StepReference)
                ? $"\nrolling baseline {s.StepReference:G4}" + (s.StepReferenceFrozen ? " (frozen)" : "")
                : "") +
            (s.HasPedestal
                ? "\nRaw intensity (no baseline subtraction): % is σ-normalized (100 = baseline, " +
                  "+20 ≈ one warn-σ above), thresholds are σ-based, SNR not applicable."
                : "") +
            (s.State == RatioState.LowSignal
                ? "\nLow signal — emission near the noise floor; not trusted."
                : "");
        SlopeText = FormatSlope(s);
    }

    internal static string FormatStep(RatioSnapshot s)
    {
        if (!s.EnabledPaths.HasFlag(JudgingPaths.Step)) return "—";
        if (s.StepWarmupSecondsRemaining > 0) return $"warming up {Math.Ceiling(s.StepWarmupSecondsRemaining):F0} s";
        if (double.IsNaN(s.StepRise)) return "—";
        string rise = $"{s.StepRise * 100:+0.0;-0.0} %";
        return s.StepReferenceFrozen ? rise + " (frozen)" : rise;
    }

    private static string Fmt(double v) => double.IsNaN(v) ? "(not found)" : v.ToString("G4");

    private static string FmtSnr(double snr) => double.IsNaN(snr) ? "—" : snr.ToString("F1");

    /// <summary>
    /// The worse of the two line SNRs — that's what limits the ratio. In absolute-intensity mode
    /// only the monitored line is measured, so only its SNR is shown; folding in a reference that
    /// takes no part in the reading would report a healthy line as near-noise.
    /// </summary>
    private static string FormatSnr(RatioSnapshot s)
    {
        double worst = double.NaN;
        if (!double.IsNaN(s.NumeratorSnr)) worst = s.NumeratorSnr;
        if (s.Mode != MonitorMode.AbsoluteIntensity && !double.IsNaN(s.DenominatorSnr))
            worst = double.IsNaN(worst) ? s.DenominatorSnr : Math.Min(worst, s.DenominatorSnr);
        return double.IsNaN(worst) ? "—" : $"SNR {worst:F1}";
    }

    private static string FormatSlope(RatioSnapshot s)
    {
        if (!s.HasBaseline || s.BaselineMean <= 0 || double.IsNaN(s.SlopePerMinute))
            return "—";

        // A value carrying a continuum pedestal compresses %/min to nothing (and one near the
        // noise floor makes it jump), so report the trend in noise σ per minute instead — the
        // same σ-normalized frame that ratio's trend chart uses.
        if (s.HasPedestal)
        {
            double sigma = Math.Max(s.BaselineSigma, double.IsNaN(s.RatioNoiseSigma) ? 0 : s.RatioNoiseSigma);
            if (!(sigma > 0)) return "—";
            double sigmaPerMin = s.SlopePerMinute / sigma;
            if (Math.Abs(sigmaPerMin) < 0.05) return "→ stable";
            return $"{(sigmaPerMin > 0 ? "▲" : "▼")} {sigmaPerMin:+0.00;-0.00} σ/min";
        }

        double pctPerMin = s.SlopePerMinute / s.BaselineMean * 100.0;
        if (Math.Abs(pctPerMin) < 0.5) return "→ stable";
        string arrow = pctPerMin > 0 ? "▲" : "▼";
        return $"{arrow} {pctPerMin:+0.0;-0.0} %/min";
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
