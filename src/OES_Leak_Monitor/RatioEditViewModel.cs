using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace OES_Leak_Monitor;

/// <summary>
/// One entry of a Ratio Setup line picker: a catalog <see cref="SpectralLine"/> plus the
/// wavelength-drift <see cref="OffsetNm"/> currently configured for it on the Wavelength
/// Calibration tab, so the picker can show which lines are corrected and by how much.
/// The offset is display-only — it never round-trips into the saved <see cref="RatioDefinition"/>
/// (the engine applies it at monitor-build time; see <see cref="WavelengthCalibration"/>).
/// </summary>
public sealed class SpectralLineOption : INotifyPropertyChanged
{
    public SpectralLineOption(SpectralLine line) => Line = line;

    public SpectralLine Line { get; }
    public string Species => Line.Species;
    public double WavelengthNm => Line.WavelengthNm;

    private double _offsetNm;
    /// <summary>Additive drift correction for this line, nm; 0 when the line is uncorrected.</summary>
    public double OffsetNm
    {
        get => _offsetNm;
        set
        {
            if (_offsetNm == value) return;
            _offsetNm = value;
            OnPropertyChanged(nameof(OffsetNm));
            OnPropertyChanged(nameof(HasCorrection));
            OnPropertyChanged(nameof(CorrectionText));
            OnPropertyChanged(nameof(Display));
        }
    }

    /// <summary>Single-string label for pickers that show one line of text per entry (the
    /// Configuration tab's line picker); the Ratio Setup combos template the parts themselves.</summary>
    public string Display => HasCorrection
        ? $"{Species} {WavelengthNm:0.###} nm  ({CorrectionText})"
        : $"{Species} {WavelengthNm:0.###} nm";

    public bool HasCorrection => _offsetNm != 0.0;

    /// <summary>Offset and the resulting effective center, e.g. "+0.20 nm → 777.4 nm". Empty
    /// when the line is uncorrected.</summary>
    public string CorrectionText => HasCorrection
        ? $"{_offsetNm:+0.00;-0.00} nm → {WavelengthNm + _offsetNm:0.##} nm"
        : "";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Editable working copy of one <see cref="RatioDefinition"/> for the Ratio Setup tab.
/// The signal and reference lines are picked as <see cref="SpectralLineOption"/> entries from
/// the shared catalog list the owning <see cref="RatioSetupViewModel"/> hands in; the extraction
/// window is derived from the chosen <see cref="LineExtractMode"/> when <see cref="ToDefinition"/>
/// rebuilds the definition.
/// </summary>
public sealed class RatioEditViewModel : INotifyPropertyChanged
{
    // Working regions, one per side. They start as verbatim copies of the loaded regions and
    // change only when the operator changes that side's line, mode or windows — so Save writes
    // an untouched side back exactly as it was (#88). They used to be rebuilt on every Save
    // unless the picked catalog wavelength equalled the region's centre, which reset every
    // tuned region whose centre is not a catalog line (NO 235.6, OH 308.8), edited or not.
    private LineRegion _numRegion;
    private LineRegion _denRegion;

    private readonly IReadOnlyList<SpectralLineOption> _lines;

    public RatioEditViewModel(RatioDefinition def, IReadOnlyList<SpectralLineOption> lines)
    {
        _lines = lines;
        Key = string.IsNullOrWhiteSpace(def.Key) ? GenerateKey() : def.Key;
        _numRegion = def.Numerator.Clone();
        _denRegion = def.Denominator.Clone();

        _signalLine     = MatchLine(def.Numerator);
        _signalMode     = def.Numerator.Mode;
        _referenceLine  = MatchLine(def.Denominator);
        _referenceMode  = def.Denominator.Mode;

        _warnFactor     = def.WarnFactor;
        _alarmFactor    = def.AlarmFactor;
        _sigmaWarn      = def.SigmaWarn;
        _sigmaAlarm     = def.SigmaAlarm;
        _emaTauSeconds  = def.EmaTauSeconds;
        _confirmSeconds = def.ConfirmSeconds;
        _minSnr         = def.MinSnr;
        _monitorMode    = def.MonitorMode;
        _enabled        = def.Enabled;
        _processClass   = def.ProcessClass ?? "";
        _role           = def.Role;
        _goldenRunJudging = def.GoldenRunJudging;
        _stepJudging    = def.StepJudging;
        _stepWarnRise   = def.StepWarnRise;
        _stepAlarmRise  = def.StepAlarmRise;

        _autoName       = AutoName();
        _displayName    = string.IsNullOrWhiteSpace(def.DisplayName) ? _autoName : def.DisplayName;
    }

