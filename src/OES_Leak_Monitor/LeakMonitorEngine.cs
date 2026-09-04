using System;
using System.Collections.Generic;
using System.Linq;
using Aqst.OesSpectrometer.Models;

namespace OES_Leak_Monitor;

/// <summary>
/// Overall leak-monitor status, the worst of all monitored ratios.
/// <para>
/// ⚠️ <b>The declaration order is a wire format.</b> This is reported to a SECS host as VID 007
/// (specification §1.4(c)-1: 0 Idle, 1 Normal, 2 Warning, 3 Alarm). Inserting a member in the
/// middle silently changes what a host reads. Append only — see
/// <c>docs/secs-integration.md</c> §5.1.
/// </para>
/// </summary>
public enum LeakAlarmLevel
{
    /// <summary>Nothing to evaluate — plasma off, no baseline, or monitor disabled.</summary>
    Idle,
    Normal,
    Warning,
    Alarm,
}

/// <summary>
/// Validity of the selected leak-rate calibration for the current conditions.
/// <para>
/// ⚠️ <b>The declaration order is a wire format.</b> This is reported to a SECS host as VID 006
/// (specification §1.4(c)-2: 0 not calibrated, 1 active, 2 baseline mismatch). Append only —
/// see <c>docs/secs-integration.md</c> §5.1.
/// </para>
/// </summary>
public enum CalibrationStatus
{
    /// <summary>No calibration is selected — leak-rate estimation is off.</summary>
    NotCalibrated,
    /// <summary>The selected calibration applies and is producing estimates.</summary>
    Active,
    /// <summary>A calibration is selected but the active Golden Run baseline is not the one it
    /// was captured against — estimation is suspended (the rises are measured against a
    /// different baseline). Select the matching baseline, or re-calibrate.</summary>
    BaselineMismatch,
}

/// <summary>Why one ratio ended a Golden Run capture without a usable baseline.</summary>
public sealed class GoldenRunRatioRejection
{
    public string Key { get; init; } = "";
    public string DisplayName { get; init; } = "";

    /// <summary>Operator-facing reason, the same sentence written to the system log.</summary>
    public string Reason { get; init; } = "";
}

/// <summary>
/// Outcome of a Golden Run capture. A capture that produced no usable baseline at all is
/// <see cref="Accepted"/> = false and was <em>discarded</em>: it is not stored, does not become
/// the active baseline, and does not replace a same-named run — so a failed capture can never
/// silently take a working baseline away. A partially successful capture is accepted (the
/// baselines it did get are usable), with the ratios that got none listed in
/// <see cref="Rejected"/> so the host can say so instead of reporting plain success.
/// </summary>
public sealed class GoldenRunCaptureResult
{
    public GoldenRun Run { get; init; } = new();

    /// <summary>False when the run was not stored — either discarded for having no usable ratio
    /// baseline, or held pending the operator's answer (see <see cref="NeedsConfirmation"/>).</summary>
    public bool Accepted { get; init; }

    /// <summary>
    /// True when the capture is finished and usable but would destroy a stored run of the same
    /// name that has baselines this one lacks. Nothing has been written: the host asks, then
    /// calls <see cref="LeakMonitorEngine.ConfirmCapturedRun"/> either way.
    /// </summary>
    public bool NeedsConfirmation { get; init; }

    /// <summary>Ratios that got no baseline from this capture, with the reason for each.</summary>
    public IReadOnlyList<GoldenRunRatioRejection> Rejected { get; init; } =
        Array.Empty<GoldenRunRatioRejection>();

    /// <summary>Ratios that would lose a baseline they have in <see cref="Replaced"/>. Only
    /// populated with <see cref="NeedsConfirmation"/>.</summary>
    public IReadOnlyList<GoldenRunRatioRejection> Lost { get; init; } =
        Array.Empty<GoldenRunRatioRejection>();

    /// <summary>The stored run of the same name this capture would replace, or null.</summary>
    public GoldenRun? Replaced { get; init; }

    /// <summary>Name of the baseline left active — unchanged when the capture was discarded.</summary>
    public string? ActiveGoldenRun { get; init; }

    public int BaselineCount => Run.Baselines.Count;
}

/// <summary>Immutable per-frame view of the whole monitor, handed to the UI.</summary>
public sealed class LeakMonitorSnapshot
{
    public DateTime Timestamp { get; init; }
    public LeakAlarmLevel Overall { get; init; }
    public IReadOnlyList<RatioSnapshot> Ratios { get; init; } = Array.Empty<RatioSnapshot>();
    public bool TestMode { get; init; }
    public bool CaptureActive { get; init; }
    public double CaptureProgress01 { get; init; }
    public string? ActiveGoldenRun { get; init; }

    /// <summary>A leak-rate calibration point is being averaged.</summary>
    public bool CalibrationCaptureActive { get; init; }
    public double CalibrationCaptureProgress01 { get; init; }
    /// <summary>Known leak rate of the calibration point currently being captured, mbar·L/s.</summary>
    public double CalibrationLeakRate { get; init; }

    /// <summary>Quantitative leak-rate estimate from the active calibration, or null when no
    /// calibration is active. <see cref="LeakRateEstimate.HasEstimate"/> is false when a
    /// calibration exists but no ratio currently yields a usable reading.</summary>
    public LeakRateEstimate? LeakRate { get; init; }

    /// <summary>Name of the selected leak-rate calibration, or null. Set even when the
    /// calibration is currently invalid — see <see cref="CalibrationStatus"/>.</summary>
    public string? ActiveCalibration { get; init; }

    /// <summary>Whether the selected calibration is valid for the current baseline.</summary>
    public CalibrationStatus CalibrationStatus { get; init; }

    /// <summary>
    /// Non-empty when the live acquisition parameters differ from the ones the active Golden Run
    /// was captured under — absolute-intensity readings scale with those, so the baseline no
    /// longer applies. Empty when they agree, or when the run predates the recording of them.
    /// </summary>
    public string AcquisitionWarning { get; init; } = "";

    /// <summary>
    /// Whether the plasma-present gate was open on this frame — the frame counted towards
    /// the monitors rather than being held out as "plasma off". False when the gate could
    /// not be evaluated at all; read it with <see cref="PlasmaGateAvailable"/>, since
    /// "we can't tell" is not "plasma off".
    /// <para/>
    /// Whole-frame counterpart of <see cref="RatioSnapshot.PlasmaPresent"/>, which is
    /// per ratio because a ratio-mode entry additionally needs its own reference line.
    /// </summary>
    public bool PlasmaPresent { get; init; }

    /// <summary>Whether the gate could be evaluated (a usable trigger, measurable on this frame).</summary>
    public bool PlasmaGateAvailable { get; init; }

    /// <summary>
    /// Process class of the plasma step this frame belongs to: a configured class name,
    /// <see cref="ProcessClassifier.Unknown"/> for a step no rule matched, or "" when no
    /// classifier is configured or the step's verdict has not been taken yet.
    /// </summary>
    public string ProcessClass { get; init; } = "";

    /// <summary>
    /// Which of the three reasons <see cref="ProcessClass"/> is empty, when it is — see
    /// <see cref="ProcessClassState"/>. Reported to the host as VID 028, because a name that is
    /// blank for "no classifier", "no plasma" and "not decided yet" alike is not an answer.
    /// </summary>
    public ProcessClassState ProcessClassState { get; init; } = ProcessClassState.NotConfigured;

    /// <summary>
    /// Counts the plasma steps seen this acquisition, so a row can say which one it belongs to.
    /// Populated whether or not a classifier is configured — a step is a fact about the tool,
    /// and the batch layer needs its boundaries either way. 0 before the first step.
    /// </summary>
    public int ProcessStepIndex { get; init; }

    /// <summary>
    /// Each classifier rule's measured value on the frame the step's verdict was taken —
    /// recorded rather than only the verdict, because a step landing near a threshold is only
    /// diagnosable from the number. Empty while no classifier is configured or before the
    /// verdict is taken.
    /// </summary>
    public IReadOnlyList<ProcessDiscriminant> ProcessDiscriminants { get; init; } =
        Array.Empty<ProcessDiscriminant>();

    /// <summary>
    /// Isolated gate dropouts counted this acquisition — single blank frames between good
    /// ones. The gate discards them silently, so this is the number that says whether an
    /// acquisition-mode change actually helped.
    /// </summary>
    public int DropoutCount { get; init; }
}

public sealed class LeakAlarmEventArgs : EventArgs
{
    public LeakAlarmLevel OldLevel { get; init; }
    public LeakAlarmLevel NewLevel { get; init; }
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// The <see cref="RatioRole.Guard"/> entries as they read on the frame that caused this
    /// transition, so whoever reads the alarm can read the control quantity beside it.
    ///
    /// <para>Carried rather than looked up afterwards because the answer changes frame by frame
    /// and the question — "is this a leak, or did the process gas move?" — is asked about the
    /// moment the alarm fired. Reported, never used to suppress: a guard's own scatter is a few
    /// per cent, and one given a veto will eventually veto a real leak.</para>
    /// </summary>
    public IReadOnlyList<RatioSnapshot> Guards { get; init; } = Array.Empty<RatioSnapshot>();
}

/// <summary>An operator ending a confirmed leak alarm — who, what was latched, and the
/// composite level either side of it.</summary>
public sealed class LeakAcknowledgedEventArgs : EventArgs
{
    /// <summary>Signed-in operator, or "" when the caller did not supply one.</summary>
    public string User { get; init; } = "";

    /// <summary>Display names of the ratios whose latch was cleared. Never empty — the event
    /// does not fire when there was nothing to clear.</summary>
    public IReadOnlyList<string> ClearedRatios { get; init; } = Array.Empty<string>();

    public LeakAlarmLevel OldLevel { get; init; }
    public LeakAlarmLevel NewLevel { get; init; }
    public DateTime Timestamp { get; init; }
}

/// <summary>
/// Core actinometry leak-monitoring engine. Consumes spectrum frames off the acquisition
/// thread, extracts each ratio's line intensities, runs the per-ratio state machines, and
/// computes a composite alarm level. Also drives Golden Run baseline capture.
///
/// <para>Mirrors <c>DualIntensityLogger</c>: thread-safe ingest, events bridged to the UI
/// and the system log by the host view-model.</para>
/// </summary>
public sealed class LeakMonitorEngine : IDisposable
{
    // A Golden Run ratio baseline is rejected unless at least this fraction of its
    // SNR-evaluable frames cleared the per-ratio SNR floor — guards against a baseline built
    // from a biased sliver of upward noise excursions when the line hovers around noise.
    private const double MinBaselineAcceptFraction = 0.5;

