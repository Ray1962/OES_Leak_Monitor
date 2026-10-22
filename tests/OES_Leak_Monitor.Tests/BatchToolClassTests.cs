using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// The batch layer's step class once the main tool's events decide it (#32).
///
/// <para>What this guards: with tool events the snapshot's class follows the process window frame
/// by frame, so it can change inside one plasma step — a window that opens after ignition, or an
/// edge that falls part-way through. The batch record samples one window of each step
/// (gate-open + 10–30 s), and a median over frames from two different processes is a number no
/// batch could be compared on. So the step's class is the class in force throughout that sampling
/// window, and a step whose window saw two is sampled for none — said in the log, not guessed.
/// With tool events off the classifier locks its verdict per step, and the record must not move.</para>
/// </summary>
public class BatchToolClassTests : IDisposable
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oeslm-batchcls-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static LeakMonitorSnapshot Frame(double t, int step, string cls, bool plasma, double value) =>
        new()
        {
            Timestamp = Epoch.AddSeconds(t),
            ProcessStepIndex = step,
            ProcessClass = cls,
            PlasmaPresent = plasma,
            PlasmaGateAvailable = true,
            Ratios = new[]
            {
                new RatioSnapshot(
                    "R", "R", RatioState.Normal,
                    RawRatio: value, SmoothedRatio: value,
                    HasBaseline: true, BaselineMean: 1, BaselineSigma: 0.1,
                    PercentOfBaseline: 100, WarnThreshold: 2, AlarmThreshold: 3,
                    SlopePerMinute: 0, PlasmaPresent: plasma,
                    NumeratorIntensity: 1, DenominatorIntensity: 1,
                    RatioNoiseSigma: 0, NumeratorSnr: 10, DenominatorSnr: 10,
                    Mode: MonitorMode.Ratio, Role: RatioRole.Alarm, HasPedestal: false),
            },
        };

    private static BatchSettings Settings(string anchor = "", double anchorSeconds = 120) => new()
    {
        Enabled = true,
        WindowStartSeconds = 10, WindowEndSeconds = 30,
        MinWindowFrames = 5,
        BatchGapSeconds = 240,
        BatchStartClass = anchor,
        BatchStartMinDurationSeconds = anchorSeconds,
    };

    /// <summary>One step of gate-open frames at 2 s, the class at each frame given by
    /// <paramref name="classAt"/> (seconds since the step's first frame).</summary>
    private static double Step(BatchTracker t, double t0, int index, Func<double, string> classAt,
                               double seconds, double value)
    {
        double s = 0;
        for (int f = 0; s <= seconds; f++, s = f * 2.0)
            t.Add(Frame(t0 + s, index, classAt(s), plasma: true, value));
        return t0 + s;
    }

    private static void Gap(BatchTracker t, double from, double to, int lastIndex)
    {
        for (double s = from; s < to; s += 2.0) t.Add(Frame(s, lastIndex, "", plasma: false, 0));
    }

    private const string Unknown = ProcessClassifier.Unknown;

    // ---------------------------------------------------------------- window edges

    /// <summary>
    /// The tool's ProcessStart arrives a few seconds after the plasma lit — the step's first frames
    /// read Unknown, everything from the sampling window on reads A. That is an A step, sampled.
    /// </summary>
    [Fact]
    public void AWindowOpeningBeforeTheSamplingWindow_GivesTheStepItsClass()
    {
        var tracker = new BatchTracker(Settings());
        BatchSummary? batch = null;
        tracker.BatchCompleted += (_, b) => batch = b;

        Step(tracker, 0, 1, s => s < 6 ? Unknown : "A", 40, 2.0);
        tracker.Flush();

        var step = Assert.Single(batch!.Steps);
        Assert.Equal("A", step.Class);
        Assert.True(step.Complete);
        Assert.Equal(2.0, batch.FirstStepOf("A")!.Medians["R"], 6);
    }

    /// <summary>
    /// The ProcessEnd falls at 20 s, inside the sampling window: half the window is A, half is a
    /// plasma no window covers. Its median would mix two processes, so the step is sampled for no
    /// class — and the tracker says which step and what it saw, once.
    /// </summary>
    [Fact]
    public void AnEdgeInsideTheSamplingWindow_SamplesTheStepForNoClass_AndSaysSo()
    {
        var tracker = new BatchTracker(Settings());
        BatchSummary? batch = null;
        var ambiguous = new List<StepClassAmbiguity>();
        tracker.BatchCompleted += (_, b) => batch = b;
        tracker.StepClassAmbiguous += (_, e) => ambiguous.Add(e);

        Step(tracker, 0, 7, s => s < 20 ? "A" : Unknown, 40, 2.0);
        tracker.Flush();

        var step = Assert.Single(batch!.Steps);
        Assert.Equal("", step.Class);
        Assert.Null(batch.FirstStepOf("A"));
        Assert.Null(batch.FirstStepOf(Unknown));
        var e = Assert.Single(ambiguous);
        Assert.Equal(7, e.StepIndex);
        Assert.Equal(new[] { "A", Unknown }, e.Classes);
    }

    /// <summary>A step no window covered at all — the unmapped process — is sampled for none of
    /// the configured classes, and that is not an ambiguity: it is one class, just not a real one.</summary>
    [Fact]
    public void AnUnknownStep_IsSampledForNoConfiguredClass()
    {
        var tracker = new BatchTracker(Settings());
        BatchSummary? batch = null;
        var ambiguous = new List<StepClassAmbiguity>();
        tracker.BatchCompleted += (_, b) => batch = b;
        tracker.StepClassAmbiguous += (_, e) => ambiguous.Add(e);

        Step(tracker, 0, 1, _ => Unknown, 84, 2.0);
        tracker.Flush();

        Assert.Null(batch!.FirstStepOf("A"));
        Assert.Null(batch.FirstStepOf("B"));
        Assert.Null(batch.FirstStepOf("C"));
        Assert.Empty(ambiguous);
    }

    // ---------------------------------------------------------------- batch boundaries

    /// <summary>
    /// The anchor rule reads the same class. A long Clean (B) window opens a batch; a long step
    /// that changed process inside its sampling window does not — the anchor is meant to be a fact,
    /// and a step whose process is in doubt is not one.
    /// </summary>
    [Fact]
    public void TheAnchorRule_UsesTheEventClass_AndAnAmbiguousStepNeverAnchors()
    {
        var tracker = new BatchTracker(Settings(anchor: "B", anchorSeconds: 120));
        var batches = new List<BatchSummary>();
        tracker.BatchCompleted += (_, b) => batches.Add(b);

        double t = Step(tracker, 0, 1, _ => "B", 156, 1.0);               // opens batch 1
        Gap(tracker, t, t + 50, 1);
        t = Step(tracker, t + 50, 2, _ => "A", 40, 2.0);
        Gap(tracker, t, t + 50, 2);
        // Long, and B at its end, but A for the first half of its sampling window: not an anchor.
        t = Step(tracker, t + 50, 3, s => s < 20 ? "A" : "B", 156, 3.0);
        Gap(tracker, t, t + 50, 3);
        t = Step(tracker, t + 50, 4, _ => "B", 156, 4.0);                 // a real anchor: batch 2
        tracker.Flush();

        Assert.Equal(2, batches.Count);
        Assert.Equal(new[] { 1, 2, 3 }, batches[0].Steps.Select(s => s.Index));
        Assert.Equal("B", batches[1].Steps[0].Class);
    }

    // ---------------------------------------------------------------- tool events off

    /// <summary>
    /// Classifier-driven steps, the way they arrive without tool events: no class for the first
    /// frames, then the verdict, locked. At a slow frame rate the verdict can land inside the
    /// sampling window, so its first frames carry no class — "not decided yet", not a second
    /// process. The record is exactly what it was before tool events existed.
    /// </summary>
    [Fact]
    public void WithoutToolEvents_AClassifierRunIsRecordedAsBefore()
    {
        var tracker = new BatchTracker(Settings(anchor: "B", anchorSeconds: 120));
        var batches = new List<BatchSummary>();
        var ambiguous = new List<StepClassAmbiguity>();
        tracker.BatchCompleted += (_, b) => batches.Add(b);
        tracker.StepClassAmbiguous += (_, e) => ambiguous.Add(e);

        double t = Step(tracker, 0, 1, s => s < 4 ? "" : "B", 156, 1.0);
        Gap(tracker, t, t + 50, 1);
        t = Step(tracker, t + 50, 2, s => s < 14 ? "" : "A", 40, 2.0);    // verdict inside the window
        Gap(tracker, t, t + 50, 2);
        t = Step(tracker, t + 50, 3, _ => "", 40, 5.0);                  // no classifier at all
        Gap(tracker, t, t + 50, 3);
        Step(tracker, t + 50, 4, s => s < 4 ? "" : "B", 156, 4.0);
        tracker.Flush();

        Assert.Empty(ambiguous);
        Assert.Equal(2, batches.Count);
        Assert.Equal(new[] { "B", "A", "" }, batches[0].Steps.Select(s => s.Class));
        Assert.Equal(2.0, batches[0].FirstStepOf("A")!.Medians["R"], 6);
        Assert.Equal(1.0, batches[0].FirstStepOf("B")!.Medians["R"], 6);
    }

    // ---------------------------------------------------------------- the log

    /// <summary>The batch logger puts the ambiguity in the system log, so whoever reads a batch row
    /// with a blank column can find out why.</summary>
    [Fact]
    public void TheBatchLogger_LogsAnAmbiguousStep()
    {
        var settings = new LeakMonitorSettings { Enabled = true, Batch = Settings() };
        using var engine = new LeakMonitorEngine(settings);
        var tracker = new BatchTracker(settings.Batch);
        using var log = new SystemLogger(Path.Combine(_root, "Logs"));
        using var logger = new BatchCsvLogger(engine, _root, tracker, log);
        logger.Configure(new LoggerSettings { BaseDirectory = _root, FilePrefix = "P" });

        Step(tracker, 0, 7, s => s < 20 ? "A" : Unknown, 40, 2.0);
        tracker.Flush();

        var entry = Assert.Single(log.Snapshot(), e => e.Condition == "BatchStepClassAmbiguous");
        Assert.Contains("7", entry.Description + entry.RelatedParameters);
        Assert.Contains("A", entry.Description + entry.RelatedParameters);
    }
}