    /// <summary>Stable key — preserved from the loaded definition so a Golden Run baseline
    /// keeps pairing with it; freshly added ratios get a generated key.</summary>
    public string Key { get; }

    public IReadOnlyList<LineExtractMode> ModeOptions { get; } =
        new[] { LineExtractMode.PeakHeight, LineExtractMode.Integral, LineExtractMode.RawMean };

    public IReadOnlyList<RatioRole> RoleOptions { get; } =
        new[] { RatioRole.Alarm, RatioRole.TrendOnly, RatioRole.Guard };

    private RatioRole _role = RatioRole.Alarm;
    /// <summary>
    /// What this entry is for. Editable here for the same reason
    /// <see cref="ProcessClass"/> is: the tab rebuilds every field it saves, so a role set by
    /// hand in settings.json and not carried through here would silently turn a trend-only entry
    /// back into an alarming one — and the first thing anyone would know about it is an alarm
    /// nobody could act on.
    /// </summary>
    public RatioRole Role
    {
        get => _role;
        set { if (Set(ref _role, value)) OnPropertyChanged(nameof(RoleHint)); }
    }

    /// <summary>One line saying what the chosen role means, since the consequence is invisible
    /// until something either does or does not alarm.</summary>
    public string RoleHint => _role switch
    {
        RatioRole.TrendOnly => "Recorded and trended, never alarmed — for an indicator whose " +
                               "batch-to-batch reproducibility cannot carry a threshold.",
        RatioRole.Guard     => "A control quantity, never alarmed, printed beside every leak " +
                               "alarm so it can be read against it.",
        _                   => "Judged against its baseline; can raise the leak alarm.",
    };

    public IReadOnlyList<MonitorMode> MonitorModeOptions { get; } =
        new[] { MonitorMode.Ratio, MonitorMode.AbsoluteIntensity };

    private MonitorMode _monitorMode;
    /// <summary>Ratio (signal ÷ reference) vs absolute signal-line intensity (reference then only
    /// gates plasma-present). Switching re-derives the auto display name and the field labels.</summary>
    public MonitorMode MonitorMode
    {
        get => _monitorMode;
        set
        {
            if (!Set(ref _monitorMode, value)) return;
            RenameIfAuto();
            OnPropertyChanged(nameof(IsAbsolute));
            OnPropertyChanged(nameof(IsRatioMode));
            OnPropertyChanged(nameof(ValueHasPedestal));
            OnPropertyChanged(nameof(SignalLineHeader));
            OnPropertyChanged(nameof(ReferenceLineHeader));
        }
    }

    /// <summary>True in absolute-intensity mode — drives the field-role hints in the UI.</summary>
    public bool IsAbsolute => _monitorMode == MonitorMode.AbsoluteIntensity;

    /// <summary>True in ratio mode — the reference line picker only exists there. In absolute
    /// mode the reference is not divided in and no longer gates plasma-present (that comes from
    /// the logger's save trigger), so showing a picker would invite the operator to pick a line
    /// that silently decides nothing — or, worse, one that isn't there at all.</summary>
    public bool IsRatioMode => !IsAbsolute;

    /// <summary>Header for the first line picker — the monitored line in both modes.</summary>
    public string SignalLineHeader => IsAbsolute
        ? "Monitored line (absolute intensity)"
        : "Signal line (numerator)";

    /// <summary>Header for the second line picker — ratio mode only.</summary>
    public string ReferenceLineHeader => "Reference line (denominator / baseline species)";