    // A baseline mean must stand this many σ clear of zero to be usable. Everything downstream
    // divides by it — thresholds, the % display, the leak-rate fit — and a mean of −25 with a σ
    // of 95 (a real capture of a wavelength with no line on it) turns each of those into a
    // number whose sign is decided by noise. Rejecting it and saying so beats a monitor that
    // appears configured and reports nonsense.
    private const double MinBaselineMeanToSigma = 10.0;

    // Bumped when a change alters the units/scale of an extracted value; stamped onto every
    // captured baseline. See GoldenRunRatioBaseline.ExtractionRevision.
    public const int CurrentExtractionRevision = 1;

    private readonly object _gate = new();
    private readonly LeakMonitorSettings _settings;
    private readonly SystemLogger? _log;
    private readonly List<RatioMonitor> _monitors = new();
    private readonly Dictionary<string, RatioDefinition> _defs = new();

    private GoldenRun? _activeRun;
    // Plasma-present gate for absolute-intensity ratios, mirroring the intensity logger's save
    // trigger. Null until ConfigureTrigger is called (the host does so at start-up and on Apply).
    private PlasmaGate? _plasmaGate;
    private bool _gateWarned;                // "gate unusable" is logged once, not per frame
    // Last gate reading, for the snapshot. null = could not be evaluated on that frame,
    // which is reported as "gate unavailable" rather than as "plasma off".
    private bool? _lastGateOpen;
    // Live acquisition conditions, for stamping onto a Golden Run and for detecting that the
    // active baseline was captured under different ones. Device half comes from the host via
    // ConfigureAcquisition; the axis half is read off each frame.
    // Isolated gate dropouts — frames the spectrometer returned blank between two good ones.
    // Counted and reported because the gate now discards them silently, and an instrument fault
    // you cannot measure is one you cannot fix: this is the number that says whether changing
    // AcquireMode helped.
    private const double MaxDropoutSeconds = 1.0;
    private bool _gateWasOpen;                // a good frame has been seen at least once
    private int _closedRun;                   // consecutive gate-closed frames
    private DateTime _closedRunStart;
    private int _dropoutFrames, _dropoutEvents, _dropoutsSinceLog;
    private DateTime _lastDropoutLog;

    // Per-step process classification. Null while the site has not configured one, in which
    // case every ratio applies to every step and nothing below changes behaviour.
    private ProcessClassifier? _classifier;
    // The step in progress, as the boundary gate sees it. A step runs from the frame the
    // boundary metric first clears BoundaryThreshold to the frame it stops clearing it.
    private bool _stepOpen;
    private int _stepFrames;                  // gate-open frames in the step so far
    // Verdict for the running step, locked once taken. Null = not decided yet (the first
    // DecideAfterFrames frames), which is treated exactly like Unknown: nothing is judged.
    private string? _stepClass;
    private IReadOnlyList<ProcessDiscriminant> _stepDiscriminants = Array.Empty<ProcessDiscriminant>();
    private int _stepIndex;                   // increments per step, so a CSV row says which
    // When the boundary gate first closed during the step in progress. A brief closure is a
    // spectrometer dropout, not the end of the step — see AdvanceStep.
    private DateTime? _stepClosedSince;

    private AcquisitionFingerprint? _acquisition;
    private string _acquisitionWarning = "";  // "" when the active baseline still applies
    private string _acquisitionWarned = "";   // last warning logged, so it is logged once
    private LeakRateEstimator? _estimator;   // built from the active calibration, or null
    private CalibrationStatus _calStatus = CalibrationStatus.NotCalibrated;
    private LeakAlarmLevel _overall = LeakAlarmLevel.Idle;
    private bool _disposed;

    // Golden Run capture state.
    private bool _capturing;
    private string _captureName = "";
    private double _captureSeconds;
    private bool _captureHasStart;
    private DateTime _captureStart, _captureLast;
    private readonly Dictionary<string, RunningStats> _captureAccum = new();
    private readonly Dictionary<string, CaptureDiag> _captureDiag = new();
    // Reference-line readings during a capture, keyed by LineRegion.MeasurementKey: ratios that
    // read the same line the same way pool into one floor, ratios that don't never mix.
    private readonly Dictionary<string, RunningStats> _captureDenoms = new();

    // Plasma-present floor per ratio key, resolved from the active Golden Run's per-reference
    // floors. Rebuilt in ApplyGoldenRun — the only place either the baseline or _defs changes.
    private readonly Dictionary<string, double> _floorByRatio = new();

    // Leak-rate calibration-point capture state. Mirrors the Golden Run capture above but
    // averages each ratio's fractional rise (rawRatio / baselineMean − 1) at a known leak.
    private bool _calCapturing;
    private double _calLeakRate;
    private string _calLabel = "";
    private double _calSeconds;
    private bool _calHasStart;
    private DateTime _calStart, _calLast;
    private readonly Dictionary<string, RunningStats> _calAccum = new();

    public LeakMonitorEngine(LeakMonitorSettings settings, SystemLogger? systemLogger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = systemLogger;
        // A monitor exists for every defined ratio; the per-ratio Enabled flag decides
        // at runtime whether it is computed, so the operator can toggle it live.
        // _defs holds wavelength-corrected *clones* (not the persisted _settings.Ratios
        // objects), so the drift correction never leaks back into settings.json. Any future
        // live mutation of a ratio must therefore write through to _settings.Ratios too.
        var lookup = WavelengthCalibration.Build(_settings.WavelengthCorrections);
        foreach (var def in _settings.Ratios)
        {
            var corrected = WavelengthCalibration.Correct(def, lookup);
            _defs[corrected.Key] = corrected;
            _monitors.Add(new RatioMonitor(corrected));
        }
        BuildClassifier();
        ApplyGoldenRun(_settings.FindGoldenRun(_settings.ActiveGoldenRun)); // also builds the estimator
    }

    /// <summary>
    /// Rebuilds the process classifier from settings. Called from the constructor and from
    /// <see cref="ReloadRatios"/>, so a classifier edit is staged behind an acquisition restart
    /// exactly like a ratio edit — the two are one configuration (a ratio names the class the
    /// classifier produces) and applying half of it live would leave them disagreeing.
    /// </summary>
    private void BuildClassifier()
    {
        _classifier = null;
        EndStep();
        var cfg = _settings.ProcessClassifier;
        if (cfg is null || !cfg.Enabled) return;

        var built = new ProcessClassifier(cfg);
        if (!built.IsUsable)
        {
            // Enabled with no usable rule would name every step the fallback class, which looks
            // like it worked. Say so and stay off, so class-scoped ratios keep judging every
            // step rather than all silently standing down.
            _log?.LogSystemEvent(LogSeverity.Warning, "LeakMonitorClassifierUnusable",
                "Process classifier is enabled but has no usable rule — class scoping is off " +
                "and every ratio applies to every step",
                value: built.Description);
            return;
        }
        _classifier = built;
        _log?.LogSystemEvent(LogSeverity.Information, "LeakMonitorClassifier",
            "Process class is decided from the spectrum " +
            $"after {built.DecideAfterFrames} gate-open frame(s) and locked for the step",
            value: built.Description);
    }

    /// <summary>Ends the step in progress, so the next gate-open frame starts a new one.</summary>
    private void EndStep()
    {
        _stepOpen = false;
        _stepFrames = 0;
        _stepClass = null;
        _stepClosedSince = null;
        _stepDiscriminants = Array.Empty<ProcessDiscriminant>();
    }

    /// <summary>
    /// Tracks the plasma step this frame belongs to and names its process class once.
    ///
    /// <para>Step boundaries come from the <em>boundary</em> threshold — the lowest level any
    /// class runs at — not from the class's own, because the class is not known until several
    /// frames in: a boundary detector using the brightest class's threshold would never see a
    /// dim class's step start, so that class could never be classified and would stay dark for
    /// ever. That is the same failure as a save threshold set above the leak-free plasma
    /// (docs/leak-test-20260819-analysis.md), arrived at from the other direction.</para>
    ///
    /// <para>The verdict is taken once, at <see cref="ProcessClassifier.DecideAfterFrames"/>,
    /// and then locked: a plasma step does not change process half way through, so allowing the
    /// answer to move can only let ignition-transient noise flip it. A step whose discriminants
    /// could not be measured is <see cref="ProcessClassifier.Unknown"/> and nothing is judged
    /// during it.</para>
    /// </summary>
    private void AdvanceStep(float? metric, float[]? wl, float[]? inten, DateTime ts)
    {
        // Steps are tracked whether or not a classifier is configured. A plasma step is a fact
        // about the tool, not about the classification, and the batch layer needs its boundaries
        // either way — deriving them a second time from the snapshot stream would be a second
        // definition of "a step", which is how the plasma gate and the recorder would have come
        // to disagree if PlasmaGate had measured brightness its own way. Without a classifier
        // the step is simply never given a class.
        if (_plasmaGate is null) return;

        double boundary = _classifier?.BoundaryThreshold(_plasmaGate.Threshold)
                          ?? _plasmaGate.Threshold;
        bool open = metric is { } m && m > boundary;
        if (!open)
        {
            if (!_stepOpen) return;
            // A blank frame is not the end of a step. The spectrometer returns isolated blank
            // frames — 20 in 13 minutes in one measured run, which is why TrackGateDropouts
            // exists — and ending the step on the first one would restart the classification
            // mid-step, re-running the verdict on whatever frames happened to follow and
            // splitting one process step into several in everything downstream. Same threshold
            // the dropout counter uses: a closure longer than MaxDropoutSeconds is the plasma
            // genuinely going off.
            _stepClosedSince ??= ts;
            if ((ts - _stepClosedSince.Value).TotalSeconds <= MaxDropoutSeconds) return;
            EndStep();
            return;
        }
        _stepClosedSince = null;

        if (!_stepOpen)
        {
            _stepOpen = true;
            _stepFrames = 0;
            _stepClass = null;
            _stepDiscriminants = Array.Empty<ProcessDiscriminant>();
            unchecked { _stepIndex++; }
        }
        _stepFrames++;
        if (_classifier is null) return;                            // steps, but no classes
        if (_stepClass is not null) return;                         // already decided and locked
        if (_stepFrames < _classifier.DecideAfterFrames) return;    // still inside the transient

        var reading = _classifier.Evaluate(wl, inten);
        _stepDiscriminants = reading.Discriminants;
        _stepClass = reading.Measurable ? reading.ClassName : ProcessClassifier.Unknown;

        // Logged with the measured discriminants, not just the verdict: the first time a step
        // lands near a threshold, the number is the only thing that says whether the threshold
        // still fits this chamber.
        string values = _stepDiscriminants.Count == 0
            ? "not measurable"
            : string.Join(", ", _stepDiscriminants.Select(d => $"{d.Label}={d.Value:0.####}"));
        _log?.LogSystemEvent(
            _stepClass == ProcessClassifier.Unknown ? LogSeverity.Warning : LogSeverity.Information,
            "LeakMonitorProcessStep",
            _stepClass == ProcessClassifier.Unknown
                ? "Process step could not be classified — no ratio is judged during it"
                : $"Process step classified as {_stepClass}",
            value: values,
            related: $"Step={_stepIndex},Class={_stepClass}");
    }

