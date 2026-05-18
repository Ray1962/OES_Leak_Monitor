using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace OES_Leak_Monitor;

/// <summary>Bindable row for one monitored ratio in the Leak Monitor panel.</summary>
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
        RatioState.NoBaseline => "No Baseline",
        _                     => _state.ToString(),
    };

    public Brush StateBrush => _state switch
    {
        RatioState.Normal  => Brushes.ForestGreen,
        RatioState.Warning => Brushes.DarkOrange,
        RatioState.Alarm   => Brushes.Firebrick,
        RatioState.NoPlasma => Brushes.Gray,
        _                  => Brushes.SlateGray,
    };

    private string _valueText = "—";
    public string ValueText { get => _valueText; private set => Set(ref _valueText, value); }

    private string _baselineText = "—";
    public string BaselineText { get => _baselineText; private set => Set(ref _baselineText, value); }

    private string _percentText = "—";
    public string PercentText { get => _percentText; private set => Set(ref _percentText, value); }

    private string _thresholdText = "—";
    public string ThresholdText { get => _thresholdText; private set => Set(ref _thresholdText, value); }

    private string _slopeText = "—";
    public string SlopeText { get => _slopeText; private set => Set(ref _slopeText, value); }

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
        ThresholdText = s.HasBaseline
            ? $"warn {s.WarnThreshold:G4}  ·  alarm {s.AlarmThreshold:G4}"
            : "—";
        SlopeText = FormatSlope(s);
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