    private string _displayName;
    public string DisplayName { get => _displayName; set => Set(ref _displayName, value?.Trim() ?? ""); }

    private bool _enabled;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    // --- signal (numerator) line ---------------------------------------------

    private SpectralLineOption _signalLine;
    public SpectralLineOption SignalLine
    {
        get => _signalLine;
        set
        {
            if (value is null) return;   // a picker clearing itself while its list is rebuilt
            bool newLine = !SameLine(_signalLine, value);
            if (!Set(ref _signalLine, value)) return;
            if (newLine) { _numRegion = BuildRegion(value.Line, _signalMode); RaiseSignalWindows(); }
            RenameIfAuto();
        }
    }

    private LineExtractMode _signalMode;
    public LineExtractMode SignalMode
    {
        get => _signalMode;
        set
        {
            if (!Set(ref _signalMode, value)) return;
            _numRegion = WithMode(_numRegion, value);
            RaiseSignalWindows();
            OnPropertyChanged(nameof(SignalIsRaw));
            OnPropertyChanged(nameof(ValueHasPedestal));
            OnPropertyChanged(nameof(StepAvailable));
        }
    }

    /// <summary>True when the monitored line is read raw — drives the hint about what changes.</summary>
    public bool SignalIsRaw => _signalMode == LineExtractMode.RawMean;

    /// <summary>
    /// Raw extraction, in either mode: the reading carries the continuum pedestal, so the
    /// Warn/Alarm <em>factors</em> stop being used (σ terms only), the trend becomes a σ-score,
    /// and SNR is not measurable. Shown in the editor so none of that is a surprise — dividing
    /// by a reference line scales the pedestal, it does not remove it. Mirrors
    /// <see cref="RatioDefinition.ValueHasPedestal"/>; the two must agree or the editor promises
    /// something the engine does not do.
    /// </summary>
    public bool ValueHasPedestal => SignalIsRaw;

    // --- reference (denominator) line ----------------------------------------

    private SpectralLineOption _referenceLine;
    public SpectralLineOption ReferenceLine
    {
        get => _referenceLine;
        set
        {
            if (value is null) return;
            bool newLine = !SameLine(_referenceLine, value);
            if (!Set(ref _referenceLine, value)) return;
            if (newLine) { _denRegion = BuildRegion(value.Line, _referenceMode); RaiseReferenceWindows(); }
            RenameIfAuto();
        }
    }

    private LineExtractMode _referenceMode;
    public LineExtractMode ReferenceMode
    {
        get => _referenceMode;
        set
        {
            if (!Set(ref _referenceMode, value)) return;
            _denRegion = WithMode(_denRegion, value);
            RaiseReferenceWindows();
        }
    }

    // --- line windows (#88) ----------------------------------------------------
    // The four extraction-window parameters of each side, edited in place on the working
    // region. Validation is at Save (WindowError), like the judging check, so a half-typed
    // value is not rejected mid-edit.

    public double SignalHalfWidthNm      { get => _numRegion.HalfWidthNm;           set { if (_numRegion.HalfWidthNm != value)           { _numRegion.HalfWidthNm = value;           OnPropertyChanged(); } } }
    public double SignalBaselineGapNm    { get => _numRegion.BaselineGapNm;         set { if (_numRegion.BaselineGapNm != value)         { _numRegion.BaselineGapNm = value;         OnPropertyChanged(); } } }
    public double SignalBaselineWidthNm  { get => _numRegion.BaselineWidthNm;       set { if (_numRegion.BaselineWidthNm != value)       { _numRegion.BaselineWidthNm = value;       OnPropertyChanged(); } } }
    public double SignalPeakSearchNm     { get => _numRegion.PeakSearchHalfWidthNm; set { if (_numRegion.PeakSearchHalfWidthNm != value) { _numRegion.PeakSearchHalfWidthNm = value; OnPropertyChanged(); } } }
    public double ReferenceHalfWidthNm     { get => _denRegion.HalfWidthNm;           set { if (_denRegion.HalfWidthNm != value)           { _denRegion.HalfWidthNm = value;           OnPropertyChanged(); } } }
    public double ReferenceBaselineGapNm   { get => _denRegion.BaselineGapNm;         set { if (_denRegion.BaselineGapNm != value)         { _denRegion.BaselineGapNm = value;         OnPropertyChanged(); } } }
    public double ReferenceBaselineWidthNm { get => _denRegion.BaselineWidthNm;       set { if (_denRegion.BaselineWidthNm != value)       { _denRegion.BaselineWidthNm = value;       OnPropertyChanged(); } } }
    public double ReferencePeakSearchNm    { get => _denRegion.PeakSearchHalfWidthNm; set { if (_denRegion.PeakSearchHalfWidthNm != value) { _denRegion.PeakSearchHalfWidthNm = value; OnPropertyChanged(); } } }

