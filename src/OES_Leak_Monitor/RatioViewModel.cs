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

    public string StateText => _state switch
    {
        RatioState.Normal     => "Normal",
        RatioState.Warning    => "WARNING",
        RatioState.Alarm      => "ALARM",
        RatioState.NoPlasma   => "No Plasma",
        RatioState.LowSignal  => "Low Signal",
        RatioState.NoBaseline => "No Baseline",
        RatioState.Disabled   => "Disabled",
        _                     => _state.ToString(),
    };

    public Brush StateBrush => _state switch
    {
        RatioState.Normal  => Brushes.ForestGreen,
        RatioState.Warning => Brushes.DarkOrange,
        RatioState.Alarm   => Brushes.Firebrick,
        RatioState.NoPlasma => Brushes.Gray,
        RatioState.LowSignal => Brushes.SlateBlue,
        RatioState.Disabled => Brushes.Silver,
        _                  => Brushes.SlateGray,
    };

    private string _valueText = "—";
    public string ValueText { get => _valueText; private set => Set(ref _valueText, value); }

    private string _baselineText = "—";
    public string BaselineText { get => _baselineText; private set => Set(ref _baselineText, value); }

    private string _percentText = "—";
    public string PercentText { get => _percentText; private set => Set(ref _percentText, value); }

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
        State = s.State;
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
                  $"plasma-ref {Fmt(s.DenominatorIntensity)} (SNR {FmtSnr(s.DenominatorSnr)}, gate only)"
                : $"signal {Fmt(s.NumeratorIntensity)} (SNR {FmtSnr(s.NumeratorSnr)})  ÷  " +
                  $"reference {Fmt(s.DenominatorIntensity)} (SNR {FmtSnr(s.DenominatorSnr)})") +
            "\n" +
            (s.HasBaseline
                ? $"warn {s.WarnThreshold:G4}  ·  alarm {s.AlarmThreshold:G4}"
                : "no baseline — capture a Golden Run") +
            (s.State == RatioState.LowSignal
                ? "\nLow signal — emission near the noise floor; not trusted."
                : "");
        SlopeText = FormatSlope(s);
    }

    private static string Fmt(double v) => double.IsNaN(v) ? "(not found)" : v.ToString("G4");

    private static string FmtSnr(double snr) => double.IsNaN(snr) ? "—" : snr.ToString("F1");

    /// <summary>The worse of the two line SNRs — that's what limits the ratio.</summary>
    private static string FormatSnr(RatioSnapshot s)
    {
        double worst = double.NaN;
        if (!double.IsNaN(s.NumeratorSnr)) worst = s.NumeratorSnr;
        if (!double.IsNaN(s.DenominatorSnr))
            worst = double.IsNaN(worst) ? s.DenominatorSnr : Math.Min(worst, s.DenominatorSnr);
        return double.IsNaN(worst) ? "—" : $"SNR {worst:F1}";
    }

    private static string FormatSlope(RatioSnapshot s)
    {
        if (!s.HasBaseline || s.BaselineMean <= 0 || double.IsNaN(s.SlopePerMinute))
            return "—";
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