    /// <summary>
    /// Whether this entry measures the step now running. True for every entry while no
    /// classifier is configured, and for an entry with no class (it applies to every step) —
    /// which is what keeps an existing installation behaving exactly as before.
    /// <see cref="ProcessClassifier.Unknown"/> and an undecided step match nothing: an entry
    /// stands down rather than judging a step whose process is not known.
    /// </summary>
    private bool AppliesToStep(RatioDefinition def) =>
        ProcessClassifier.AppliesTo(def, _stepClass, _classifier is not null);

    /// <summary>
    /// Which of <see cref="ProcessClassState"/>'s cases the step machine is in. Computed here
    /// rather than inferred downstream: the three states that all leave
    /// <see cref="LeakMonitorSnapshot.ProcessClass"/> empty are only distinguishable from the
    /// engine's own fields, and <c>SecsBridge</c> reads the snapshot, it does not compute.
    /// </summary>
    private ProcessClassState CurrentProcessClassState =>
        _classifier is null ? ProcessClassState.NotConfigured
        : !_stepOpen ? ProcessClassState.NoStep
        : _stepClass is null ? ProcessClassState.Deciding
        : _stepClass == ProcessClassifier.Unknown ? ProcessClassState.Unclassified
        : ProcessClassState.Classified;

    /// <summary>
    /// Labels of the classifier's discriminants, in the order
    /// <see cref="LeakMonitorSnapshot.ProcessDiscriminants"/> reports them. Empty when no
    /// classifier is configured — which is what keeps the ratio CSV byte-identical to the one
    /// an unconfigured installation writes today.
    /// </summary>
    public IReadOnlyList<string> ProcessDiscriminantLabels
    {
        get
        {
            lock (_gate)
                return _classifier is null
                    ? Array.Empty<string>()
                    : (_settings.ProcessClassifier?.Rules ?? new List<ProcessClassRule>())
                        .Where(r => !string.IsNullOrWhiteSpace(r.ClassName))
                        .Select(r => r.Label)
                        .ToArray();
        }
    }

    /// <summary>Class of the plasma step now running: a configured class name,
    /// <see cref="ProcessClassifier.Unknown"/>, or "" when no classifier is configured or no
    /// step is in progress.</summary>
    public string CurrentProcessClass
    {
        get { lock (_gate) return _stepClass ?? ""; }
    }

    /// <summary>
    /// Points the absolute-intensity plasma gate at the intensity logger's save trigger — same
    /// quantity, same threshold, so the gate is open exactly while the logger would be recording.
    /// Call at start-up and on every Apply; the gate is hot-applied (unlike a ratio-set edit,
    /// which is staged until acquisition restarts) because it is a logger setting, and the two
    /// would otherwise disagree for the rest of the run.
    /// <para>Ratio-mode entries are unaffected — they keep gating on their reference line, which
    /// they divide by anyway.</para>
    /// </summary>
    public void ConfigureTrigger(LoggerSettings? settings)
    {
        string? before, after;
        lock (_gate)
        {
            before = _plasmaGate?.Description;
            _plasmaGate = settings is null ? null : new PlasmaGate(settings);
            after = _plasmaGate?.Description;
            _gateWarned = false;
            // The boundary threshold is derived from this gate, so a step detected under the
            // old one has no meaning under the new one — end it and let the next frame start
            // a step the new threshold agrees with.
            EndStep();
            // The last reading was taken through the old gate; a new one has not judged a
            // frame yet, so report "unavailable" rather than carrying the stale answer over.
            _lastGateOpen = null;
        }
        if (before != after)
            _log?.LogSystemEvent(LogSeverity.Information, "LeakMonitorPlasmaGate",
                "Absolute-intensity ratios gate on the logger save trigger",
                value: after ?? "(none)");
    }

    /// <summary>
    /// Tells the engine what acquisition parameters the spectra are being taken with, so a
    /// Golden Run can record them and a later mismatch can be reported. Call at start-up and
    /// whenever device parameters are applied. An absolute-intensity baseline is a count, and
    /// counts scale with integration time and averaging — without this, changing the exposure
    /// silently re-scales every reading against a baseline that no longer applies.
    /// </summary>
    public void ConfigureAcquisition(DeviceSettings? settings)
    {
        lock (_gate)
        {
            _acquisition = settings is null ? null : new AcquisitionFingerprint
            {
                IntegrationTimeMs = settings.IntegrationTimeMs,
                AverageCount = settings.AverageCount,
                BoxcarWidth = settings.BoxcarWidth,
                AcquireMode = settings.AcquireMode.ToString(),
                AverageMode = settings.AverageMode.ToString(),
                BackgroundRemove = settings.EnableBackgroundRemove,
                StraylightCorrection = settings.EnableStraylightCorrection,
                LinearityCorrection = settings.EnableLinearityCorrection,
            };
        }
    }

    /// <summary>
    /// The conditions frames are currently arriving under — the same object a Golden Run is
    /// stamped with, axis included once a frame has been seen. Exposed so the recording written
    /// alongside can carry it too: a CSV says nothing about the exposure it was taken at, which
    /// is what leaves an offline-built baseline unable to answer the question the mismatch check
    /// asks. Null before <see cref="ConfigureAcquisition"/> has been called.
    /// </summary>
    public AcquisitionFingerprint? CurrentAcquisition
    {
        get { lock (_gate) return _acquisition?.Clone(); }
    }

    /// <summary>Raised for every processed frame. Fires on the acquisition thread.</summary>
    public event EventHandler<LeakMonitorSnapshot>? SampleProcessed;

    /// <summary>Raised when the composite alarm level changes.</summary>
    public event EventHandler<LeakAlarmEventArgs>? AlarmStateChanged;

    /// <summary>Raised when a Golden Run capture finishes and becomes the active baseline.
    /// Only fires for an accepted capture — a discarded one changed nothing, so there is
    /// nothing for the host to persist. See <see cref="GoldenRunCaptureFinished"/>.</summary>
    public event EventHandler<GoldenRun>? GoldenRunCaptured;

    /// <summary>Raised when a Golden Run capture finishes, accepted or discarded, carrying the
    /// per-ratio rejection reasons. Fires after <see cref="GoldenRunCaptured"/>, so a host that
    /// reports the outcome to the operator sees the new baseline already applied.</summary>
    public event EventHandler<GoldenRunCaptureResult>? GoldenRunCaptureFinished;

    /// <summary>Raised when a leak-rate calibration point finishes averaging. The host collects
    /// these across leak elements and fits them into a <see cref="LeakCalibration"/>.</summary>
    public event EventHandler<LeakCalPoint>? CalibrationPointCaptured;

    /// <summary>Raised when the ratio configuration changes (e.g. a reference line swap),
    /// so the host can persist <see cref="Settings"/>.</summary>
    public event EventHandler? ConfigurationChanged;

    /// <summary>Raised after <see cref="ReloadRatios"/> rebuilds the monitored-ratio set.</summary>
    public event EventHandler? RatiosReloaded;

    /// <summary>
    /// Raised when <see cref="Acknowledge"/> actually cleared a latched alarm — the same
    /// condition that writes the <c>LeakMonitorAcknowledged</c> audit entry, so the event
    /// means exactly what that entry means: a person ended a confirmed alarm.
    /// </summary>
    public event EventHandler<LeakAcknowledgedEventArgs>? Acknowledged;

    /// <summary>The live settings object — mutated in place as Golden Runs are captured.</summary>
    public LeakMonitorSettings Settings => _settings;

    /// <summary>Definitions of the ratios actually being monitored (enabled only).</summary>
    public IReadOnlyList<RatioDefinition> MonitoredRatios =>
        _monitors.Select(m => _defs[m.Key]).ToList();