    // --- separate right side (#112) ---------------------------------------------
    // Unticked, the right window mirrors the left and the region stores null for it, so a
    // symmetric line keeps the WindowKey its Golden Run recorded. Ticking seeds the right
    // side from the left; unticking clears it back to null.

    public bool SignalSeparateRight { get => _numRegion.HasSeparateRightSide; set => SetSeparate(_numRegion, value, RaiseSignalWindows); }
    public bool ReferenceSeparateRight { get => _denRegion.HasSeparateRightSide; set => SetSeparate(_denRegion, value, RaiseReferenceWindows); }

    public double SignalRightGapNm      { get => _numRegion.EffectiveRightGapNm;   set { if (_numRegion.HasSeparateRightSide && _numRegion.RightBaselineGapNm != value)   { _numRegion.RightBaselineGapNm = value;   OnPropertyChanged(); } } }
    public double SignalRightWidthNm    { get => _numRegion.EffectiveRightWidthNm; set { if (_numRegion.HasSeparateRightSide && _numRegion.RightBaselineWidthNm != value) { _numRegion.RightBaselineWidthNm = value; OnPropertyChanged(); } } }
    public double ReferenceRightGapNm   { get => _denRegion.EffectiveRightGapNm;   set { if (_denRegion.HasSeparateRightSide && _denRegion.RightBaselineGapNm != value)   { _denRegion.RightBaselineGapNm = value;   OnPropertyChanged(); } } }
    public double ReferenceRightWidthNm { get => _denRegion.EffectiveRightWidthNm; set { if (_denRegion.HasSeparateRightSide && _denRegion.RightBaselineWidthNm != value) { _denRegion.RightBaselineWidthNm = value; OnPropertyChanged(); } } }

    private static void SetSeparate(LineRegion r, bool on, Action raise)
    {
        if (on == r.HasSeparateRightSide) return;
        if (on) { r.RightBaselineGapNm = r.BaselineGapNm; r.RightBaselineWidthNm = r.BaselineWidthNm; }
        else { r.RightBaselineGapNm = null; r.RightBaselineWidthNm = null; }
        raise();
    }

    /// <summary>The working regions as they stand, unsaved edits included — what the line
    /// preview draws.</summary>
    public LineRegion SignalRegionPreview => _numRegion.Clone();
    public LineRegion? ReferenceRegionPreview => IsRatioMode ? _denRegion.Clone() : null;

    /// <summary>A raw reading never peak-searches (see <see cref="LineRegion.PeakSearchHalfWidthNm"/>),
    /// so its search field is fixed at 0 and greyed.</summary>
    public bool SignalPeakSearchEditable    => _signalMode != LineExtractMode.RawMean;
    public bool ReferencePeakSearchEditable => _referenceMode != LineExtractMode.RawMean;

    /// <summary>Upper bound on every window parameter, nm — wide enough for a molecular band
    /// (OH needed a 6.7 nm gap on the lab machine), narrow enough to catch a typo.</summary>
    public const double MaxWindowNm = 20.0;

    /// <summary>Why this row's windows cannot be saved, or null when they can. Names the side
    /// and the field, since Save shows only the first error.</summary>
    public string? WindowError =>
        RegionError("signal", _numRegion) ?? (IsRatioMode ? RegionError("reference", _denRegion) : null);

