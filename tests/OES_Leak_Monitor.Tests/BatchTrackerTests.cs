using System;
using System.Collections.Generic;
using System.Linq;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// Grouping plasma steps into batches, and reducing each step to the one number a batch record
/// keeps.
///
/// <para>What this guards: the cross-batch comparison is the plan's primary detection mechanism,
/// and it only works if two batches are compared at the same point in their own history. The
/// viewport fouls measurably within every batch and recovers when the chamber is cleaned, so the
/// sampling point — the first complete step of a class, in a fixed window after its gate opened
/// — is not a convenience. Get it wrong and the comparison measures the coating.</para>
/// </summary>
public class BatchTrackerTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);

    private static LeakMonitorSnapshot Frame(double t, int step, string cls, bool plasma,
                                             params (string Key, double Value)[] ratios) =>
        new()
        {
            Timestamp = Epoch.AddSeconds(t),
            ProcessStepIndex = step,
            ProcessClass = cls,
            PlasmaPresent = plasma,
            PlasmaGateAvailable = true,
            Ratios = ratios.Select(r => new RatioSnapshot(
                r.Key, r.Key, RatioState.Normal,
                RawRatio: r.Value, SmoothedRatio: r.Value,
                HasBaseline: true, BaselineMean: 1, BaselineSigma: 0.1,
                PercentOfBaseline: 100, WarnThreshold: 2, AlarmThreshold: 3,
                SlopePerMinute: 0, PlasmaPresent: plasma,
                NumeratorIntensity: 1, DenominatorIntensity: 1,
                RatioNoiseSigma: 0, NumeratorSnr: 10, DenominatorSnr: 10,
                Mode: MonitorMode.Ratio, Role: RatioRole.Alarm, HasPedestal: false)).ToList(),
        };

    private static BatchSettings Settings(double gap = 240, string anchor = "",
                                          double anchorSeconds = 120, int minFrames = 5) => new()
    {
        Enabled = true,
        WindowStartSeconds = 10, WindowEndSeconds = 30,
        MinWindowFrames = minFrames,
        BatchGapSeconds = gap,
        BatchStartClass = anchor,
        BatchStartMinDurationSeconds = anchorSeconds,
    };

    /// <summary>
    /// Drives one step: <paramref name="durationSeconds"/> of gate-open frames at 2 s, the
    /// engine's own cadence on this tool, with the class arriving on the third frame the way
    /// the classifier delivers it.
    /// </summary>
    private static double DriveStep(BatchTracker t, double t0, int index, string cls,
                                    double durationSeconds, double value)
    {
        double s = 0;
        for (int f = 0; s <= durationSeconds; f++, s = f * 2.0)
            t.Add(Frame(t0 + s, index, f >= 2 ? cls : "", plasma: true, ("R", value)));
        return t0 + s;   // the moment after the step's last frame
    }

    /// <summary>Plasma off between steps: the engine keeps the old step index and empties the
    /// class, which is exactly what the tracker has to read as "the step is over".</summary>
    private static void DriveGap(BatchTracker t, double from, double to, int lastIndex)
    {
        for (double s = from; s < to; s += 2.0)
            t.Add(Frame(s, lastIndex, "", plasma: false));
    }

    // ---------------------------------------------------------------- the sampling window

    [Fact]
    public void The_window_is_measured_from_the_gate_opening_and_excludes_the_transient()
    {
        var tracker = new BatchTracker(Settings());
        StepSummary? step = null;
        tracker.StepCompleted += (_, s) => step = s;

        // 0–8 s reads 5 (the ignition transient), 10–30 s reads 2, beyond reads 9.
        double s2 = 0;
        for (int f = 0; s2 <= 60; f++, s2 = f * 2.0)
        {
            double v = s2 < 10 ? 5.0 : s2 < 30 ? 2.0 : 9.0;
            tracker.Add(Frame(s2, 1, f >= 2 ? "C" : "", plasma: true, ("R", v)));
        }
        tracker.Add(Frame(s2, 2, "", plasma: true));    // a new step closes the previous one

        Assert.NotNull(step);
        Assert.True(step!.Complete);
        Assert.Equal(2.0, step.Medians["R"], 6);        // only the window's frames counted
        Assert.Equal(10, step.WindowFrames);            // 10–28 s inclusive at 2 s
    }

    [Fact]
    public void A_step_too_short_for_the_window_is_recorded_but_marked_incomplete()
    {
        var tracker = new BatchTracker(Settings(minFrames: 5));
        StepSummary? step = null;
        tracker.StepCompleted += (_, s) => step = s;

        DriveStep(tracker, 0, 1, "A", durationSeconds: 14, value: 3.0);   // only 10–14 s in window
        tracker.Flush();

        Assert.NotNull(step);
        Assert.False(step!.Complete);
        Assert.Equal(3, step.WindowFrames);             // 10, 12, 14
    }

    [Fact]
    public void Gate_closed_frames_do_not_enter_the_reading()
    {
        var tracker = new BatchTracker(Settings());
        StepSummary? step = null;
        tracker.StepCompleted += (_, s) => step = s;

        double s2 = 0;
        for (int f = 0; s2 <= 40; f++, s2 = f * 2.0)
        {
            // One blank frame inside the window carries a wild value; the gate says it is blank.
            bool blank = Math.Abs(s2 - 20.0) < 0.01;
            tracker.Add(Frame(s2, 1, f >= 2 ? "C" : "", plasma: !blank, ("R", blank ? 99.0 : 2.0)));
        }
        tracker.Flush();

        Assert.Equal(2.0, step!.Medians["R"], 6);
        Assert.Equal(9, step.WindowFrames);              // the blank one is not counted
    }

    // ---------------------------------------------------------------- batch boundaries

    [Fact]
    public void A_long_gap_starts_a_new_batch()
    {
        var tracker = new BatchTracker(Settings(gap: 240));
        var batches = new List<BatchSummary>();
        tracker.BatchCompleted += (_, b) => batches.Add(b);

        double t = DriveStep(tracker, 0, 1, "A", 40, 1.0);
        DriveGap(tracker, t, t + 60, 1);                 // 60 s — inside a batch
        t = DriveStep(tracker, t + 60, 2, "C", 84, 2.0);
        DriveGap(tracker, t, t + 400, 2);                // 400 s — a new batch
        t = DriveStep(tracker, t + 400, 3, "A", 40, 3.0);
        tracker.Flush();

        Assert.Equal(2, batches.Count);
        Assert.Equal(2, batches[0].Steps.Count);
        Assert.Single(batches[1].Steps);
    }

    /// <summary>
    /// The anchor rule catches what the gap rule cannot: a batch that opens with the chamber
    /// clean before the previous one's steps have finished draining away. The class alone is not
    /// enough — on the measured tool the same process runs for 156 s at the head of a batch and
    /// for 36 s inside one — so the duration is what separates them.
    /// </summary>
    [Fact]
    public void A_long_step_of_the_anchor_class_starts_a_batch_whatever_the_gap()
    {
        var tracker = new BatchTracker(Settings(gap: 240, anchor: "B", anchorSeconds: 120));
        var batches = new List<BatchSummary>();
        tracker.BatchCompleted += (_, b) => batches.Add(b);

        double t = DriveStep(tracker, 0, 1, "B", 156, 1.0);   // the anchor: opens batch 1
        DriveGap(tracker, t, t + 50, 1);
        t = DriveStep(tracker, t + 50, 2, "A", 40, 2.0);
        DriveGap(tracker, t, t + 50, 2);
        t = DriveStep(tracker, t + 50, 3, "B", 36, 3.0);      // same class, short: still batch 1
        DriveGap(tracker, t, t + 50, 3);
        t = DriveStep(tracker, t + 50, 4, "B", 156, 4.0);     // the anchor again: batch 2
        tracker.Flush();

        Assert.Equal(2, batches.Count);
        Assert.Equal(3, batches[0].Steps.Count);
        Assert.Equal("B", batches[1].Steps[0].Class);
        Assert.True(batches[1].Steps[0].DurationSeconds >= 120);
    }

    // ---------------------------------------------------------------- the sampling point

    /// <summary>
    /// The first complete step of a class, not the best one and not their average. Two steps of
    /// the same process in one batch are taken at different points in the viewport's fouling, so
    /// only the first is comparable with the first of another batch.
    /// </summary>
    [Fact]
    public void The_sampling_point_is_the_first_complete_step_of_its_class()
    {
        var tracker = new BatchTracker(Settings());
        BatchSummary? batch = null;
        tracker.BatchCompleted += (_, b) => batch = b;

        double t = DriveStep(tracker, 0, 1, "C", 14, 9.9);    // too short to be complete
        DriveGap(tracker, t, t + 40, 1);
        t = DriveStep(tracker, t + 40, 2, "C", 84, 2.0);      // the sampling point
        DriveGap(tracker, t, t + 40, 2);
        t = DriveStep(tracker, t + 40, 3, "C", 84, 1.5);      // later in the batch, dimmer
        tracker.Flush();

        Assert.NotNull(batch);
        var first = batch!.FirstStepOf("C");
        Assert.NotNull(first);
        Assert.Equal(2, first!.Index);                        // skipped the incomplete one
        Assert.Equal(2.0, first.Medians["R"], 6);
        Assert.Null(batch.FirstStepOf("A"));                  // a class that never ran
    }

    [Fact]
    public void Class_matching_is_case_insensitive_but_a_missing_class_matches_nothing()
    {
        var tracker = new BatchTracker(Settings());
        BatchSummary? batch = null;
        tracker.BatchCompleted += (_, b) => batch = b;

        DriveStep(tracker, 0, 1, "c", 84, 2.0);
        tracker.Flush();

        Assert.NotNull(batch!.FirstStepOf("C"));
        Assert.Null(batch.FirstStepOf(""));
    }

    // ---------------------------------------------------------------- off by default is not it

    [Fact]
    public void Disabled_records_nothing()
    {
        var s = Settings();
        s.Enabled = false;
        var tracker = new BatchTracker(s);
        var batches = new List<BatchSummary>();
        tracker.BatchCompleted += (_, b) => batches.Add(b);

        DriveStep(tracker, 0, 1, "C", 84, 2.0);
        tracker.Flush();

        Assert.Empty(batches);
    }
}