    /// <summary>Feeds one spectrum frame through the monitor. Safe to call off the UI thread.</summary>
    public void ProcessSample(SpectrumSample sample)
    {
        if (sample is null || _disposed) return;

        LeakMonitorSnapshot snap;
        LeakAlarmLevel oldOverall, newOverall;
        GoldenRunCaptureResult? captureResult = null;
        LeakCalPoint? capturedPoint = null;
        bool wasCapturing;

        lock (_gate)
        {
            if (!_settings.Enabled) return;

            // Whether this frame was judged during a capture — read before FinalizeCapture can
            // flip it, since it decides how this frame's transition is reported.
            wasCapturing = _capturing;

            var wl = sample.Wavelengths;
            var inten = sample.Intensities;

            // Keep the live acquisition fingerprint current and check the active baseline
            // against it — an exposure change mid-campaign re-scales every absolute reading.
            UpdateAcquisitionStatus(wl);

            // Absolute-intensity ratios gate on the logger's trigger metric, not on their
            // reference line — they never divide by it, so requiring it to extract positive only
            // let the reference's own noise (or a systematically negative extraction on a curved
            // continuum) decide whether the frame was evaluated at all. Measured once per frame
            // and shared by every absolute ratio; null = the gate could not be evaluated.
            // One brightness reading per frame, compared against up to two thresholds: the
            // boundary threshold that says a plasma step is running at all, and — once that step
            // has been classified — the threshold that class runs at. Measuring it twice would
            // be a second definition of "how bright is this frame", which is the thing PlasmaGate
            // exists to prevent.
            float? metric = _plasmaGate?.TriggerMetric(wl, inten);
            AdvanceStep(metric, wl, inten, sample.Timestamp);

            bool? triggerPlasma = null;
            if (_plasmaGate is { IsUsable: true } && metric is { } m)
                triggerPlasma = m > (_classifier is null
                    ? _plasmaGate.Threshold
                    : _classifier.PlasmaThresholdFor(_stepClass, _plasmaGate.Threshold));
            _lastGateOpen = triggerPlasma;
            if (triggerPlasma is { } g) TrackGateDropouts(g, sample.Timestamp);

            foreach (var mon in _monitors)
            {
                var def = _defs[mon.Key];
                // A capture is judged against the baseline it is about to replace, so any
                // deviation it shows is expected and says nothing about a leak. Show it, don't
                // latch it — an acknowledge-me alarm from a routine re-baseline teaches
                // operators to acknowledge without reading.
                mon.SuppressLatch = wasCapturing;
                if (!def.Enabled)
                {
                    mon.MarkDisabled();
                    if (_capturing) GetDiag(mon.Key).Disabled = true;
                    continue;
                }

                // Out of its class this entry measures a different plasma, so it neither judges
                // nor feeds a baseline. It is not disabled and the tool is not idle, so it says
                // so with a state of its own — and its latch survives, because a confirmed leak
                // does not end when the tool moves to the next process step.
                if (!AppliesToStep(def))
                {
                    mon.MarkNotApplicable();
                    if (_capturing) GetDiag(mon.Key).OutOfClass++;
                    continue;
                }

                bool absolute = def.MonitorMode == MonitorMode.AbsoluteIntensity;
                // An unusable / unmeasurable gate leaves the ratio ungated rather than dark:
                // "we can't tell" is not "plasma off", and a silently dead ratio is the failure
                // mode this gate exists to remove. Said once in the log.
                if (triggerPlasma is null) WarnGateUnusableOnce();
                // The floor is this ratio's own reference line's leak-free level, not a level
                // pooled across every ratio: a floor derived from a brighter reference sits
                // above a fainter one's normal reading and closes that ratio permanently.
                // During a capture it stands down entirely — the inherited floor came from a
                // previous run and would otherwise block capturing a fresh baseline after a
                // peak shift or a lower-power recipe. The new floors come out of the capture
                // itself in FinalizeCapture().
                double floor = _capturing || absolute ? 0.0
                    : _floorByRatio.TryGetValue(mon.Key, out var f) ? f : 0.0;
                // What this frame says about this ratio, and whether it may feed a baseline.
                // Shared with the offline builder — see RatioFrameSampling for why that must be
                // one definition and not two.
                var fs = RatioFrameSampling.Evaluate(def, wl, inten, triggerPlasma, floor);
                mon.Update(fs.Numerator, fs.Denominator, sample.Timestamp, fs.PlasmaPresent);
                double value = fs.Value;

                if (_capturing)
                {
                    // Tally why each frame did or didn't feed the baseline, so a ratio
                    // that ends the capture with no samples can be explained in the log.
                    var diag = GetDiag(mon.Key);
                    diag.Frames++;
                    if (fs.NumeratorMissing) diag.NumeratorMissing++;
                    if (!fs.GateOpen) diag.GateClosed++;
                    if (fs.ReferenceMissing) diag.ReferenceMissing++;
                    if (fs.Evaluable)
                    {
                        if (fs.LowSnr)
                        {
                            diag.LowSnr++;
                        }
                        else
                        {
                            diag.Accepted++;
                            GetAccum(mon.Key).Add(value);
                            // Only reference lines that are actually used contribute to the
                            // recipe's plasma floor; an absolute ratio's reference is inert.
                            // Pooled by measurement key, so two ratios sharing a reference
                            // share its floor and get it from twice the frames.
                            if (!absolute) GetDenomAccum(def.Denominator.MeasurementKey).Add(fs.Denominator.Value);
                        }
                    }
                }

                // Calibration point: average the rise relative to the active baseline. Needs a
                // baseline (x is defined against it) and live plasma. Where the value carries a
                // continuum pedestal, record the *absolute* rise Δ = value − baseMean: the
                // fractional rise would compress a real response into a fraction of a percent of
                // the pedestal. Everywhere else the fractional rise is the better-conditioned
                // quantity (it is immune to a pure re-scaling). The fit's Absolute flag records
                // which unit was used, and refuses a reading in the other one.
                if (_calCapturing && mon.HasBaseline && fs.Evaluable && mon.BaselineMean > 0)
                {
                    double x = def.ValueHasPedestal
                        ? value - mon.BaselineMean
                        : value / mon.BaselineMean - 1.0;
                    GetCalAccum(mon.Key).Add(x);
                }
            }

            if (_capturing)
            {
                if (!_captureHasStart)
                {
                    _captureHasStart = true;
                    _captureStart = sample.Timestamp;
                }
                _captureLast = sample.Timestamp;
                if ((_captureLast - _captureStart).TotalSeconds >= _captureSeconds)
                    captureResult = FinalizeCapture();
            }

            if (_calCapturing)
            {
                if (!_calHasStart)
                {
                    _calHasStart = true;
                    _calStart = sample.Timestamp;
                }
                _calLast = sample.Timestamp;
                if ((_calLast - _calStart).TotalSeconds >= _calSeconds)
                    capturedPoint = FinalizeCalibrationPoint();
            }

            oldOverall = _overall;
            _overall = ComputeOverall();
            newOverall = _overall;
            snap = BuildSnapshot(sample.Timestamp, sample.IsTestMode);
        }

        SampleProcessed?.Invoke(this, snap);

        // A transition seen while capturing is measured against the outgoing baseline — not
        // reported, for the same reason it is not latched. The capture's own summary line
        // (LogCaptureDeviation) is what records where the ratios actually sat.
        if (newOverall != oldOverall && !wasCapturing &&
            !(snap.TestMode && _settings.SuppressAlarmsInTestMode))
        {
            AlarmStateChanged?.Invoke(this, new LeakAlarmEventArgs
            {
                OldLevel = oldOverall,
                NewLevel = newOverall,
                Timestamp = snap.Timestamp,
                Guards = snap.Ratios.Where(r => r.Role == RatioRole.Guard).ToList(),
            });
        }

        if (captureResult is not null)
        {
            // Only an accepted capture changed anything — a discarded one leaves the settings,
            // the stored runs and the active baseline exactly as they were, so there is nothing
            // to persist and nothing to re-select.
            if (captureResult.Accepted)
                GoldenRunCaptured?.Invoke(this, captureResult.Run);
            GoldenRunCaptureFinished?.Invoke(this, captureResult);
        }

        if (capturedPoint is not null)
            CalibrationPointCaptured?.Invoke(this, capturedPoint);
    }

    /// <summary>Starts averaging the ratios into a new Golden Run baseline.</summary>
    public void BeginGoldenRunCapture(string name, double seconds)
    {
        lock (_gate)
        {
            _capturing = true;
            _captureName = string.IsNullOrWhiteSpace(name) ? "Default" : name.Trim();
            _captureSeconds = Math.Max(1.0, seconds);
            _captureHasStart = false;
            _captureAccum.Clear();
            _captureDiag.Clear();
            _captureDenoms.Clear();
        }
    }

    public void CancelGoldenRunCapture()
    {
        lock (_gate) _capturing = false;
    }

    /// <summary>
    /// Starts averaging each ratio's fractional rise at a known leak rate into one calibration
    /// point. Requires an active Golden Run baseline (the rise is measured against it) and live
    /// plasma; ratios with no usable frames are simply omitted from the resulting point.
    /// </summary>
    public void BeginCalibrationPointCapture(double leakRate, string label, double seconds)
    {
        lock (_gate)
        {
            _calCapturing = true;
            _calLeakRate = leakRate;
            _calLabel = label?.Trim() ?? "";
            _calSeconds = Math.Max(1.0, seconds);
            _calHasStart = false;
            _calAccum.Clear();
        }
    }

    public void CancelCalibrationCapture()
    {
        lock (_gate) _calCapturing = false;
    }

    /// <summary>
    /// Current reference (denominator) line label per monitored ratio — used to stamp a fitted
    /// calibration so a later reference swap invalidates it. Empty for an absolute-intensity
    /// ratio, which doesn't use the reference at all: swapping a line that takes no part in the
    /// measurement must not throw away a calibration. Same rule as the Golden Run baselines.
    /// </summary>
    public IReadOnlyDictionary<string, string> CurrentReferenceLabels()
    {
        lock (_gate)
            return _defs.ToDictionary(kv => kv.Key,
                kv => kv.Value.MonitorMode == MonitorMode.AbsoluteIntensity
                    ? "" : kv.Value.Denominator.Label);
    }

    /// <summary>
    /// Unit each ratio's rise is expressed in — true = the absolute rise Δ (a value carrying a
    /// continuum pedestal), false = the fractional rise. Used to stamp a fitted calibration with
    /// the unit it was made in and to reject it later if that changed: a fractional fit cannot
    /// score an absolute reading, or vice versa. See <see cref="RatioDefinition.ValueHasPedestal"/>.
    /// </summary>
    public IReadOnlyDictionary<string, bool> CurrentValueUnits()
    {
        lock (_gate)
            return _defs.ToDictionary(kv => kv.Key, kv => kv.Value.ValueHasPedestal);
    }

    /// <summary>
    /// Clears every latched alarm. Logged, because this is the one action that ends a confirmed
    /// leak alarm and it leaves no other trace: without it the audit CSV shows the alarm
    /// stopping by itself, which is exactly the question asked afterwards — who cleared it, when,
    /// and what was still latched at the time. A call that finds nothing latched is a no-op and
    /// says nothing, so the entry means what it says.
    /// </summary>
    /// <param name="user">Signed-in operator, for the log line.</param>
    public void Acknowledge(string? user = null)
    {
        LeakAlarmLevel oldOverall, newOverall;
        List<string> cleared;
        lock (_gate)
        {
            cleared = _monitors.Where(m => m.HasLatchedAlarm)
                               .Select(m => _defs.TryGetValue(m.Key, out var d) ? d.DisplayName : m.Key)
                               .ToList();
            foreach (var mon in _monitors) mon.Acknowledge();
            oldOverall = _overall;
            _overall = ComputeOverall();
            newOverall = _overall;
        }

        if (cleared.Count > 0)
            _log?.LogSystemEvent(LogSeverity.Warning, "LeakMonitorAcknowledged",
                $"Operator acknowledged the leak alarm — {cleared.Count} latched ratio alarm(s) " +
                $"cleared, composite {oldOverall} → {newOverall}. Nothing clears a latch without " +
                "an entry of its own, so this alarm ended here.",
                related: $"User={user ?? "(unknown)"}",
                value: string.Join(", ", cleared));

        if (cleared.Count > 0)
            Acknowledged?.Invoke(this, new LeakAcknowledgedEventArgs
            {
                User = user ?? "",
                ClearedRatios = cleared,
                OldLevel = oldOverall,
                NewLevel = newOverall,
                Timestamp = DateTime.Now,
            });

        if (newOverall != oldOverall)
            AlarmStateChanged?.Invoke(this, new LeakAlarmEventArgs
            {
                OldLevel = oldOverall, NewLevel = newOverall, Timestamp = DateTime.Now,
            });
    }