    private static string? RegionError(string side, LineRegion r)
    {
        static bool Bad(double v) => double.IsNaN(v) || v < 0 || v > MaxWindowNm;
        if (Bad(r.HalfWidthNm) || (r.Mode != LineExtractMode.RawMean && r.HalfWidthNm == 0))
            return $"{side} half-width must be {(r.Mode == LineExtractMode.RawMean ? "0" : "above 0")}–{MaxWindowNm:0} nm.";
        if (Bad(r.BaselineGapNm)) return $"{side} baseline gap must be 0–{MaxWindowNm:0} nm.";
        if (Bad(r.BaselineWidthNm) || r.BaselineWidthNm == 0) return $"{side} baseline width must be above 0 and at most {MaxWindowNm:0} nm.";
        if (r.HasSeparateRightSide)
        {
            if (Bad(r.EffectiveRightGapNm)) return $"{side} right gap must be 0–{MaxWindowNm:0} nm.";
            if (Bad(r.EffectiveRightWidthNm) || r.EffectiveRightWidthNm == 0) return $"{side} right width must be above 0 and at most {MaxWindowNm:0} nm.";
        }
        if (Bad(r.PeakSearchHalfWidthNm)) return $"{side} peak search must be 0–{MaxWindowNm:0} nm.";
        return null;
    }

    private void RaiseSignalWindows()
    {
        OnPropertyChanged(nameof(SignalHalfWidthNm));
        OnPropertyChanged(nameof(SignalBaselineGapNm));
        OnPropertyChanged(nameof(SignalBaselineWidthNm));
        OnPropertyChanged(nameof(SignalPeakSearchNm));
        OnPropertyChanged(nameof(SignalPeakSearchEditable));
        OnPropertyChanged(nameof(SignalSeparateRight));
        OnPropertyChanged(nameof(SignalRightGapNm));
        OnPropertyChanged(nameof(SignalRightWidthNm));
    }

    private void RaiseReferenceWindows()
    {
        OnPropertyChanged(nameof(ReferenceHalfWidthNm));
        OnPropertyChanged(nameof(ReferenceBaselineGapNm));
        OnPropertyChanged(nameof(ReferenceBaselineWidthNm));
        OnPropertyChanged(nameof(ReferencePeakSearchNm));
        OnPropertyChanged(nameof(ReferencePeakSearchEditable));
        OnPropertyChanged(nameof(ReferenceSeparateRight));
        OnPropertyChanged(nameof(ReferenceRightGapNm));
        OnPropertyChanged(nameof(ReferenceRightWidthNm));
    }

    private static bool SameLine(SpectralLineOption? a, SpectralLineOption b) =>
        a is not null && a.Species == b.Species && Math.Abs(a.WavelengthNm - b.WavelengthNm) < 1e-9;

    /// <summary>A mode change keeps where the windows are — a tuned baseline position does not
    /// depend on how the signal window is read — and sets only what the mode dictates: the
    /// peak search (none for a raw reading; the factory 1 nm when leaving raw).</summary>
    private static LineRegion WithMode(LineRegion r, LineExtractMode mode)
    {
        var c = r.Clone();
        bool wasRaw = c.Mode == LineExtractMode.RawMean;
        c.Mode = mode;
        if (mode == LineExtractMode.RawMean) c.PeakSearchHalfWidthNm = 0;
        else if (wasRaw) c.PeakSearchHalfWidthNm = 1.0;
        return c;
    }

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
    /// "Low Signal" and is held out of the alarm. 0 disables the gate. A negative SNR is
    /// meaningless, so it is clamped to 0 (gate off).</summary>
    public double MinSnr
    {
        get => _minSnr;
        set
        {
            double clamped = value < 0 || double.IsNaN(value) ? 0.0 : value;
            // Notify even when the stored value is unchanged, so a rejected negative entry
            // (e.g. typing "-3" while already 0) snaps the bound TextBox back to 0.
            if (!Set(ref _minSnr, clamped) && clamped != value)
                OnPropertyChanged();
        }
    }

