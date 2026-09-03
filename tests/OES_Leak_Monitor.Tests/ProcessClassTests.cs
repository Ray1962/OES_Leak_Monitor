using System;
using System.Collections.Generic;
using System.Linq;
using Aqst.OesSpectrometer.Models;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// Per-step process classification and class-scoped ratios.
///
/// <para>What this guards: the measured chamber interleaves three processes on a cycle of about
/// three minutes, and the leak monitor holds one active Golden Run. Judging every step against
/// one baseline compares three different plasmas to whichever of them happened to be captured —
/// on that tool the same pair of lines reads levels ten times apart between processes, and the
/// argon reference the factory ratio set divides by exists in only one of the three.</para>
///
/// <para>The synthetic spectra here mirror that shape: one process carries a strong Ar 750.4
/// line, the other two carry none and differ from each other in Hα 656.3 — which is the pair of
/// discriminants the real recordings selected (see
/// <c>docs/process-classification-20260820-21-zh-TW.html</c>).</para>
/// </summary>
public class ProcessClassTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);

    private const double Ar750 = 750.4, O777 = 777.4, Ha656 = 656.3, Sig337 = 337.1;

    /// <summary>A flat continuum with four lines placed on it at the levels asked for.</summary>
    private static SpectrumSample Frame(double seconds,
        double ar750, double o777, double ha656, double sig337, double continuum = 10)
    {
        const int n = 1100;
        var wl = new float[n];
        var inten = new float[n];
        for (int i = 0; i < n; i++)
        {
            wl[i] = (float)(300.0 + i * 0.5);
            // A hair of structure so a captured baseline has a non-zero sigma; without one the
            // Warn threshold sits exactly on the mean and any rise at all trips it.
            inten[i] = (float)(continuum + (i % 2 == 0 ? 1 : -1));
            // Each line is a plateau wider than the extraction window, so a RawMean over
            // +/-0.5 nm reads the level exactly. ExtractRaw weights the window's fractional
            // edges, and a single-bin line would blend continuum into every expected value.
            if (Math.Abs(wl[i] - Ar750) <= 1.5) inten[i] = (float)ar750;
            if (Math.Abs(wl[i] - O777) <= 1.5) inten[i] = (float)o777;
            if (Math.Abs(wl[i] - Ha656) <= 1.5) inten[i] = (float)ha656;
            if (Math.Abs(wl[i] - Sig337) <= 1.5) inten[i] = (float)sig337;
        }
        return new SpectrumSample
        {
            Timestamp = Epoch.AddSeconds(seconds),
            Wavelengths = wl,
            Intensities = inten,
            IntegrationTime = 0,
            AverageCount = 0,
            SerialNumber = "TEST",
            IsTestMode = true,
        };
    }

    // The three processes, named as the real chamber's are. C is the one with argon; A and B
    // have none and are separated by hydrogen. Absolute levels differ between them the way the
    // real ones do, so the per-class plasma threshold is actually exercised.
    private static SpectrumSample StepC(double t, double signal = 500) =>
        Frame(t, ar750: 1400, o777: 1000, ha656: 125, sig337: signal);
    private static SpectrumSample StepA(double t, double signal = 500) =>
        Frame(t, ar750: 10, o777: 300, ha656: 15, sig337: signal);
    private static SpectrumSample StepB(double t, double signal = 500) =>
        Frame(t, ar750: 10, o777: 800, ha656: 100, sig337: signal);
    /// <summary>Plasma off — every line at the continuum, so no gate anywhere opens.</summary>
    private static SpectrumSample Off(double t) =>
        Frame(t, ar750: 10, o777: 10, ha656: 10, sig337: 10);

    private static LineRegion Raw(string label, double nm) => new()
    {
        Label = label, CenterNm = nm, HalfWidthNm = 0.5,
        BaselineGapNm = 1.0, BaselineWidthNm = 1.0,
        Mode = LineExtractMode.RawMean,
    };

    private static RatioDefinition Ratio(string key, string processClass) => new()
    {
        Key = key,
        DisplayName = key,
        Enabled = true,
        ProcessClass = processClass,
        MonitorMode = MonitorMode.AbsoluteIntensity,
        MinSnr = 0,
        SigmaWarn = 3, SigmaAlarm = 6,
        EmaTauSeconds = 0.1, ConfirmSeconds = 1.0,
        Numerator = Raw("Sig 337", Sig337),
        Denominator = Raw("Ar 750", Ar750),
    };

    /// <summary>
    /// The rules the real recordings selected, with the same thresholds: argon over oxygen names
    /// the argon-carrying process, hydrogen over oxygen separates the other two.
    /// </summary>
    private static ProcessClassifierSettings Classifier(
        double thresholdA = 0, double thresholdB = 0, double thresholdC = 0,
        int decideAfter = 3) => new()
    {
        Enabled = true,
        DecideAfterFrames = decideAfter,
        FallbackClass = "B",
        Rules =
        {
            new ProcessClassRule
            {
                ClassName = "C", DisplayName = "Ar750/O777",
                Numerator = Raw("Ar 750", Ar750), Denominator = Raw("O 777", O777),
                Op = ComparisonOp.GreaterThan, Threshold = 0.5,
            },
            new ProcessClassRule
            {
                ClassName = "A", DisplayName = "Ha656/O777",
                Numerator = Raw("Ha 656", Ha656), Denominator = Raw("O 777", O777),
                Op = ComparisonOp.LessThan, Threshold = 0.07,
            },
        },
        Classes =
        {
            new ProcessClassDefinition { Name = "A", PlasmaThreshold = thresholdA },
            new ProcessClassDefinition { Name = "B", PlasmaThreshold = thresholdB },
            new ProcessClassDefinition { Name = "C", PlasmaThreshold = thresholdC },
        },
    };

    private sealed class Harness : IDisposable
    {
        public LeakMonitorEngine Engine { get; }
        public List<LeakMonitorSnapshot> Snaps { get; } = new();
        public List<LeakAlarmLevel> Levels { get; } = new();

        public Harness(ProcessClassifierSettings? classifier, params RatioDefinition[] ratios)
        {
            var settings = new LeakMonitorSettings
            {
                Enabled = true,
                SuppressAlarmsInTestMode = false,
                RequireTwoForAlarm = false,
            };
            foreach (var r in ratios) settings.Ratios.Add(r);
            if (classifier is not null) settings.ProcessClassifier = classifier;

            Engine = new LeakMonitorEngine(settings);
            // The trigger metric is the intensity at 777 nm, which each step sets directly, so
            // "how bright is this frame" is a number the test controls rather than infers.
            Engine.ConfigureTrigger(new LoggerSettings
            {
                TriggerMode = TriggerMode.Wavelength,
                TriggerWavelength = (float)O777,
                WavelengthToleranceNm = 1f,
                SaveStartThresholdIntensity = 500,
            });
            Engine.SampleProcessed += (_, s) => Snaps.Add(s);
            Engine.AlarmStateChanged += (_, e) => Levels.Add(e.NewLevel);
        }

        public LeakMonitorSnapshot Last => Snaps[^1];
        public RatioSnapshot Ratio(string key) => Last.Ratios.First(r => r.Key == key);
        public void Dispose() => Engine.Dispose();
    }

    // ---------------------------------------------------------------- unconfigured

    /// <summary>
    /// With no classifier the engine behaves exactly as it did before class scoping existed:
    /// every ratio judges every step, and the snapshot names no class. This is the property that
    /// makes the feature safe to ship to installations that will never configure it.
    /// </summary>
    [Fact]
    public void NoClassifier_EveryRatioJudgesEveryStep()
    {
        using var h = new Harness(classifier: null, Ratio("R_any", processClass: ""));
        double t = 0;
        h.Engine.BeginGoldenRunCapture("base", seconds: 2);
        for (; t <= 3.0; t += 0.5) h.Engine.ProcessSample(StepC(t));

        Assert.Equal("", h.Last.ProcessClass);
        Assert.Empty(h.Last.ProcessDiscriminants);
        // The step itself is still tracked — a plasma step is a fact about the tool, and the
        // batch layer needs its boundaries whether or not anything named the process.
        Assert.Equal(1, h.Last.ProcessStepIndex);
        Assert.Equal(RatioState.Normal, h.Ratio("R_any").State);
    }

    // ---------------------------------------------------------------- classification

    [Theory]
    [InlineData("C")]
    [InlineData("A")]
    [InlineData("B")]
    public void EachProcess_IsNamedFromItsSpectrum(string expected)
    {
        using var h = new Harness(Classifier(thresholdA: 200), Ratio("R_any", ""));
        Func<double, SpectrumSample> step = expected switch
        {
            "C" => t => StepC(t),
            "A" => t => StepA(t),
            _   => t => StepB(t),
        };
        for (double t = 0; t <= 3.0; t += 0.5) h.Engine.ProcessSample(step(t));

        Assert.Equal(expected, h.Last.ProcessClass);
        Assert.Equal(expected, h.Engine.CurrentProcessClass);
    }

    /// <summary>
    /// The verdict is not taken during the ignition transient, and once taken it is locked for
    /// the rest of the step. A plasma step does not change process half way through, so letting
    /// the answer move could only let transient noise flip it.
    /// </summary>
    [Fact]
    public void Verdict_IsDeferredThenLocked()
    {
        using var h = new Harness(Classifier(thresholdA: 200, decideAfter: 3), Ratio("R_any", ""));

        // Frames 1 and 2 are inside the transient: no verdict yet.
        h.Engine.ProcessSample(StepC(0.0));
        Assert.Equal("", h.Snaps[^1].ProcessClass);
        h.Engine.ProcessSample(StepC(0.5));
        Assert.Equal("", h.Snaps[^1].ProcessClass);

        // Frame 3 decides.
        h.Engine.ProcessSample(StepC(1.0));
        Assert.Equal("C", h.Snaps[^1].ProcessClass);

        // Later frames that look like a different process do not move it — the step is locked.
        for (double t = 1.5; t <= 4.0; t += 0.5) h.Engine.ProcessSample(StepB(t));
        Assert.Equal("C", h.Snaps[^1].ProcessClass);

        // Plasma off ends the step; the next step is classified afresh.
        for (double t = 4.5; t <= 6.0; t += 0.5) h.Engine.ProcessSample(Off(t));
        for (double t = 6.5; t <= 9.0; t += 0.5) h.Engine.ProcessSample(StepB(t));
        Assert.Equal("B", h.Snaps[^1].ProcessClass);
    }

    /// <summary>Every rule's measured value is recorded, including for the step that matched no
    /// rule and fell through to the fallback — the verdict alone cannot say whether a threshold
    /// still fits the chamber.</summary>
    [Fact]
    public void Discriminants_AreRecordedEvenForTheFallbackClass()
    {
        using var h = new Harness(Classifier(), Ratio("R_any", ""));
        for (double t = 0; t <= 3.0; t += 0.5) h.Engine.ProcessSample(StepB(t));

        Assert.Equal("B", h.Last.ProcessClass);                       // fallback, no rule matched
        var disc = h.Last.ProcessDiscriminants;
        Assert.Equal(2, disc.Count);
        Assert.Equal("Ar750/O777", disc[0].Label);
        Assert.Equal(10.0 / 800.0, disc[0].Value, 6);                 // below its 0.5 threshold
        Assert.Equal("Ha656/O777", disc[1].Label);
        Assert.Equal(100.0 / 800.0, disc[1].Value, 6);                // above its 0.07 threshold
    }

    // ---------------------------------------------------------------- class scoping

    /// <summary>
    /// Out of its class a ratio reads <see cref="RatioState.NotApplicable"/> — not
    /// <see cref="RatioState.NoPlasma"/> (a fault or an idle tool) and not
    /// <see cref="RatioState.Disabled"/> (an operator decision). On screen those three call for
    /// three different reactions, and an entry stuck in the wrong one is how an operator learns
    /// to ignore the panel.
    /// </summary>
    [Fact]
    public void OutOfClass_ReadsNotApplicable_AndIsHeldOutOfTheComposite()
    {
        using var h = new Harness(Classifier(thresholdA: 200),
            Ratio("R_C", processClass: "C"), Ratio("R_A", processClass: "A"));

        double t = 0;
        for (; t <= 3.0; t += 0.5) h.Engine.ProcessSample(StepC(t));
        Assert.Equal("C", h.Last.ProcessClass);
        Assert.Equal(RatioState.NoBaseline, h.Ratio("R_C").State);       // in class, no baseline yet
        Assert.Equal(RatioState.NotApplicable, h.Ratio("R_A").State);    // out of class

        // Nothing is judged, so the composite is Idle rather than any alarm level.
        Assert.Equal(LeakAlarmLevel.Idle, h.Last.Overall);

        for (; t <= 6.0; t += 0.5) h.Engine.ProcessSample(Off(t));
        for (; t <= 9.5; t += 0.5) h.Engine.ProcessSample(StepA(t));
        Assert.Equal("A", h.Last.ProcessClass);
        Assert.Equal(RatioState.NotApplicable, h.Ratio("R_C").State);
        Assert.Equal(RatioState.NoBaseline, h.Ratio("R_A").State);
    }

    /// <summary>
    /// A step no rule could name judges nothing. "We cannot tell which process this is" is not
    /// "it is process C", and guessing produces an alarm nobody can attribute — the same rule
    /// the plasma gate follows when it cannot be evaluated.
    /// </summary>
    [Fact]
    public void UnknownStep_JudgesNothing()
    {
        var cfg = Classifier(thresholdA: 200);
        cfg.FallbackClass = "";        // no fallback, so an unmatched step is Unknown
        using var h = new Harness(cfg, Ratio("R_C", "C"), Ratio("R_any", ""));

        for (double t = 0; t <= 3.0; t += 0.5) h.Engine.ProcessSample(StepB(t));

        Assert.Equal(ProcessClassifier.Unknown, h.Last.ProcessClass);
        Assert.Equal(RatioState.NotApplicable, h.Ratio("R_C").State);
        // An entry with no class still applies — it made no claim about which process it needs.
        Assert.NotEqual(RatioState.NotApplicable, h.Ratio("R_any").State);
    }

    /// <summary>
    /// An isolated blank frame does not end the step.
    ///
    /// <para>The spectrometer returns them — 20 in 13 minutes in one measured run, which is what
    /// <c>SpectrumFrameDropout</c> counts. Ending the step on the first one would restart the
    /// classification mid-step, take the verdict again on whatever frames happened to follow, and
    /// split one process step into several for everything downstream. A closure longer than the
    /// dropout threshold is the plasma genuinely going off and does end it.</para>
    /// </summary>
    [Fact]
    public void AnIsolatedBlankFrame_DoesNotEndTheStep()
    {
        using var h = new Harness(Classifier(thresholdA: 200), Ratio("R_C", processClass: "C"));

        double t = 0;
        for (; t <= 3.0; t += 0.5) h.Engine.ProcessSample(StepC(t));
        int step = h.Last.ProcessStepIndex;
        Assert.Equal("C", h.Last.ProcessClass);

        // One blank frame in the middle, then the plasma is back.
        h.Engine.ProcessSample(Off(t)); t += 0.5;
        for (; t <= 6.0; t += 0.5) h.Engine.ProcessSample(StepC(t));
        Assert.Equal(step, h.Last.ProcessStepIndex);      // same step, not a new one
        Assert.Equal("C", h.Last.ProcessClass);

        // A sustained closure is the plasma going off, and does end it.
        for (; t <= 9.0; t += 0.5) h.Engine.ProcessSample(Off(t));
        for (; t <= 12.0; t += 0.5) h.Engine.ProcessSample(StepC(t));
        Assert.True(h.Last.ProcessStepIndex > step, "a sustained closure should have ended the step");
    }

    // ---------------------------------------------------------------- the audit rule

    /// <summary>
    /// A confirmed leak does not end because the tool moved on to the next process step. The
    /// latch survives leaving its class and the composite still reads Alarm — the operator has
    /// to Acknowledge, which is the one path that writes an audit entry.
    /// </summary>
    [Fact]
    public void LatchedAlarm_SurvivesLeavingItsClass()
    {
        using var h = new Harness(Classifier(thresholdA: 200), Ratio("R_C", processClass: "C"));

        double t = 0;
        h.Engine.BeginGoldenRunCapture("base", seconds: 2);
        for (; t <= 3.0; t += 0.5) h.Engine.ProcessSample(StepC(t, signal: 500));
        for (; t <= 12.0; t += 0.5) h.Engine.ProcessSample(StepC(t, signal: 2000));
        Assert.Equal(LeakAlarmLevel.Alarm, h.Last.Overall);

        // The tool moves to another process. The ratio is out of class, but it is latched.
        for (; t <= 15.0; t += 0.5) h.Engine.ProcessSample(Off(t));
        for (; t <= 19.0; t += 0.5) h.Engine.ProcessSample(StepA(t));
        Assert.Equal("A", h.Last.ProcessClass);
        Assert.Equal(RatioState.Alarm, h.Ratio("R_C").State);
        Assert.Equal(LeakAlarmLevel.Alarm, h.Last.Overall);

        // Only an acknowledgement ends it. Acknowledge does not itself emit a snapshot — the
        // panel sees the change on the next frame — so read it from one.
        h.Engine.Acknowledge("tester");
        h.Engine.ProcessSample(StepA(t));
        Assert.Equal(RatioState.NotApplicable, h.Ratio("R_C").State);
        Assert.Equal(LeakAlarmLevel.Idle, h.Last.Overall);
    }

    // ---------------------------------------------------------------- the gate

    /// <summary>
    /// A dim process's step is still detected when a brighter process's threshold is higher.
    ///
    /// <para>This is the failure recorded in <c>docs/leak-test-20260819-analysis.md</c> reached
    /// from the other direction: a save threshold set above the leak-free plasma left the gate
    /// shut and nothing was evaluated at all. Here the dim process reads 300 on the trigger
    /// metric against a global threshold of 500 — with one threshold it would never be seen, so
    /// it could never be classified, so it would stay dark for ever.</para>
    /// </summary>
    [Fact]
    public void PerClassThreshold_LetsADimProcessBeSeenAtAll()
    {
        // Without a per-class threshold: the dim step never opens the gate.
        using (var blind = new Harness(Classifier(), Ratio("R_A", processClass: "A")))
        {
            for (double t = 0; t <= 3.0; t += 0.5) blind.Engine.ProcessSample(StepA(t));
            Assert.Equal("", blind.Last.ProcessClass);          // no step ever started
            Assert.False(blind.Last.PlasmaPresent);
        }

        // With one, the same frames are a classified step with the gate open.
        using var h = new Harness(Classifier(thresholdA: 200), Ratio("R_A", processClass: "A"));
        for (double t = 0; t <= 3.0; t += 0.5) h.Engine.ProcessSample(StepA(t));
        Assert.Equal("A", h.Last.ProcessClass);
        Assert.True(h.Last.PlasmaPresent);
    }

    // ---------------------------------------------------------------- capture

    // ---------------------------------------------------------------- round-trip

    /// <summary>
    /// Editing a ratio in the Ratio Setup tab preserves its process class.
    ///
    /// <para>The class is set by hand in <c>settings.json</c>, and the tab rebuilds every
    /// definition it saves from its own fields. Without the class among them, the first Save
    /// anyone pressed would un-scope every ratio at once — the panel would look untouched, the
    /// leak monitor would go back to judging three processes against one baseline, and nothing
    /// anywhere would say it had happened.</para>
    /// </summary>
    [Fact]
    public void RatioSetupEdit_DoesNotDropTheProcessClass()
    {
        var lines = SpectralLineCatalog.All.Select(l => new SpectralLineOption(l)).ToList();
        var original = Ratio("R_C", processClass: "C");

        var edited = new RatioEditViewModel(original.Clone(), lines);
        Assert.Equal("C", edited.ProcessClass);

        edited.DisplayName = "renamed";           // an ordinary edit, nothing to do with classes
        var saved = edited.ToDefinition();

        Assert.Equal("C", saved.ProcessClass);
        Assert.Equal("renamed", saved.DisplayName);
        Assert.True(original.MeasuresSameAs(saved));   // a rename does not change what it measures
    }

    /// <summary>
    /// A ratio moved to a different process class no longer measures the same quantity, so a
    /// latched alarm and a stored baseline from the old class must not carry over — the same
    /// two lines read during a different plasma give a different number.
    /// </summary>
    [Fact]
    public void ChangingTheProcessClass_ChangesWhatIsMeasured()
    {
        var inC = Ratio("R_C", processClass: "C");
        var inA = inC.Clone();
        inA.ProcessClass = "A";
        Assert.False(inC.MeasuresSameAs(inA));
    }

    // ---------------------------------------------------------------- capture

    /// <summary>
    /// A Golden Run capture that only ever saw the wrong process gets no baseline, and the
    /// engine says which class it was looking for. Storing a baseline built from another
    /// plasma would be worse than storing none: nothing on screen would say the number is
    /// meaningless.
    /// </summary>
    [Fact]
    public void CaptureAcrossTheWrongClass_YieldsNoBaselineAndExplainsWhy()
    {
        using var h = new Harness(Classifier(thresholdA: 200), Ratio("R_C", processClass: "C"));
        GoldenRunCaptureResult? result = null;
        h.Engine.GoldenRunCaptureFinished += (_, r) => result = r;

        h.Engine.BeginGoldenRunCapture("wrong-step", seconds: 2);
        for (double t = 0; t <= 3.0; t += 0.5) h.Engine.ProcessSample(StepB(t));

        Assert.NotNull(result);
        Assert.False(result!.Accepted);
        var reason = Assert.Single(result.Rejected).Reason;
        Assert.Contains("process step", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("C", reason);
    }
}
