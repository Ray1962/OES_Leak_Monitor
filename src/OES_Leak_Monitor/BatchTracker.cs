using System;
using System.Collections.Generic;
using System.Linq;

namespace OES_Leak_Monitor;

/// <summary>
/// How plasma steps are grouped into batches and where in a step the indicator is sampled.
/// Serialized inside <see cref="LeakMonitorSettings"/>.
/// </summary>
public sealed class BatchSettings
{
    /// <summary>
    /// Whether the batch record is kept. On by default: a batch row is about 200 bytes and one
    /// is written every twenty minutes or so, and it is the primary evidence for the whole
    /// cross-batch comparison — the same argument that made the ratio CSV independent of the
    /// intensity logger and always written.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Start of the sampling window, seconds after the step's gate opened.
    ///
    /// <para>Ten seconds because a step's first ten scatter far more than the rest of it: on the
    /// measured recordings the indicator's coefficient of variation over 0–10 s is 8.7 % in the
    /// dimmest process and 1.6 % over 10–30 s. That is the ignition transient, not noise.</para>
    /// </summary>
    public double WindowStartSeconds { get; set; } = 10;

    /// <summary>
    /// End of the sampling window, seconds after the step's gate opened. Thirty, because the
    /// shortest process step on the measured tool is 38 s and the last frames of any recording
    /// are the recorder's stop-confirm tail, where the reference line has collapsed and the
    /// ratio diverges (CV 136–241 % in the tail, against 1.5–3.7 % in this window).
    /// </summary>
    public double WindowEndSeconds { get; set; } = 30;

    /// <summary>
    /// Fewest frames in the window for the step's value to count. Below this the step is
    /// recorded but marked incomplete rather than being given a median of two points — a
    /// missing number is readable, a badly-founded one is not.
    /// </summary>
    public int MinWindowFrames { get; set; } = 5;

    /// <summary>
    /// A gap longer than this between one step ending and the next beginning starts a new batch.
    /// On the measured tool the gaps inside a batch are 14–178 s and the gaps between batches are
    /// 332 s and up, so 240 s separates them with room on both sides.
    /// </summary>
    public double BatchGapSeconds { get; set; } = 240;

    /// <summary>
    /// Optional semantic batch anchor: a step of this class lasting at least
    /// <see cref="BatchStartMinDurationSeconds"/> starts a new batch whatever the gap was.
    ///
    /// <para>It exists because the gap rule is a threshold and the anchor is a fact. On the
    /// measured tool every batch opens with a 156 s chamber clean while the same process also
    /// runs for 36 s inside a batch, so the class alone is not enough and the duration is what
    /// separates them. Empty disables it and the gap rule stands alone.</para>
    /// </summary>
    public string BatchStartClass { get; set; } = "";

    public double BatchStartMinDurationSeconds { get; set; } = 120;

    public BatchSettings Clone() => new()
    {
        Enabled = Enabled,
        WindowStartSeconds = WindowStartSeconds,
        WindowEndSeconds = WindowEndSeconds,
        MinWindowFrames = MinWindowFrames,
        BatchGapSeconds = BatchGapSeconds,
        BatchStartClass = BatchStartClass,
        BatchStartMinDurationSeconds = BatchStartMinDurationSeconds,
    };
}

/// <summary>One plasma step, reduced to what a batch record keeps of it.</summary>
public sealed class StepSummary
{
    /// <summary>The engine's own step counter, so a row can be traced back to the ratio CSV.</summary>
    public int Index { get; init; }

    /// <summary>Process class, <see cref="ProcessClassifier.Unknown"/>, or "" when no classifier
    /// is configured or the step ended before its verdict was taken.</summary>
    public string Class { get; init; } = "";

    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public double DurationSeconds => (End - Start).TotalSeconds;

    /// <summary>Frames the boundary gate counted as part of this step.</summary>
    public int Frames { get; init; }

    /// <summary>Frames that fell inside the sampling window and produced a value.</summary>
    public int WindowFrames { get; init; }

    /// <summary>Whether the window held enough frames for the medians to mean anything.</summary>
    public bool Complete { get; init; }

    /// <summary>Median raw value in the sampling window, per ratio key. A ratio that produced no
    /// usable frame is absent rather than present as NaN.</summary>
    public IReadOnlyDictionary<string, double> Medians { get; init; } =
        new Dictionary<string, double>();