    // --- auto display name ---------------------------------------------------

    private string _autoName;
    private void RenameIfAuto()
    {
        var next = AutoName();
        if (_displayName == _autoName) DisplayName = next;
        _autoName = next;
    }

    private string AutoName() => IsAbsolute
        ? $"{_signalLine.Species} {_signalLine.WavelengthNm:0.#} (abs)"
        : $"{_signalLine.Species} {_signalLine.WavelengthNm:0.#} / " +
          $"{_referenceLine.Species} {_referenceLine.WavelengthNm:0.#}";

    private string _processClass = "";
    /// <summary>
    /// Process class this ratio measures (<see cref="RatioDefinition.ProcessClass"/>). Empty
    /// means every step, which is what an unconfigured site has.
    ///
    /// <para>Editable here so that saving the Ratio Setup tab cannot silently drop it. The
    /// value is set by hand in <c>settings.json</c> today; without this it would round-trip to
    /// empty on the first Save, un-scoping every ratio at once with nothing on screen to say
    /// so — the panel would look untouched and the leak monitor would start judging all three
    /// processes against one baseline again.</para>
    /// </summary>
    public string ProcessClass
    {
        get => _processClass;
        set => Set(ref _processClass, (value ?? "").Trim());
    }

    private bool _goldenRunJudging = true, _stepJudging;
    private double _stepWarnRise = RatioDefinition.DefaultStepWarnRise, _stepAlarmRise = RatioDefinition.DefaultStepAlarmRise;

    /// <summary>Judge against the Golden Run baseline (<see cref="RatioDefinition.GoldenRunJudging"/>).
    /// Carried through every save, so a save cannot reset it — the trap of #88.</summary>
    public bool GoldenRunJudging
    {
        get => _goldenRunJudging;
        set { if (Set(ref _goldenRunJudging, value)) OnPropertyChanged(nameof(GoldenRunFieldsEnabled)); }
    }

    /// <summary>Judge against the rolling baseline (<see cref="RatioDefinition.StepJudging"/>).</summary>
    public bool StepJudging
    {
        get => _stepJudging;
        set { if (Set(ref _stepJudging, value)) OnPropertyChanged(nameof(GoldenRunFieldsEnabled)); }
    }

    /// <summary>Step-path Warn rise, as a fraction (<see cref="RatioDefinition.StepWarnRise"/>).</summary>
    public double StepWarnRise
    {
        get => _stepWarnRise;
        set { if (Set(ref _stepWarnRise, value)) OnPropertyChanged(nameof(StepWarnRisePercent)); }
    }

    /// <summary>Step-path Alarm rise, as a fraction (<see cref="RatioDefinition.StepAlarmRise"/>).</summary>
    public double StepAlarmRise
    {
        get => _stepAlarmRise;
        set { if (Set(ref _stepAlarmRise, value)) OnPropertyChanged(nameof(StepAlarmRisePercent)); }
    }

    /// <summary>The Warn rise as the editor shows it, in percent (10 = +10 %).</summary>
    public double StepWarnRisePercent
    {
        get => Math.Round(_stepWarnRise * 100, 6);
        set => StepWarnRise = value / 100.0;
    }

    /// <summary>The Alarm rise as the editor shows it, in percent.</summary>
    public double StepAlarmRisePercent
    {
        get => Math.Round(_stepAlarmRise * 100, 6);
        set => StepAlarmRise = value / 100.0;
    }

    /// <summary>
    /// Step judging can be turned on: the reading carries no pedestal. A rise measured as a
    /// fraction of a raw reading is a fraction of the continuum (<see cref="RatioDefinition.StepJudgingActive"/>).
    /// </summary>
    public bool StepAvailable => !ValueHasPedestal;

    /// <summary>The Golden Run factor and σ fields are editable unless step judging is the only
    /// path — then they keep their values, greyed, ready for the path being turned back on.</summary>
    public bool GoldenRunFieldsEnabled => _goldenRunJudging || !_stepJudging;

