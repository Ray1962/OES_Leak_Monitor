using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OES_Leak_Monitor;

/// <summary>
/// Editable working copy of one <see cref="RatioDefinition"/> for the Ratio Setup tab.
/// The signal and reference lines are picked as <see cref="SpectralLine"/> entries from
/// <see cref="SpectralLineCatalog"/>; the extraction window is derived from the chosen
/// <see cref="LineExtractMode"/> when <see cref="ToDefinition"/> rebuilds the definition.
/// </summary>
public sealed class RatioEditViewModel : INotifyPropertyChanged
{
    // Original regions, kept so an untouched line round-trips with its exact label and
    // window tuning — editing one ratio then never invalidates another ratio's baseline.
    private readonly LineRegion _origNumerator;
    private readonly LineRegion _origDenominator;

    public RatioEditViewModel(RatioDefinition def)
    {
        Key = string.IsNullOrWhiteSpace(def.Key) ? GenerateKey() : def.Key;
        _origNumerator   = def.Numerator.Clone();
        _origDenominator = def.Denominator.Clone();

        _signalLine     = NearestLine(def.Numerator.CenterNm);
        _signalMode     = def.Numerator.Mode;
        _referenceLine  = NearestLine(def.Denominator.CenterNm);
        _referenceMode  = def.Denominator.Mode;

        _warnFactor     = def.WarnFactor;
        _alarmFactor    = def.AlarmFactor;
        _sigmaWarn      = def.SigmaWarn;
        _sigmaAlarm     = def.SigmaAlarm;
        _emaTauSeconds  = def.EmaTauSeconds;
        _confirmSeconds = def.ConfirmSeconds;
        _minSnr         = def.MinSnr;
        _enabled        = def.Enabled;

        _autoName       = AutoName();
        _displayName    = string.IsNullOrWhiteSpace(def.DisplayName) ? _autoName : def.DisplayName;
    }

    /// <summary>Stable key — preserved from the loaded definition so a Golden Run baseline
    /// keeps pairing with it; freshly added ratios get a generated key.</summary>
    public string Key { get; }

    public IReadOnlyList<LineExtractMode> ModeOptions { get; } =
        new[] { LineExtractMode.PeakHeight, LineExtractMode.Integral };

    private string _displayName;
    public string DisplayName { get => _displayName; set => Set(ref _displayName, value?.Trim() ?? ""); }

    private bool _enabled;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    // --- signal (numerator) line ---------------------------------------------

    private SpectralLine _signalLine;
    public SpectralLine SignalLine
    {
        get => _signalLine;
        set { if (Set(ref _signalLine, value)) RenameIfAuto(); }
    }

    private LineExtractMode _signalMode;
    public LineExtractMode SignalMode { get => _signalMode; set => Set(ref _signalMode, value); }

    // --- reference (denominator) line ----------------------------------------

    private SpectralLine _referenceLine;
    public SpectralLine ReferenceLine
    {
        get => _referenceLine;
        set { if (Set(ref _referenceLine, value)) RenameIfAuto(); }
    }

    private LineExtractMode _referenceMode;
    public LineExtractMode ReferenceMode { get => _referenceMode; set => Set(ref _referenceMode, value); }

    // --- thresholds ----------------------------------------------------------

    private double _warnFactor;
    public double WarnFactor { get => _warnFactor; set => Set(ref _warnFactor, value); }

    private double _alarmFactor;
    public double AlarmFactor { get => _alarmFactor; set => Set(ref _alarmFactor, value); }

    private double _sigmaWarn;
    public double SigmaWarn { get => _sigmaWarn; set => Set(ref _sigmaWarn, value); }

    private double _sigmaAlarm;
    public double SigmaAlarm { get => _sigmaAlarm; set => Set(ref _sigmaAlarm, value); }

    private double _emaTauSeconds;
    public double EmaTauSeconds { get => _emaTauSeconds; set => Set(ref _emaTauSeconds, value); }

    private double _confirmSeconds;
    public double ConfirmSeconds { get => _confirmSeconds; set => Set(ref _confirmSeconds, value); }

    private double _minSnr;
    /// <summary>Minimum line SNR before the ratio is trusted; below it the ratio reads
    /// "Low Signal" and is held out of the alarm. 0 disables the gate.</summary>
    public double MinSnr { get => _minSnr; set => Set(ref _minSnr, value); }

    // --- auto display name ---------------------------------------------------

    private string _autoName;
    private void RenameIfAuto()
    {
        var next = AutoName();
        if (_displayName == _autoName) DisplayName = next;
        _autoName = next;
    }

    private string AutoName() =>
        $"{_signalLine.Species} {_signalLine.WavelengthNm:0.#} / " +
        $"{_referenceLine.Species} {_referenceLine.WavelengthNm:0.#}";

    /// <summary>Builds a persistable definition from the current edits.</summary>
    public RatioDefinition ToDefinition() => new()
    {
        Key = Key,
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? AutoName() : DisplayName,
        Enabled = Enabled,
        Numerator = RegionFor(SignalLine, SignalMode, _origNumerator),
        Denominator = RegionFor(ReferenceLine, ReferenceMode, _origDenominator),
        WarnFactor = WarnFactor,
        AlarmFactor = AlarmFactor,
        SigmaWarn = SigmaWarn,
        SigmaAlarm = SigmaAlarm,
        EmaTauSeconds = EmaTauSeconds,
        ConfirmSeconds = ConfirmSeconds,
        MinSnr = MinSnr,
    };

    /// <summary>True if the signal and reference are the same line (a meaningless ratio).</summary>
    public bool SignalEqualsReference =>
        _signalLine.Species == _referenceLine.Species &&
        Math.Abs(_signalLine.WavelengthNm - _referenceLine.WavelengthNm) < 1e-6;

    // Reuses the original region (exact label + tuned windows preserved) when the line and
    // mode are unchanged; otherwise builds a fresh region for the newly picked line.
    private static LineRegion RegionFor(SpectralLine line, LineExtractMode mode, LineRegion orig)
    {
        bool unchanged = !string.IsNullOrEmpty(orig.Label)
                       && orig.Mode == mode
                       && Math.Abs(orig.CenterNm - line.WavelengthNm) < 1e-6;
        return unchanged ? orig.Clone() : BuildRegion(line, mode);
    }

    // A peak-height line is a narrow atomic transition; an integral line is a broader
    // molecular band head, so it gets a wider signal window.
    private static LineRegion BuildRegion(SpectralLine line, LineExtractMode mode) => new()
    {
        Label = $"{line.Species} {line.WavelengthNm:0.###}",
        CenterNm = line.WavelengthNm,
        Mode = mode,
        HalfWidthNm = mode == LineExtractMode.Integral ? 1.0 : 0.5,
        BaselineGapNm = 1.0,
        BaselineWidthNm = 1.0,
        PeakSearchHalfWidthNm = 1.0,
    };

    private static SpectralLine NearestLine(double wl)
    {
        var all = SpectralLineCatalog.All;
        var best = all[0];
        double bestD = Math.Abs(best.WavelengthNm - wl);
        foreach (var l in all)
        {
            double d = Math.Abs(l.WavelengthNm - wl);
            if (d < bestD) { bestD = d; best = l; }
        }
        return best;
    }

    private static string GenerateKey() => "R_" + Guid.NewGuid().ToString("N").Substring(0, 8);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(n);
        return true;
    }
}