    /// <summary>
    /// Resets every ratio's live smoothing / trend / confirmation state and the composite level
    /// so a new experiment run starts clean — used by the Monitor-tab Reset after a parameter
    /// change, so pre-change frames don't bleed into the post-change EMA. Leaves the Golden Run
    /// baseline, the ratio configuration, and any calibration untouched; latched alarms are kept
    /// unless <paramref name="clearAlarms"/> is set.
    /// <para><paramref name="clearAlarms"/> ends a confirmed alarm without an operator
    /// acknowledgement, which is the one thing the audit trail must never show happening by
    /// itself. It is therefore logged in its own right, naming <paramref name="reason"/> and the
    /// ratios that were latched — say why, or the entry is the same silence it exists to
    /// prevent.</para>
    /// </summary>
    /// <param name="reason">Why the alarms are being cleared, for the log entry. Required in
    /// substance whenever <paramref name="clearAlarms"/> is set.</param>
    public void ResetRuntimeState(bool clearAlarms, string? reason = null)
    {
        LeakAlarmLevel oldOverall, newOverall;
        List<string> cleared;
        lock (_gate)
        {
            cleared = clearAlarms
                ? _monitors.Where(m => m.HasLatchedAlarm)
                           .Select(m => _defs.TryGetValue(m.Key, out var d) ? d.DisplayName : m.Key)
                           .ToList()
                : new List<string>();
            foreach (var mon in _monitors) mon.ResetRuntime(clearAlarms);
            // A new run restarts step tracking too: the step in progress belonged to the old
            // one, and carrying its verdict over would judge the first frames of the new run
            // against a class nobody measured.
            EndStep();
            oldOverall = _overall;
            _overall = ComputeOverall();
            newOverall = _overall;
        }

        if (cleared.Count > 0)
            _log?.LogSystemEvent(LogSeverity.Warning, "LeakMonitorAlarmLatchCleared",
                $"{cleared.Count} latched ratio alarm(s) cleared without an operator " +
                $"acknowledgement — {reason ?? "no reason given"}. Composite {oldOverall} → " +
                $"{newOverall}.",
                related: $"Reason={reason ?? "(none)"}",
                value: string.Join(", ", cleared));

        if (newOverall != oldOverall)
            AlarmStateChanged?.Invoke(this, new LeakAlarmEventArgs
            {
                OldLevel = oldOverall, NewLevel = newOverall, Timestamp = DateTime.Now,
            });
    }