    /// <summary>
    /// Why this entry cannot be saved as it stands, or null. An alarming entry must be judged
    /// some way (one that is only meant to be recorded has a role for that), and the step
    /// rises must make an Alarm at least as hard to reach as a Warning.
    /// </summary>
    public string? JudgingError
    {
        get
        {
            if (Role != RatioRole.Alarm) return null;
            bool stepRuns = _stepJudging && StepAvailable;
            if (!_goldenRunJudging && !stepRuns)
                return StepAvailable
                    ? "choose at least one way of judging it (Golden Run baseline or rolling baseline). " +
                      "To record a ratio without judging it, set its role to TrendOnly."
                    : "its reading is raw (it carries the continuum pedestal), so it can only be judged " +
                      "against the Golden Run baseline — turn that on, or set its role to TrendOnly.";
            if (stepRuns)
            {
                if (!(_stepWarnRise > 0)) return "the step Warn rise must be above 0 %.";
                if (!(_stepAlarmRise >= _stepWarnRise)) return "the step Alarm rise must be at least the Warn rise.";
            }
            return null;
        }
    }

    /// <summary>Builds a persistable definition from the current edits.</summary>
    public RatioDefinition ToDefinition() => new()
    {
        Key = Key,
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? AutoName() : DisplayName,
        Enabled = Enabled,
        ProcessClass = ProcessClass,
        Role = Role,
        MonitorMode = MonitorMode,
        Numerator = _numRegion.Clone(),
        Denominator = _denRegion.Clone(),
        WarnFactor = WarnFactor,
        AlarmFactor = AlarmFactor,
        SigmaWarn = SigmaWarn,
        SigmaAlarm = SigmaAlarm,
        EmaTauSeconds = EmaTauSeconds,
        ConfirmSeconds = ConfirmSeconds,
        MinSnr = MinSnr,
        GoldenRunJudging = GoldenRunJudging,
        StepJudging = StepJudging,
        StepWarnRise = StepWarnRise,
        StepAlarmRise = StepAlarmRise,
    };

    /// <summary>True if the signal and reference are the same line (a meaningless ratio).
    /// Not a problem in absolute mode, where the reference takes no part in the measurement.</summary>
    public bool SignalEqualsReference =>
        IsRatioMode &&
        _signalLine.Species == _referenceLine.Species &&
        Math.Abs(_signalLine.WavelengthNm - _referenceLine.WavelengthNm) < 1e-6;

    // A peak-height line is a narrow atomic transition; an integral line is a broader
    // molecular band head, so it gets a wider signal window. A raw reading averages a narrow
    // window (a few pixels — enough to damp pixel noise without pulling in a neighbour) and
    // pins the window: peak-searching a position with no peak is what it exists to avoid.
    private static LineRegion BuildRegion(SpectralLine line, LineExtractMode mode) => new()
    {
        Label = $"{line.Species} {line.WavelengthNm:0.###}",
        CenterNm = line.WavelengthNm,
        Mode = mode,
        HalfWidthNm = mode == LineExtractMode.Integral ? 1.0 : 0.5,
        BaselineGapNm = 1.0,
        BaselineWidthNm = 1.0,
        PeakSearchHalfWidthNm = mode == LineExtractMode.RawMean ? 0.0 : 1.0,
    };

    // The region's label carries the species it was built from, so prefer the nearest line of
    // that species: several species share a wavelength (Ar and N2 both list 750.4 nm), and a
    // wavelength-only match would silently re-label the region — and pick up the wrong line's
    // wavelength correction. Regions without a resolvable species fall back to the global nearest.
    private SpectralLineOption MatchLine(LineRegion region)
    {
        string species = WavelengthCalibration.SpeciesOf(region.Label);
        return Nearest(_lines.Where(o => o.Species == species), region.CenterNm)
            ?? Nearest(_lines, region.CenterNm)!;
    }

    private static SpectralLineOption? Nearest(IEnumerable<SpectralLineOption> lines, double wl)
    {
        SpectralLineOption? best = null;
        double bestD = double.MaxValue;
        foreach (var l in lines)
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