    /// <summary>Each classifier discriminant's value as measured when the verdict was taken.</summary>
    public IReadOnlyList<ProcessDiscriminant> Discriminants { get; init; } =
        Array.Empty<ProcessDiscriminant>();
}

/// <summary>A batch: the run of steps between two batch boundaries.</summary>
public sealed class BatchSummary
{
    public int Index { get; init; }
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public IReadOnlyList<StepSummary> Steps { get; init; } = Array.Empty<StepSummary>();

    /// <summary>
    /// The sampling point: the first complete step of <paramref name="processClass"/> in this
    /// batch, or null if there is none.
    ///
    /// <para>First, not best and not averaged. The viewport fouls measurably within a single
    /// batch — on the measured tool a process step's absolute brightness falls to 0.70 of the
    /// batch's first while the ratio of a near-infrared line to an ultraviolet one moves by a
    /// factor of 1.8 — so the only way to compare batches is to compare them at the same point
    /// in their own history. Immediately after the chamber clean is that point.</para>
    /// </summary>
    public StepSummary? FirstStepOf(string processClass) =>
        Steps.FirstOrDefault(s => s.Complete &&
                                  string.Equals(s.Class, processClass, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Turns the engine's per-frame snapshots into one summary row per batch.
///
/// <para><b>Why a layer at all.</b> The per-frame monitor answers "is it leaking now" against a
/// baseline. The question this chamber actually poses is "is this batch different from the last
/// forty", and that cannot be asked frame by frame: the viewport fouls within every batch and
/// recovers when the chamber is cleaned, so any two frames from different points in a batch are
/// not comparable, while the same point in two batches is. This reduces each batch to the
/// numbers that survive that.</para>
///
/// <para><b>Deliberately independent of <see cref="RatioMonitor"/>.</b> It reads
/// <see cref="LeakMonitorEngine.SampleProcessed"/> and computes a median, using neither the EMA
/// nor the live sigma — which is what makes it immune to the effect measured in
/// <c>docs/leak-monitor-plan-zh-TW.md</c> §9.3, where a step change inflates the live sigma
/// enough to carry its own alarm threshold up with it. Same separation, and for the same reason,
/// as <see cref="RatioCsvLogger"/> being independent of the intensity logger.</para>
///
/// <para><b>A median, not a mean.</b> The spectrometer returns isolated blank frames; the gate
/// discards them but a mean over ten points is still moved by one survivor near the floor. A
/// median over the same ten is not.</para>
///
/// <para>Pure: no files, no clock of its own, no engine reference. <see cref="Add"/> is driven by
/// the snapshot stream and <see cref="Flush"/> closes the batch in progress when acquisition
/// stops. Everything that writes lives in <c>BatchCsvLogger</c>.</para>
/// </summary>
public sealed class BatchTracker
{
    private readonly BatchSettings _settings;

    // The step being accumulated. Null between steps.
    private int _stepIndex = -1;
    private DateTime _stepStart, _stepLast;
    private string _stepClass = "";
    private int _stepFrames;
    private IReadOnlyList<ProcessDiscriminant> _stepDiscriminants = Array.Empty<ProcessDiscriminant>();
    private readonly Dictionary<string, List<double>> _window = new();
    private bool _inStep;

    private readonly List<StepSummary> _batch = new();
    private DateTime _batchStart, _previousStepEnd;
    private int _batchIndex;

    public BatchTracker(BatchSettings settings) =>
        _settings = (settings ?? throw new ArgumentNullException(nameof(settings))).Clone();

    /// <summary>Raised when a batch is complete — on the first step of the next one, or on
    /// <see cref="Flush"/>.</summary>
    public event EventHandler<BatchSummary>? BatchCompleted;

    /// <summary>Raised as each step completes, for a caller that wants the finer record.</summary>
    public event EventHandler<StepSummary>? StepCompleted;

    /// <summary>Steps accumulated into the batch in progress.</summary>
    public int PendingSteps => _batch.Count;

    public void Add(LeakMonitorSnapshot snapshot)
    {
        if (snapshot is null || !_settings.Enabled) return;

        // A step ends when the engine's counter moves on. The counter only advances when a new
        // step opens, so the frames between two steps carry the old index with an empty class —
        // which is why the step's end is the last frame that was actually part of it, not the
        // frame on which we noticed.
        if (snapshot.ProcessStepIndex != _stepIndex)
        {
            CloseStep();
            _stepIndex = snapshot.ProcessStepIndex;
            _inStep = _stepIndex > 0;
            _stepStart = _stepLast = snapshot.Timestamp;
            _stepClass = "";
            _stepFrames = 0;
            _stepDiscriminants = Array.Empty<ProcessDiscriminant>();
            _window.Clear();
        }

        if (!_inStep) return;

        // The class arrives a few frames into the step, once the verdict has been taken; an
        // empty value afterwards means the step has ended, not that it was declassified.
        if (!string.IsNullOrEmpty(snapshot.ProcessClass))
        {
            _stepClass = snapshot.ProcessClass;
            if (snapshot.ProcessDiscriminants.Count > 0)
                _stepDiscriminants = snapshot.ProcessDiscriminants;
        }

        if (!snapshot.PlasmaPresent) return;   // gate-closed frames are not part of the reading
        _stepFrames++;
        _stepLast = snapshot.Timestamp;

        double t = (snapshot.Timestamp - _stepStart).TotalSeconds;
        if (t < _settings.WindowStartSeconds || t >= _settings.WindowEndSeconds) return;

        foreach (var r in snapshot.Ratios)
        {
            // The raw ratio, not the smoothed one: the median is the smoothing, and layering an
            // EMA underneath it would carry state across the window's edges.
            if (double.IsNaN(r.RawRatio) || double.IsInfinity(r.RawRatio)) continue;
            if (!_window.TryGetValue(r.Key, out var list))
                _window[r.Key] = list = new List<double>();
            list.Add(r.RawRatio);
        }
    }

    /// <summary>Closes the batch in progress. Call when acquisition stops.</summary>
    public void Flush()
    {
        CloseStep();
        EmitBatch();
    }

    private void CloseStep()
    {
        if (!_inStep) return;
        _inStep = false;
        if (_stepFrames == 0) return;

        var medians = new Dictionary<string, double>(StringComparer.Ordinal);
        int windowFrames = 0;
        foreach (var (key, values) in _window)
        {
            if (values.Count == 0) continue;
            windowFrames = Math.Max(windowFrames, values.Count);
            values.Sort();
            medians[key] = values.Count % 2 == 1
                ? values[values.Count / 2]
                : 0.5 * (values[values.Count / 2 - 1] + values[values.Count / 2]);
        }

        var step = new StepSummary
        {
            Index = _stepIndex,
            Class = _stepClass,
            Start = _stepStart,
            End = _stepLast,
            Frames = _stepFrames,
            WindowFrames = windowFrames,
            Complete = windowFrames >= _settings.MinWindowFrames,
            Medians = medians,
            Discriminants = _stepDiscriminants,
        };

        if (StartsNewBatch(step)) EmitBatch();
        if (_batch.Count == 0) _batchStart = step.Start;
        _batch.Add(step);
        _previousStepEnd = step.End;
        StepCompleted?.Invoke(this, step);
    }

    /// <summary>
    /// Whether <paramref name="step"/> opens a new batch. Evaluated when the step <em>ends</em>,
    /// because the anchor rule needs its duration — a step is assigned to a batch retrospectively
    /// and nothing downstream sees a batch until it is complete anyway.
    /// </summary>
    private bool StartsNewBatch(StepSummary step)
    {
        if (_batch.Count == 0) return false;
        if ((step.Start - _previousStepEnd).TotalSeconds > _settings.BatchGapSeconds) return true;
        return !string.IsNullOrWhiteSpace(_settings.BatchStartClass) &&
               string.Equals(step.Class, _settings.BatchStartClass, StringComparison.OrdinalIgnoreCase) &&
               step.DurationSeconds >= _settings.BatchStartMinDurationSeconds;
    }

    private void EmitBatch()
    {
        if (_batch.Count == 0) return;
        var summary = new BatchSummary
        {
            Index = ++_batchIndex,
            Start = _batchStart,
            End = _batch[^1].End,
            Steps = _batch.ToList(),
        };
        _batch.Clear();
        BatchCompleted?.Invoke(this, summary);
    }
}