    /// <summary>
    /// Switches the active baseline to a previously captured Golden Run, and auto-selects the
    /// leak-rate calibration bound to it (a calibration follows its recipe baseline, so the live
    /// estimate doesn't fall into a stale <see cref="CalibrationStatus.BaselineMismatch"/> on a
    /// recipe change). Persists the new baseline + paired calibration via
    /// <see cref="ConfigurationChanged"/>.
    /// </summary>
    public void SelectGoldenRun(string? name)
    {
        lock (_gate)
        {
            _settings.ActiveGoldenRun = name;
            AutoPairCalibration(name);
            ApplyGoldenRun(_settings.FindGoldenRun(name)); // rebuilds the estimator + logs status
        }
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Points <see cref="LeakMonitorSettings.ActiveCalibration"/> at the calibration bound to
    /// <paramref name="goldenRunName"/> — the most recently captured one when several share the
    /// baseline — or clears it when none exists, so the active calibration always tracks the
    /// active recipe. Caller holds <see cref="_gate"/>; the subsequent
    /// <see cref="ApplyGoldenRun"/> → <see cref="BuildEstimator"/> logs the status transition.
    /// </summary>
    private void AutoPairCalibration(string? goldenRunName)
    {
        string? match = null;
        if (goldenRunName is not null)
        {
            LeakCalibration? best = null;
            foreach (var c in _settings.Calibrations)
                if (string.Equals(c.GoldenRunName, goldenRunName, StringComparison.Ordinal) &&
                    (best is null || c.CapturedUtc > best.CapturedUtc))
                    best = c;
            match = best?.Name;
        }
        _settings.ActiveCalibration = match;
    }

    /// <summary>Rebuilds the runtime leak-rate estimator from <see cref="Settings"/>'s active
    /// calibration. Call after a calibration is saved or the active one is switched.</summary>
    public void ReloadCalibration()
    {
        lock (_gate) BuildEstimator();
    }

    /// <summary>
    /// (Re)builds the runtime estimator and re-evaluates calibration validity. A calibration
    /// only applies while the active Golden Run baseline matches the one it was captured against
    /// — the per-ratio rise is defined relative to that baseline, so a different baseline would
    /// silently corrupt the estimate. Logs each status transition.
    /// </summary>
    private void BuildEstimator()
    {
        var prev = _calStatus;
        string? prevCal = _activeCalForLog;
        var cal = _settings.FindCalibration(_settings.ActiveCalibration);
        if (cal is null)
        {
            _estimator = null;
            _calStatus = CalibrationStatus.NotCalibrated;
        }
        else if (_settings.ActiveGoldenRun is null ||
                 !string.Equals(cal.GoldenRunName, _settings.ActiveGoldenRun, StringComparison.Ordinal))
        {
            _estimator = null;
            _calStatus = CalibrationStatus.BaselineMismatch;
        }
        else
        {
            _estimator = new LeakRateEstimator(cal);
            _calStatus = CalibrationStatus.Active;
        }
        _activeCalForLog = cal?.Name;

        if (_calStatus != prev || !string.Equals(_activeCalForLog, prevCal, StringComparison.Ordinal))
            LogCalibrationStatus(cal);
    }

    private string? _activeCalForLog;

    private void LogCalibrationStatus(LeakCalibration? cal)
    {
        switch (_calStatus)
        {
            case CalibrationStatus.Active:
                _log?.LogSystemEvent(LogSeverity.Information, "LeakCalibrationActive",
                    $"Leak-rate calibration “{cal!.Name}” active against baseline " +
                    $"“{_settings.ActiveGoldenRun}”.",
                    related: $"Calibration={cal.Name},Baseline={_settings.ActiveGoldenRun}");
                break;
            case CalibrationStatus.BaselineMismatch:
                _log?.LogSystemEvent(LogSeverity.Warning, "LeakCalibrationSuspended",
                    $"Leak-rate calibration “{cal!.Name}” suspended — it was captured against " +
                    $"baseline “{cal.GoldenRunName}”, but the active baseline is " +
                    $"“{_settings.ActiveGoldenRun ?? "(none)"}”. Select that baseline or re-calibrate.",
                    related: $"Calibration={cal.Name},NeedBaseline={cal.GoldenRunName}," +
                             $"ActiveBaseline={_settings.ActiveGoldenRun ?? "(none)"}");
                break;
            case CalibrationStatus.NotCalibrated:
                // Only meaningful as a transition away from a previously selected calibration.
                _log?.LogSystemEvent(LogSeverity.Information, "LeakCalibrationCleared",
                    "Leak-rate estimation off — no calibration selected.");
                break;
        }
    }

    /// <summary>
    /// Includes or excludes a ratio from monitoring. A disabled ratio is not computed
    /// and never contributes to the composite alarm; it can be toggled back on live.
    /// </summary>
    public void SetRatioEnabled(string ratioKey, bool enabled)
    {
        lock (_gate)
        {
            if (!_defs.TryGetValue(ratioKey, out var def) || def.Enabled == enabled) return;
            def.Enabled = enabled;
        }
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Swaps a ratio's reference (denominator) line. Any Golden Run baseline captured
    /// against the previous reference stops applying — that ratio reads "No Baseline"
    /// until a new Golden Run is captured.
    /// </summary>
    public void SetRatioReference(string ratioKey, LineRegion reference)
    {
        if (reference is null) return;
        lock (_gate)
        {
            if (!_defs.TryGetValue(ratioKey, out var def)) return;
            def.Denominator = reference.Clone();
            ApplyGoldenRun(_activeRun);
        }
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Rebuilds the monitored-ratio set from <see cref="Settings"/> — applies a configuration
    /// edited in the Ratio Setup tab. Meant to be called when OES acquisition (re)starts, so
    /// a mid-run edit never disturbs a live evaluation. Resets per-ratio smoothing/state and
    /// re-applies the active Golden Run.
    /// <para><b>Latched alarms survive.</b> This runs on every Stop→Start of acquisition, and
    /// rebuilding the monitors used to drop every latch with it — so a confirmed leak could be
    /// cleared by restarting acquisition, silently and without an Acknowledge. A latch is
    /// carried over for any ratio still measuring the same quantity
    /// (<see cref="RatioDefinition.MeasuresSameAs"/>) and is dropped, with a log line, for one
    /// that was redefined, disabled or removed — there the alarm referred to a measurement that
    /// no longer exists.</para>
    /// </summary>
    public void ReloadRatios()
    {
        lock (_gate)
        {
            var latched = _monitors.Where(m => m.HasLatchedAlarm).Select(m => m.Key).ToHashSet();
            var previousDefs = new Dictionary<string, RatioDefinition>(_defs);

            _monitors.Clear();
            _defs.Clear();
            var lookup = WavelengthCalibration.Build(_settings.WavelengthCorrections);
            foreach (var def in _settings.Ratios)
            {
                var corrected = WavelengthCalibration.Correct(def, lookup);
                _defs[corrected.Key] = corrected;
                _monitors.Add(new RatioMonitor(corrected));
            }
            BuildClassifier();
            ApplyGoldenRun(_settings.FindGoldenRun(_settings.ActiveGoldenRun)); // rebuilds the estimator

            foreach (var key in latched)
            {
                var mon = _monitors.FirstOrDefault(m => m.Key == key);
                previousDefs.TryGetValue(key, out var before);
                _defs.TryGetValue(key, out var after);
                if (mon is not null && after is { Enabled: true } && (before?.MeasuresSameAs(after) ?? false))
                {
                    mon.RestoreLatchedAlarm();
                }
                else
                {
                    _log?.LogSystemEvent(LogSeverity.Information, "LeakMonitorAlarmLatchDropped",
                        $"Latched alarm on {before?.DisplayName ?? key} was not carried over — the " +
                        "ratio was removed, disabled, or redefined, so the alarm referred to a " +
                        "measurement that no longer exists.",
                        related: $"Ratio={key}");
                }
            }
            // Recompute rather than assume Idle: a carried-over latch must still read Alarm.
            _overall = ComputeOverall();
        }
        RatiosReloaded?.Invoke(this, EventArgs.Empty);
    }

    // --- internals -----------------------------------------------------------

    private GoldenRunCaptureResult FinalizeCapture()
    {
        _capturing = false;

        var rejected = new List<GoldenRunRatioRejection>();
        var run = new GoldenRun
        {
            Name = _captureName,
            CapturedUtc = DateTime.UtcNow,
            DurationSeconds = (_captureLast - _captureStart).TotalSeconds,
            Acquisition = _acquisition?.Clone(),
            // Stamped so that a run with no Source means "stored before this existed" and
            // nothing else — in particular, not "built offline".
            Source = new GoldenRunSource { Kind = GoldenRunSource.LiveCapture },
        };
        // One floor per reference line at 20 % of its own leak-free level. Ordered so the same
        // capture always writes the same settings.json, which makes a diff mean something.
        foreach (var kv in _captureDenoms.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (kv.Value.Count == 0) continue;
            run.PlasmaFloors.Add(new PlasmaFloorEntry
            {
                ReferenceKey = kv.Key,
                ReferenceLabel = kv.Key.Split('|')[0],
                Floor = 0.2 * kv.Value.Mean,
            });
        }
        foreach (var mon in _monitors)
        {
            _captureAccum.TryGetValue(mon.Key, out var acc);
            int accepted = acc?.Count ?? 0;
            if (accepted == 0)
            {
                // The ratio produced no usable samples — record why so the operator
                // isn't left guessing at a permanent "No Baseline".
                rejected.Add(Reject(mon.Key, ReportDroppedRatio(mon.Key, run.Name)));
                continue;
            }

            // Even with some samples, if too few of the SNR-evaluable frames cleared the floor
            // the line hovered around noise and the survivors are a biased upward sliver — reject
            // rather than set a misleading baseline.
            _captureDiag.TryGetValue(mon.Key, out var d);
            int lowSnr = d?.LowSnr ?? 0;
            int evaluable = accepted + lowSnr;
            if (evaluable > 0 && accepted < MinBaselineAcceptFraction * evaluable)
            {
                var dropDef = _defs[mon.Key];
                string reason =
                    $"only {accepted} of {evaluable} frames cleared the SNR floor " +
                    $"({dropDef.MinSnr:0.#}); the line sat near the noise floor. Raise plasma " +
                    "intensity / exposure, lower Min SNR, or use a stronger line";
                _log?.LogSystemEvent(LogSeverity.Warning, "GoldenRunRatioLowSnr",
                    $"Ratio {dropDef.DisplayName} baseline rejected — {reason}.",
                    related: $"GoldenRun={run.Name},Ratio={mon.Key}");
                rejected.Add(Reject(mon.Key, reason));
                continue;
            }

            var baselineDef = _defs[mon.Key];

            // Everything downstream divides by this mean. A mean that isn't clear of zero
            // compared with its own scatter makes thresholds, the % display and the leak-rate
            // fit into sign-of-the-noise arithmetic — reject it and name the number, rather
            // than ship a monitor that looks configured and reports nonsense.
            double mean = acc!.Mean, sd = acc.StdDev;
            if (mean <= 0 || (sd > 0 && mean < MinBaselineMeanToSigma * sd))
            {
                string reason =
                    $"mean {mean:G4} ± {sd:G3} is not clear of zero (needs mean > " +
                    $"{MinBaselineMeanToSigma:0} σ). The line is at or below the local continuum " +
                    "estimate, so every derived quantity would be noise. Use a stronger line, or " +
                    "switch the extraction to Raw (no baseline subtraction) if there is no peak " +
                    "at this wavelength";
                _log?.LogSystemEvent(LogSeverity.Warning, "GoldenRunRatioUnstableBaseline",
                    $"Ratio {baselineDef.DisplayName} baseline rejected — {reason}.",
                    related: $"GoldenRun={run.Name},Ratio={mon.Key}");
                rejected.Add(Reject(mon.Key, reason));
                continue;
            }

            run.Baselines.Add(new GoldenRunRatioBaseline
            {
                Key = mon.Key,
                Mean = mean,
                Sigma = sd,
                SampleCount = acc.Count,
                ExtractionRevision = CurrentExtractionRevision,
                Mode = baselineDef.MonitorMode,
                // Absolute-intensity baselines don't involve the reference line, so they don't
                // record one — a later reference swap must not invalidate them.
                ReferenceLabel = baselineDef.MonitorMode == MonitorMode.AbsoluteIntensity
                    ? "" : baselineDef.Denominator.Label,
            });
        }

        // Where the ratios sat during the window, measured against the baseline still active
        // while it ran. Recorded before that baseline is replaced, and stated as a fact rather
        // than judged: re-capturing after a recipe change makes a large deviation expected and
        // meaningless, while re-capturing the *same* recipe makes it the whole story — it says
        // the state being frozen as "leak-free" was already well away from the last one.
        LogCaptureDeviation(run.Name);

        // A capture that produced nothing is discarded outright: it is not stored, does not
        // become the active baseline, and — crucially — does not replace a same-named run that
        // is already working. Storing it would have taken every ratio's baseline away (the panel
        // reads "No Baseline", nothing takes part in the composite alarm) and dropped the paired
        // calibration with it, on the strength of a capture that measured nothing. The operator
        // is told; the settings are left exactly as they were.
        if (run.Baselines.Count == 0)
        {
            _log?.LogSystemEvent(LogSeverity.Warning, "GoldenRunEmpty",
                $"Golden Run “{run.Name}” captured no usable ratio baselines and was discarded — " +
                "check the spectrometer wavelength range, the plasma state, and which ratios are " +
                $"enabled. The active baseline is unchanged ({_settings.ActiveGoldenRun ?? "none"}).",
                related: $"GoldenRun={run.Name}");
            return new GoldenRunCaptureResult
            {
                Run = run,
                Accepted = false,
                Rejected = rejected,
                ActiveGoldenRun = _settings.ActiveGoldenRun,
            };
        }

        // Replacing a same-named run destroys it. If the new capture is missing a baseline the
        // old one had, that loss is permanent and the operator is the only one who can weigh it
        // — so the run is held, unstored, until they answer. A capture that loses nothing (the
        // ordinary weekly re-baseline) is stored without asking; a confirmation everyone sees
        // every time is one nobody reads by the third week.
        var replaced = _settings.FindGoldenRun(run.Name);
        var lost = replaced is null
            ? new List<GoldenRunRatioRejection>()
            : replaced.Baselines
                .Where(b => b.Mean > 0 && run.Find(b.Key) is null)
                .Select(b => Reject(b.Key, "had a baseline in the run being replaced"))
                .ToList();
        if (lost.Count > 0)
            return new GoldenRunCaptureResult
            {
                Run = run,
                Accepted = false,
                NeedsConfirmation = true,
                Rejected = rejected,
                Lost = lost,
                Replaced = replaced,
                ActiveGoldenRun = _settings.ActiveGoldenRun,
            };

        StoreCapturedRun(run);
        return new GoldenRunCaptureResult
        {
            Run = run,
            Accepted = true,
            Rejected = rejected,
            ActiveGoldenRun = _settings.ActiveGoldenRun,
        };
    }

    /// <summary>
    /// Stores a finished capture, makes it the active baseline and re-pairs the calibration.
    /// Caller holds the lock. Split out of <see cref="FinalizeCapture"/> so a capture that has
    /// to be confirmed first can be stored later, from <see cref="ConfirmCapturedRun"/>.
    /// </summary>
    private void StoreCapturedRun(GoldenRun run)
    {
        _settings.GoldenRuns.RemoveAll(g => g.Name == run.Name);
        _settings.GoldenRuns.Add(run);
        _settings.ActiveGoldenRun = run.Name;
        // Pair the calibration to the new baseline: re-capturing a recipe re-selects its
        // calibration; a brand-new baseline has none, so estimation turns off rather than
        // mismatching against a stale one.
        AutoPairCalibration(run.Name);
        ApplyGoldenRun(run);
    }

    /// <summary>
    /// Stores a Golden Run built somewhere other than the live capture — today, from recordings
    /// (<see cref="BaselineBuilder"/>). Deliberately re-uses the capture's own result type and
    /// confirmation flow rather than inventing a second one: overwriting a stored run costs the
    /// same thing either way (the ratios that had a baseline there and none here), and two
    /// dialogs for one consequence teach an operator that they mean different things.
    /// </summary>
    /// <returns>A result whose <see cref="GoldenRunCaptureResult.NeedsConfirmation"/> the caller
    /// must answer with <see cref="ConfirmCapturedRun"/>; when it is false the run is already
    /// stored and active.</returns>
    public GoldenRunCaptureResult ImportGoldenRun(GoldenRun run)
    {
        if (run is null) throw new ArgumentNullException(nameof(run));

        GoldenRunCaptureResult result;
        bool stored = false;
        lock (_gate)
        {
            var replaced = _settings.FindGoldenRun(run.Name);
            var have = run.Baselines.Select(b => b.Key).ToHashSet(StringComparer.Ordinal);
            var lost = replaced is null
                ? new List<GoldenRunRatioRejection>()
                : replaced.Baselines.Where(b => !have.Contains(b.Key))
                          .Select(b => Reject(b.Key, "had a baseline in the run being replaced"))
                          .ToList();

            if (lost.Count == 0)
            {
                StoreCapturedRun(run);
                stored = true;
            }

            result = new GoldenRunCaptureResult
            {
                Run = run,
                Accepted = true,
                NeedsConfirmation = lost.Count > 0,
                Lost = lost,
                Replaced = replaced,
                ActiveGoldenRun = _settings.ActiveGoldenRun,
            };
        }

        // Outside the lock, as the capture path does: a handler persists settings.
        if (stored) GoldenRunCaptured?.Invoke(this, run);
        return result;
    }

    /// <summary>
    /// Answers a <see cref="GoldenRunCaptureResult.NeedsConfirmation"/> capture: <paramref
    /// name="keep"/> stores it and makes it active (raising <see cref="GoldenRunCaptured"/> so
    /// the host persists it), anything else discards it — the run it would have replaced, the
    /// active baseline and the paired calibration all stay exactly as they were. Call from the
    /// UI thread once the operator has answered; the run is inert until then.
    /// </summary>
    public void ConfirmCapturedRun(GoldenRun run, bool keep)
    {
        if (run is null || _disposed) return;
        lock (_gate)
        {
            if (!keep)
            {
                _log?.LogSystemEvent(LogSeverity.Warning, "GoldenRunCaptureDiscarded",
                    $"Golden Run “{run.Name}” was discarded by the operator rather than replace " +
                    $"the stored run of the same name. The active baseline is unchanged " +
                    $"({_settings.ActiveGoldenRun ?? "none"}).",
                    related: $"GoldenRun={run.Name}");
                return;
            }
            StoreCapturedRun(run);
        }
        GoldenRunCaptured?.Invoke(this, run);
    }

    /// <summary>
    /// Records where each ratio sat during the capture window relative to the baseline that was
    /// active while it ran — the one about to be replaced. Stated, never judged: after a recipe
    /// change a large deviation is expected, so calling it a warning would cry wolf every time,
    /// while on a re-capture of the same recipe this line is the only record that the state
    /// being frozen as "leak-free" had already moved. Caller holds the lock, and must call this
    /// before <see cref="ApplyGoldenRun"/> swaps the baselines out.
    /// </summary>
    private void LogCaptureDeviation(string runName)
    {
        if (_log is null) return;
        var parts = new List<string>();
        foreach (var mon in _monitors)
        {
            if (!mon.HasBaseline || !_captureAccum.TryGetValue(mon.Key, out var acc) || acc.Count == 0)
                continue;
            var def = _defs[mon.Key];
            // A pedestal value's ratio to its baseline is compressed into a fraction of a
            // percent, so it is reported in σ for the same reason the trend plots it that way.
            string where = def.ValueHasPedestal
                ? (mon.BaselineSigma > 0
                    ? $"{(acc.Mean - mon.BaselineMean) / mon.BaselineSigma:+0.0;-0.0}σ"
                    : "n/a")
                : $"{acc.Mean / mon.BaselineMean * 100.0:0}%";
            parts.Add($"{def.DisplayName}={where}");
        }
        if (parts.Count == 0) return;
        _log.LogSystemEvent(LogSeverity.Information, "GoldenRunCaptureDeviation",
            $"During the “{runName}” capture the ratios sat at these levels relative to the " +
            "baseline that was still active. A recipe change makes a large figure expected; " +
            "re-capturing the same recipe does not — there it says the state being recorded as " +
            "leak-free had already moved.",
            related: $"GoldenRun={runName}", value: string.Join(", ", parts));
    }

    /// <summary>Packages one rejected ratio for <see cref="GoldenRunCaptureResult"/>.</summary>
    private GoldenRunRatioRejection Reject(string key, string reason) => new()
    {
        Key = key,
        DisplayName = _defs.TryGetValue(key, out var d) ? d.DisplayName : key,
        Reason = reason,
    };

    private LeakCalPoint FinalizeCalibrationPoint()
    {
        _calCapturing = false;
        var pt = new LeakCalPoint
        {
            LeakRate = _calLeakRate,
            Label = _calLabel,
            CapturedUtc = DateTime.UtcNow,
        };
        foreach (var mon in _monitors)
        {
            if (_calAccum.TryGetValue(mon.Key, out var acc) && acc.Count > 0)
                pt.Measurements.Add(new RatioCalMeasurement
                {
                    Key = mon.Key,
                    X = acc.Mean,
                    Sigma = acc.StdDev,
                    SampleCount = acc.Count,
                });
        }
        return pt;
    }

    private void ApplyGoldenRun(GoldenRun? run)
    {
        _activeRun = run;
        _floorByRatio.Clear();
        foreach (var mon in _monitors)
        {
            var b = run?.Find(mon.Key);
            var def = _defs[mon.Key];
            // Resolved once here rather than per frame: the reference's measurement key is
            // built from the *corrected* region, so it only changes when _defs or the active
            // run does — which is exactly when this method runs.
            if (run is not null && def.MonitorMode != MonitorMode.AbsoluteIntensity)
                _floorByRatio[mon.Key] = run.FindFloor(def.Denominator.MeasurementKey);
            bool absolute = def.MonitorMode == MonitorMode.AbsoluteIntensity;
            // The baseline is a mean of the monitored quantity, so it only applies while that
            // quantity is still defined the same way: same monitor mode, and — in ratio mode —
            // the same reference line it was divided by. An absolute baseline is a plain line
            // intensity, so swapping the (inert) reference must not invalidate it; but switching
            // between the two modes must, because the numbers aren't comparable.
            bool modeMatches = b is not null && b.Mode == def.MonitorMode;
            bool labelMatches = b is not null && (absolute || b.ReferenceLabel == def.Denominator.Label);
            // An older extraction revision only matters where the units actually changed —
            // integrals became counts·nm. Peak-height and raw baselines are unaffected and keep
            // working, so an upgrade doesn't invalidate more than it has to.
            bool involvesIntegral = def.Numerator.Mode == LineExtractMode.Integral ||
                                    (!absolute && def.Denominator.Mode == LineExtractMode.Integral);
            bool revisionOk = b is not null &&
                              (!involvesIntegral || b.ExtractionRevision >= CurrentExtractionRevision);
            bool usable = b is not null && b.Mean > 0 && modeMatches && labelMatches && revisionOk;
            if (usable)
            {
                mon.SetBaseline(b!.Mean, b.Sigma);
            }
            else
            {
                mon.ClearBaseline();
                // A baseline exists but is being rejected purely on a mode / reference-line
                // mismatch — surface it so it doesn't look like the capture failed.
                if (b is not null && b.Mean > 0 && !modeMatches)
                    _log?.LogSystemEvent(LogSeverity.Information, "LeakMonitorBaselineMismatch",
                        $"Golden Run “{run!.Name}” has a baseline for {def.DisplayName}, but it was " +
                        $"captured in {b.Mode} mode and the ratio is now in {def.MonitorMode} mode — " +
                        "the two measure different quantities, so capture a new Golden Run.",
                        related: $"GoldenRun={run.Name},Ratio={mon.Key}");
                else if (b is not null && b.Mean > 0 && !labelMatches)
                    _log?.LogSystemEvent(LogSeverity.Information, "LeakMonitorBaselineMismatch",
                        $"Golden Run “{run!.Name}” has a baseline for {def.DisplayName}, " +
                        $"but it was captured against reference {b.ReferenceLabel}, not the current " +
                        $"{def.Denominator.Label} — capture a new Golden Run for this reference.",
                        related: $"GoldenRun={run.Name},Ratio={mon.Key}");
                else if (b is not null && b.Mean > 0 && !revisionOk)
                    _log?.LogSystemEvent(LogSeverity.Information, "LeakMonitorBaselineMismatch",
                        $"Golden Run “{run!.Name}” has a baseline for {def.DisplayName} measured " +
                        "with the previous integral extraction (a plain pixel sum). Integrals are " +
                        "now in counts·nm with fractional-pixel window edges, so the stored mean is " +
                        "in a different unit — capture a new Golden Run.",
                        related: $"GoldenRun={run.Name},Ratio={mon.Key}");
            }
        }
        // The active baseline just changed — re-evaluate calibration validity against it.
        BuildEstimator();
    }

    /// <summary>
    /// Stamps the current frame's axis onto the live acquisition fingerprint and compares it
    /// with the one the active Golden Run was captured under. The result is surfaced on every
    /// snapshot (so the panel can show it) and logged once per distinct difference — a
    /// per-frame log would bury everything else, and staying silent is how a re-exposed
    /// spectrometer quietly invalidates a month of baselines. Caller holds the lock.
    /// </summary>
    private void UpdateAcquisitionStatus(float[]? wl)
    {
        if (_acquisition is not null && wl is { Length: > 1 })
        {
            _acquisition.AxisLength = wl.Length;
            _acquisition.AxisStartNm = wl[0];
            _acquisition.AxisEndNm = wl[wl.Length - 1];
        }

        var captured = _activeRun?.Acquisition;
        string diff = captured is null || _acquisition is null
            ? ""                              // nothing to compare against — say nothing
            : _acquisition.Differences(captured);
        _acquisitionWarning = diff.Length == 0
            ? ""
            : $"Acquisition differs from Golden Run “{_activeRun!.Name}”: {diff}";

        if (_acquisitionWarning == _acquisitionWarned) return;
        _acquisitionWarned = _acquisitionWarning;
        if (_acquisitionWarning.Length > 0)
            _log?.LogSystemEvent(LogSeverity.Warning, "LeakMonitorAcquisitionMismatch",
                _acquisitionWarning + ". Absolute-intensity readings scale with these settings, " +
                "so the baseline no longer applies — capture a new Golden Run.",
                related: $"GoldenRun={_activeRun!.Name}", value: diff);
    }

    /// <summary>
    /// Counts brief gate closures that sit between two good frames — the signature of a frame
    /// the spectrometer returned blank, as opposed to the plasma genuinely going off (which
    /// lasts far longer than <see cref="MaxDropoutSeconds"/>). A real run produced twenty such
    /// frames in thirteen minutes; one of them landed inside a Golden Run capture and pushed
    /// that ratio's baseline σ to 2.6× its mean, so the baseline was rejected and nobody could
    /// see why. The gate discards them now, but silently — this is what makes the fault
    /// visible, and what tells you whether changing the acquire mode fixed it. First occurrence
    /// is logged at once, then a running total at most every five minutes. Caller holds the lock.
    /// </summary>
    private void TrackGateDropouts(bool open, DateTime ts)
    {
        if (!open)
        {
            if (_closedRun++ == 0) _closedRunStart = ts;
            return;
        }
        if (_closedRun > 0 && _gateWasOpen &&
            (ts - _closedRunStart).TotalSeconds <= MaxDropoutSeconds)
        {
            _dropoutFrames += _closedRun;
            _dropoutEvents++;
            _dropoutsSinceLog += _closedRun;
            bool first = _lastDropoutLog == default;
            if (first || (ts - _lastDropoutLog).TotalMinutes >= 5)
            {
                _lastDropoutLog = ts;
                _log?.LogSystemEvent(LogSeverity.Warning, "SpectrumFrameDropout",
                    $"The spectrometer returned {_dropoutsSinceLog} blank frame(s) between good " +
                    "ones — the plasma gate discarded them, so no reading was corrupted, but they " +
                    "are an instrument fault, not a process event. If this persists, try " +
                    "AcquireMode = Oneshot in the Configuration tab.",
                    related: $"EventsThisSession={_dropoutEvents},FramesThisSession={_dropoutFrames}",
                    value: $"Since last report={_dropoutsSinceLog}");
                _dropoutsSinceLog = 0;
            }
        }
        _closedRun = 0;
        _gateWasOpen = true;
    }

    /// <summary>
    /// Says once — not once per frame — that the plasma gate could not be evaluated, so every
    /// ratio is running without it (ratio-mode entries fall back to their reference line alone,
    /// which does not reject a blank frame). Silence here is what made a mistyped trigger
    /// wavelength look like a dead leak monitor. Reset by <see cref="ConfigureTrigger"/>, so a
    /// corrected setting reports again if it is still wrong.
    /// </summary>
    private void WarnGateUnusableOnce()
    {
        if (_gateWarned) return;
        _gateWarned = true;
        string why = _plasmaGate is null
            ? "no logger trigger has been configured"
            : !_plasmaGate.IsUsable
                ? $"the trigger is not usable as a gate ({_plasmaGate.Description})"
                : $"the trigger could not be measured on this frame ({_plasmaGate.Description}) — " +
                  "the wavelength is outside the spectrometer axis or its tolerance";
        _log?.LogSystemEvent(LogSeverity.Warning, "LeakMonitorPlasmaGateUnavailable",
            $"Every ratio is running without the plasma-present gate: {why}. Absolute-intensity " +
            "entries are ungated; ratio-mode entries fall back to their reference line, which " +
            "does not reject a frame the spectrometer returns blank. Set the logger's trigger " +
            "wavelength and save-start threshold in the Configuration tab.",
            value: _plasmaGate?.Description ?? "(none)");
    }

    /// <summary>Logs why a ratio ended a Golden Run capture with no usable baseline, and returns
    /// the same reason so the host can show it to the operator without re-deriving it.</summary>
    private string ReportDroppedRatio(string key, string runName)
    {
        var def = _defs[key];
        bool absolute = def.MonitorMode == MonitorMode.AbsoluteIntensity;
        _captureDiag.TryGetValue(key, out var d);
        string reason;
        if (d is { Disabled: false, Frames: > 0 })
        {
            if (d.NumeratorMissing == d.Frames)
                reason = $"the monitored line {def.Numerator.Label} ({def.Numerator.CenterNm:0.#} nm) " +
                         "fell outside the spectrometer wavelength range in every frame";
            else if (d.GateClosed == d.Frames)
                reason = "no frame passed the plasma-present gate — check the logger's trigger " +
                         "wavelength / save-start threshold, which is what gates the leak monitor, " +
                         "and that the plasma was on during the capture";
            else if (absolute && d.LowSnr == 0)
                reason = "no frame produced a usable value while the gate was open — check the " +
                         "logger's trigger threshold and that the plasma was on during the capture";
            else if (!absolute && d.ReferenceMissing == d.Frames)
                reason = $"the reference line {def.Denominator.Label} ({def.Denominator.CenterNm:0.#} nm) " +
                         "never registered — the plasma was off, or the line is outside the spectrum";
            else if (d.LowSnr > 0)
                reason = $"the line(s) stayed below the SNR floor ({def.MinSnr:0.#}) — near the noise " +
                         $"floor — in every usable frame ({d.LowSnr}/{d.Frames}). Raise plasma " +
                         "intensity / exposure, lower Min SNR, or use a stronger line";
            else
                reason = $"no frame had both lines valid ({d.NumeratorMissing}/{d.Frames} frames " +
                         $"missing the numerator, {d.ReferenceMissing}/{d.Frames} missing the reference)";
        }
        else if (d is { Disabled: true } || !def.Enabled)
        {
            reason = "the ratio was disabled for the whole capture";
        }
        else if (d is { OutOfClass: > 0 })
        {
            reason = $"every frame in the capture window ({d.OutOfClass}) ran a process step " +
                     $"other than {def.ProcessClass}, which is the class this ratio measures — " +
                     "capture across a step of that process, or build the baseline offline from " +
                     "recordings that contain one";
        }
        else
        {
            reason = "no spectrum frames were processed during the capture window (plasma off?)";
        }

        _log?.LogSystemEvent(LogSeverity.Warning, "GoldenRunRatioDropped",
            $"Ratio {def.DisplayName} got no baseline from Golden Run “{runName}”: {reason}.",
            related: $"GoldenRun={runName},Ratio={key}");
        return reason;
    }

    private LeakAlarmLevel ComputeOverall()
    {
        int alarm = 0, warn = 0, active = 0;
        foreach (var mon in _monitors)
        {
            switch (mon.State)
            {
                case RatioState.Alarm:   alarm++; active++; break;
                case RatioState.Warning: warn++;  active++; break;
                case RatioState.Normal:           active++; break;
            }
        }
        if (active == 0) return LeakAlarmLevel.Idle;

        int need = _settings.RequireTwoForAlarm ? 2 : 1;
        if (alarm >= need) return LeakAlarmLevel.Alarm;
        if (alarm > 0 || warn > 0) return LeakAlarmLevel.Warning;
        return LeakAlarmLevel.Normal;
    }

    private LeakMonitorSnapshot BuildSnapshot(DateTime ts, bool testMode)
    {
        double progress = 0.0;
        if (_capturing && _captureHasStart && _captureSeconds > 0)
            progress = Math.Clamp(
                (_captureLast - _captureStart).TotalSeconds / _captureSeconds, 0.0, 1.0);

        double calProgress = 0.0;
        if (_calCapturing && _calHasStart && _calSeconds > 0)
            calProgress = Math.Clamp(
                (_calLast - _calStart).TotalSeconds / _calSeconds, 0.0, 1.0);

        var ratios = _monitors.Select(m => m.Snapshot()).ToList();
        LeakRateEstimate? estimate = ComputeLeakRate(ratios);

        return new LeakMonitorSnapshot
        {
            Timestamp = ts,
            Overall = _overall,
            Ratios = ratios,
            TestMode = testMode,
            CaptureActive = _capturing,
            CaptureProgress01 = progress,
            ActiveGoldenRun = _settings.ActiveGoldenRun,
            CalibrationCaptureActive = _calCapturing,
            CalibrationCaptureProgress01 = calProgress,
            CalibrationLeakRate = _calLeakRate,
            LeakRate = estimate,
            ActiveCalibration = _settings.ActiveCalibration,
            CalibrationStatus = _calStatus,
            AcquisitionWarning = _acquisitionWarning,
            PlasmaPresent = _lastGateOpen ?? false,
            PlasmaGateAvailable = _lastGateOpen.HasValue,
            DropoutCount = _dropoutEvents,
            ProcessClass = _stepClass ?? "",
            ProcessClassState = CurrentProcessClassState,
            ProcessStepIndex = _stepIndex,
            ProcessDiscriminants = _stepDiscriminants,
        };
    }

    /// <summary>
    /// Inverts the current per-ratio rises into a fused leak-rate estimate via the active
    /// calibration. Returns null when no calibration is active. In ratio mode each ratio's
    /// rise is the fractional rise (from % -of-baseline) with σ scaled by the baseline mean;
    /// in absolute-intensity mode it is the absolute rise Δ = smoothed − baseMean with σ taken
    /// straight from the EWMA scatter — no division by the near-noise baseline mean, matching
    /// the unit the calibration was fit in.
    /// </summary>
    private LeakRateEstimate? ComputeLeakRate(IReadOnlyList<RatioSnapshot> ratios)
    {
        if (_estimator is null) return null;

        var readings = new List<LeakRateEstimator.RatioReading>(ratios.Count);
        foreach (var r in ratios)
        {
            // A low-SNR ratio still carries a (now-plotted) PercentOfBaseline, but its value is
            // near-noise garbage — keep it out of the leak-rate fit as before. Out of its
            // process class an entry is excluded outright: its smoothing was dropped when the
            // class changed, so what it holds is a reading of a different plasma. It would fall
            // out on the NaN test below anyway; saying so is not the same as relying on it.
            bool usable = r.HasBaseline &&
                          // A trend-only entry is not reproducible enough to carry a threshold,
                          // and a guard measures process drift rather than the leak. Neither
                          // belongs in an inverse-variance fusion that treats every reading as
                          // an independent estimate of the same quantity.
                          r.Role == RatioRole.Alarm &&
                          r.State != RatioState.LowSignal &&
                          r.State != RatioState.NotApplicable &&
                          !double.IsNaN(r.SmoothedRatio) && r.BaselineMean > 0;
            double x, sigX;
            if (!usable)
            {
                x = double.NaN;
                sigX = 0.0;
            }
            else if (r.HasPedestal)
            {
                // Absolute rise Δ and its raw σ (both in intensity units).
                x = r.SmoothedRatio - r.BaselineMean;
                sigX = double.IsNaN(r.RatioNoiseSigma) ? 0.0 : r.RatioNoiseSigma;
            }
            else
            {
                x = r.SmoothedRatio / r.BaselineMean - 1.0;
                sigX = double.IsNaN(r.RatioNoiseSigma) ? 0.0 : r.RatioNoiseSigma / r.BaselineMean;
            }
            readings.Add(new LeakRateEstimator.RatioReading(r.Key, x, sigX));
        }

        return _estimator.Estimate(readings, CurrentReferenceLabels(), CurrentValueUnits());
    }

    private RunningStats GetAccum(string key)
    {
        if (!_captureAccum.TryGetValue(key, out var acc))
            _captureAccum[key] = acc = new RunningStats();
        return acc;
    }

    private RunningStats GetDenomAccum(string referenceKey)
    {
        if (!_captureDenoms.TryGetValue(referenceKey, out var acc))
            _captureDenoms[referenceKey] = acc = new RunningStats();
        return acc;
    }

    private CaptureDiag GetDiag(string key)
    {
        if (!_captureDiag.TryGetValue(key, out var d))
            _captureDiag[key] = d = new CaptureDiag();
        return d;
    }

    private RunningStats GetCalAccum(string key)
    {
        if (!_calAccum.TryGetValue(key, out var acc))
            _calAccum[key] = acc = new RunningStats();
        return acc;
    }

    public void Dispose()
    {
        _disposed = true;
        SampleProcessed = null;
        AlarmStateChanged = null;
        GoldenRunCaptured = null;
        GoldenRunCaptureFinished = null;
        CalibrationPointCaptured = null;
        ConfigurationChanged = null;
        RatiosReloaded = null;
    }

    /// <summary>Per-ratio tally of why frames were or weren't usable during a Golden Run capture.</summary>
    private sealed class CaptureDiag
    {
        /// <summary>Frames seen while this ratio was enabled.</summary>
        public int Frames;
        /// <summary>Frames whose numerator line was outside the spectrum (NaN).</summary>
        public int NumeratorMissing;
        /// <summary>Frames whose reference line was NaN or ≤ 0 (no plasma at that line).</summary>
        public int ReferenceMissing;
        /// <summary>Frames the plasma-present gate rejected — plasma off, or a blank frame.</summary>
        public int GateClosed;
        /// <summary>Frames with plasma + both lines present but below the SNR floor (near noise).</summary>
        public int LowSnr;
        /// <summary>Frames that contributed a sample to the baseline.</summary>
        public int Accepted;
        /// <summary>The ratio was excluded by the operator for the whole capture.</summary>
        public bool Disabled;
        /// <summary>Frames that ran a process class this ratio does not measure. Counted so a
        /// capture spanning the wrong step can say so, instead of reporting "no frames".</summary>
        public int OutOfClass;
    }
}
