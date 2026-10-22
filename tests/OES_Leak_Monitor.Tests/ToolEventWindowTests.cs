using System;
using System.Collections.Generic;
using System.Linq;
using Aqst.OesSpectrometer.Models;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// Process windows driven by the main tool's own events (S2F41 <c>TOOL_EVENT</c>), at the
/// engine seam.
///
/// <para>What this guards: PM3 interleaves Chamber Process (process A), Chamber Clean (B) and a
/// process nobody has placed yet, against one leak monitor. The spectrum can only infer which is
/// running; the tool knows, and the EAP forwards its ProcessStart / ProcessEnd CEIDs. A ratio
/// scoped to <c>A</c> must judge inside an A window and nowhere else — and an installation that
/// never configures any of this must not notice it exists.</para>
/// </summary>
public class ToolEventWindowTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);

    // PM3's rows of docs/EAP ceid-events.md.
    private const uint ProcessStart = 3100439529, ProcessEnd = 3100439530;
    private const uint CleanStart = 3099591660, CleanEnd = 3099591661;
    private const uint LeakCheckStart = 3100167145;

    private const double O777 = 777.4, Sig337 = 337.1;

    /// <summary>A flat continuum with the two lines the tests read placed on it.</summary>
    private static SpectrumSample Frame(double seconds, double o777, double sig337)
    {
        const int n = 1100;
        var wl = new float[n];
        var inten = new float[n];
        for (int i = 0; i < n; i++)
        {
            wl[i] = (float)(300.0 + i * 0.5);
            inten[i] = (float)(10 + (i % 2 == 0 ? 1 : -1));
            // Plateaus wider than the extraction window, so a RawMean reads the level exactly.
            if (Math.Abs(wl[i] - O777) <= 1.5) inten[i] = (float)o777;
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
            IsSimulated = true,
        };
    }

    /// <summary>Plasma on: the trigger line (777 nm) at 800 against a save threshold of 500.</summary>
    private static SpectrumSample On(double t, double signal = 500) => Frame(t, o777: 800, sig337: signal);

    /// <summary>Plasma off: the trigger line at the continuum.</summary>
    private static SpectrumSample Off(double t) => Frame(t, o777: 10, sig337: 10);

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
        Denominator = Raw("O 777", O777),
    };

    /// <summary>PM3's process and clean windows, plus a record-only row, as a site would map them.</summary>
    private static ToolEventSettings Pm3(double thresholdA = 0) => new()
    {
        Enabled = true,
        Mappings =
        {
            new ToolEventMapping { Ceid = ProcessStart, ClassName = "A", Edge = ToolEventEdge.Start,
                                   Name = "PM3 ProcessStart", PlasmaThreshold = thresholdA },
            new ToolEventMapping { Ceid = ProcessEnd, ClassName = "A", Edge = ToolEventEdge.End,
                                   Name = "PM3 ProcessEnd" },
            new ToolEventMapping { Ceid = CleanStart, ClassName = "B", Edge = ToolEventEdge.Start,
                                   Name = "PM3 Clean ProcessStart" },
            new ToolEventMapping { Ceid = CleanEnd, ClassName = "B", Edge = ToolEventEdge.End,
                                   Name = "PM3 Clean ProcessEnd" },
            new ToolEventMapping { Ceid = LeakCheckStart, ClassName = "", Edge = ToolEventEdge.Start,
                                   Name = "PM3 Auto Leak check" },
        },
    };

    private sealed class Harness : IDisposable
    {
        public LeakMonitorEngine Engine { get; }
        public List<LeakMonitorSnapshot> Snaps { get; } = new();

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
            Engine.ConfigureTrigger(new LoggerSettings
            {
                TriggerMode = TriggerMode.Wavelength,
                TriggerWavelength = (float)O777,
                WavelengthToleranceNm = 1f,
                SaveStartThresholdIntensity = 500,
            });
            Engine.SampleProcessed += (_, s) => Snaps.Add(s);
        }

        public LeakMonitorSnapshot Last => Snaps[^1];
        public RatioSnapshot Ratio(string key) => Last.Ratios.First(r => r.Key == key);

        /// <summary>Forwards a tool event the way the SECS bridge does.</summary>
        public ToolEventAck Event(uint ceid, double t) => Engine.ReceiveToolEvent(ceid, Epoch.AddSeconds(t));

        public void Dispose() => Engine.Dispose();
    }

    // ---------------------------------------------------------------- off by default

    /// <summary>
    /// Never configured, the engine reports tool events as not in use, refuses them, and judges
    /// exactly as it always has: a class-scoped ratio applies to every step because nothing names
    /// one. This is what makes the feature safe to ship to an installation that has no EAP.
    /// </summary>
    [Fact]
    public void NotConfigured_ChangesNothing()
    {
        using var h = new Harness(classifier: null, Ratio("R_A", "A"));

        Assert.Equal(ToolEventAck.Disabled, h.Event(ProcessStart, 0));
        for (double t = 0; t <= 2.0; t += 0.5) h.Engine.ProcessSample(On(t));

        Assert.Equal(ToolEventContext.Disabled, h.Last.ToolEventState);
        Assert.Equal("", h.Last.ToolWindowClass);
        Assert.NotEqual(RatioState.NotApplicable, h.Ratio("R_A").State);
    }

    // ---------------------------------------------------------------- the window

    /// <summary>
    /// Inside a Chamber Process window an A-scoped ratio judges; once the window closes it reads
    /// NotApplicable, while a ratio with no class keeps judging throughout — it made no claim
    /// about which process it measures.
    /// </summary>
    [Fact]
    public void AWindow_ScopesAClassRatio_AndAnUnscopedOneIsUnaffected()
    {
        using var h = new Harness(classifier: null, Ratio("R_A", "A"), Ratio("R_any", ""));
        h.Engine.ConfigureToolEvents(Pm3());

        double t = 0;
        Assert.Equal(ToolEventAck.Accepted, h.Event(ProcessStart, t));
        for (; t <= 2.0; t += 0.5) h.Engine.ProcessSample(On(t));
        Assert.Equal(ToolEventContext.InWindow, h.Last.ToolEventState);
        Assert.Equal("A", h.Last.ToolWindowClass);
        Assert.Equal("A", h.Last.ProcessClass);
        Assert.Equal(RatioState.NoBaseline, h.Ratio("R_A").State);     // judged; no baseline yet
        Assert.Equal(RatioState.NoBaseline, h.Ratio("R_any").State);

        Assert.Equal(ToolEventAck.Accepted, h.Event(ProcessEnd, t));
        for (; t <= 4.0; t += 0.5) h.Engine.ProcessSample(On(t));
        Assert.Equal(ToolEventContext.OutsideWindow, h.Last.ToolEventState);
        Assert.Equal("", h.Last.ToolWindowClass);
        Assert.Equal(RatioState.NotApplicable, h.Ratio("R_A").State);
        Assert.Equal(RatioState.NoBaseline, h.Ratio("R_any").State);
    }

    /// <summary>
    /// The window decides from the next frame on, not after a step has been watched for a few
    /// frames: the tool's event marks the boundary exactly, so there is no ignition transient to
    /// wait out the way the spectral classifier has to.
    /// </summary>
    [Fact]
    public void TheWindowApplies_OnTheVeryNextFrame()
    {
        using var h = new Harness(classifier: null, Ratio("R_A", "A"));
        h.Engine.ConfigureToolEvents(Pm3());

        h.Event(CleanStart, 0);
        h.Engine.ProcessSample(On(0));
        Assert.Equal("B", h.Last.ToolWindowClass);
        Assert.Equal(RatioState.NotApplicable, h.Ratio("R_A").State);

        // The tool moves straight from one window to the next while the plasma stays lit.
        h.Event(CleanEnd, 0.5);
        h.Event(ProcessStart, 0.5);
        h.Engine.ProcessSample(On(0.5));
        Assert.Equal("A", h.Last.ToolWindowClass);
        Assert.NotEqual(RatioState.NotApplicable, h.Ratio("R_A").State);
    }

    /// <summary>
    /// A window says which process the tool is running, not that there is plasma: the seconds
    /// before ignition and after the plasma goes out are inside the window and still gated.
    /// </summary>
    [Fact]
    public void ThePlasmaGate_StillApplies_InsideAWindow()
    {
        using var h = new Harness(classifier: null, Ratio("R_A", "A"));
        h.Engine.ConfigureToolEvents(Pm3());

        h.Event(ProcessStart, 0);
        for (double t = 0; t <= 2.0; t += 0.5) h.Engine.ProcessSample(Off(t));

        Assert.Equal(ToolEventContext.InWindow, h.Last.ToolEventState);
        Assert.False(h.Last.PlasmaPresent);
        Assert.Equal(RatioState.NoPlasma, h.Ratio("R_A").State);
    }

    /// <summary>
    /// A latched alarm survives its window closing, exactly as it survives leaving a class the
    /// classifier named: a confirmed leak does not end because the tool moved on.
    /// </summary>
    [Fact]
    public void ALatchedAlarm_SurvivesTheWindowClosing()
    {
        using var h = new Harness(classifier: null, Ratio("R_A", "A"));
        h.Engine.ConfigureToolEvents(Pm3());

        double t = 0;
        h.Event(ProcessStart, t);
        h.Engine.BeginGoldenRunCapture("base", seconds: 2);
        for (; t <= 3.0; t += 0.5) h.Engine.ProcessSample(On(t, signal: 500));
        for (; t <= 12.0; t += 0.5) h.Engine.ProcessSample(On(t, signal: 2000));
        Assert.Equal(LeakAlarmLevel.Alarm, h.Last.Overall);

        h.Event(ProcessEnd, t);
        for (; t <= 14.0; t += 0.5) h.Engine.ProcessSample(On(t, signal: 500));
        Assert.Equal(ToolEventContext.OutsideWindow, h.Last.ToolEventState);
        Assert.Equal(RatioState.Alarm, h.Ratio("R_A").State);
        Assert.Equal(LeakAlarmLevel.Alarm, h.Last.Overall);
    }

    // ---------------------------------------------------------------- unknown context

    /// <summary>
    /// Switched on but before any window edge has arrived, the engine cannot say which process
    /// is running, and judges as it did without tool events rather than judging nothing — losing
    /// a real leak to an EAP that is late with its first event costs more than a false alarm,
    /// and the state says "unknown" so nobody mistakes it for a scoped judgement.
    /// </summary>
    [Fact]
    public void BeforeTheFirstEvent_TheEngineFallsBackToHowItJudgedWithoutThem()
    {
        using var h = new Harness(classifier: null, Ratio("R_A", "A"));
        h.Engine.ConfigureToolEvents(Pm3());

        for (double t = 0; t <= 2.0; t += 0.5) h.Engine.ProcessSample(On(t));

        Assert.Equal(ToolEventContext.Unknown, h.Last.ToolEventState);
        Assert.NotEqual(RatioState.NotApplicable, h.Ratio("R_A").State);   // no classifier: unscoped
    }

    /// <summary>
    /// The fallback is the classifier when one is configured — and once a window is known, the
    /// tool's word replaces the spectrum's guess, so a ratio the classifier would have stood down
    /// judges inside the window the tool says is its own.
    /// </summary>
    [Fact]
    public void WithAClassifier_TheFallbackIsTheClassifier_AndAWindowOverridesIt()
    {
        // A one-rule classifier that names every lit step A.
        var classifier = new ProcessClassifierSettings
        {
            Enabled = true,
            DecideAfterFrames = 1,
            Rules =
            {
                new ProcessClassRule
                {
                    ClassName = "A", DisplayName = "O777/O777",
                    Numerator = Raw("O 777", O777), Denominator = Raw("O 777", O777),
                    Op = ComparisonOp.GreaterThan, Threshold = 0.5,
                },
            },
        };
        using var h = new Harness(classifier, Ratio("R_B", "B"));
        h.Engine.ConfigureToolEvents(Pm3());

        double t = 0;
        for (; t <= 2.0; t += 0.5) h.Engine.ProcessSample(On(t));
        Assert.Equal(ToolEventContext.Unknown, h.Last.ToolEventState);
        Assert.Equal("A", h.Last.ProcessClass);                          // the classifier's verdict
        Assert.Equal(RatioState.NotApplicable, h.Ratio("R_B").State);

        h.Event(CleanStart, t);
        for (; t <= 4.0; t += 0.5) h.Engine.ProcessSample(On(t));
        Assert.Equal("B", h.Last.ProcessClass);                          // the tool's word
        Assert.NotEqual(RatioState.NotApplicable, h.Ratio("R_B").State);
    }

    // ---------------------------------------------------------------- per-class plasma threshold

    /// <summary>
    /// Each class can run at its own plasma level. The mapping's threshold applies inside its
    /// window even with the spectral classifier switched off — the classifier used to be the only
    /// place a per-class level could come from, which would have left a window-supplied class
    /// gated at the base threshold with nothing to say so.
    /// </summary>
    [Fact]
    public void TheMappingsPlasmaThreshold_AppliesWithTheClassifierOff()
    {
        using var h = new Harness(classifier: null, Ratio("R_A", "A"));
        h.Engine.ConfigureToolEvents(Pm3(thresholdA: 900));

        h.Event(ProcessStart, 0);
        for (double t = 0; t <= 2.0; t += 0.5) h.Engine.ProcessSample(On(t));   // 800 < 900

        Assert.False(h.Last.PlasmaPresent);
        Assert.Equal(RatioState.NoPlasma, h.Ratio("R_A").State);
    }

    /// <summary>
    /// With no threshold on the mapping, the class's level in the classifier settings applies —
    /// whether or not the classifier is switched on, because that is where a site already wrote
    /// down what each process runs at.
    /// </summary>
    [Fact]
    public void WithoutOne_TheClassifierSettingsThresholdApplies_EvenWithTheClassifierOff()
    {
        var off = new ProcessClassifierSettings
        {
            Enabled = false,
            Classes = { new ProcessClassDefinition { Name = "A", PlasmaThreshold = 900 } },
        };
        using var h = new Harness(off, Ratio("R_A", "A"));
        h.Engine.ConfigureToolEvents(Pm3(thresholdA: 0));

        h.Event(ProcessStart, 0);
        for (double t = 0; t <= 2.0; t += 0.5) h.Engine.ProcessSample(On(t));

        Assert.False(h.Last.PlasmaPresent);
    }

    // ---------------------------------------------------------------- Golden Run capture

    /// <summary>
    /// A live capture that runs across a window's end learns an A-scoped ratio's baseline only
    /// from the A-window frames. The plasma after the window is lit and reads a different level;
    /// pooling it would put the mean between two processes, a value neither ever reads, with a σ
    /// wide enough to hide a leak.
    /// </summary>
    [Fact]
    public void ACapture_LearnsAnAScopedRatio_OnlyFromAWindowFrames()
    {
        using var h = new Harness(classifier: null, Ratio("R_A", "A"));
        h.Engine.ConfigureToolEvents(Pm3());

        double t = 0;
        h.Event(ProcessStart, t);
        h.Engine.BeginGoldenRunCapture("window", seconds: 4);
        for (; t < 2.0; t += 0.5) h.Engine.ProcessSample(On(t, signal: 500));
        h.Event(ProcessEnd, t);
        for (; t <= 4.5; t += 0.5) h.Engine.ProcessSample(On(t, signal: 900));

        var run = h.Engine.Settings.FindGoldenRun("window");
        Assert.NotNull(run);
        var baseline = run!.Find("R_A");
        Assert.NotNull(baseline);
        Assert.Equal(500.0, baseline!.Mean, 6);
    }

    // ---------------------------------------------------------------- adoption

    /// <summary>
    /// What the host is answered with: an event the table knows is taken, one it does not is
    /// acknowledged and ignored (so the EAP can forward unfiltered), and a recorded-only row is
    /// taken without opening anything.
    /// </summary>
    [Fact]
    public void EventsAreTaken_IgnoredOrRecorded_ByTheTable()
    {
        using var h = new Harness(classifier: null, Ratio("R_A", "A"));
        h.Engine.ConfigureToolEvents(Pm3());

        Assert.Equal(ToolEventAck.Ignored, h.Event(2697019492, 0));     // slit door, not mapped
        Assert.Equal(ToolEventAck.Accepted, h.Event(LeakCheckStart, 0)); // recorded only
        h.Engine.ProcessSample(On(0));

        Assert.Equal(ToolEventContext.Unknown, h.Last.ToolEventState);    // no window opened
        Assert.Equal(LeakCheckStart, h.Last.LastToolCeid);
    }

    /// <summary>
    /// Adoption is where the engine takes a configuration, and it is staged: the host applies it
    /// on an acquisition restart. Re-applying the same table — every restart does — keeps the
    /// window it is in; a changed table starts from "unknown", because the window it remembers
    /// was named by a table that no longer exists. Switching off returns to "not in use".
    /// </summary>
    [Fact]
    public void ReapplyingTheSameTable_KeepsTheWindow_AndAChangedOneForgetsIt()
    {
        using var h = new Harness(classifier: null, Ratio("R_A", "A"));
        h.Engine.ConfigureToolEvents(Pm3());

        h.Event(ProcessStart, 0);
        h.Engine.ProcessSample(On(0));
        Assert.Equal(ToolEventContext.InWindow, h.Last.ToolEventState);

        h.Engine.ConfigureToolEvents(Pm3());
        h.Engine.ProcessSample(On(0.5));
        Assert.Equal(ToolEventContext.InWindow, h.Last.ToolEventState);

        h.Engine.ConfigureToolEvents(Pm3(thresholdA: 100));
        h.Engine.ProcessSample(On(1.0));
        Assert.Equal(ToolEventContext.Unknown, h.Last.ToolEventState);

        h.Engine.ConfigureToolEvents(new ToolEventSettings { Enabled = false, Mappings = Pm3().Mappings });
        h.Engine.ProcessSample(On(1.5));
        Assert.Equal(ToolEventContext.Disabled, h.Last.ToolEventState);
        Assert.Equal(ToolEventAck.Disabled, h.Event(ProcessStart, 2.0));
    }
}
