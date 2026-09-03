using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// Building one Golden Run that holds a baseline for each process, from recordings of all of
/// them.
///
/// <para>What this guards: a ratio must draw only from recordings of its own process. The same
/// two lines read during a different plasma are a different quantity — on the measured tool the
/// levels differ by up to a factor of ten between processes — so pooling them produces a mean
/// nothing ever reads and a sigma wide enough to hide any leak. Nothing on screen would say so:
/// the build would report a baseline, and it would look like a baseline.</para>
///
/// <para>The applicability rule itself is shared with the engine
/// (<see cref="ProcessClassifier.AppliesTo"/>). A baseline built here is stored in the same field
/// and judged by the same thresholds as one captured live, so the two paths cannot be allowed to
/// disagree about which frames a ratio may learn from.</para>
/// </summary>
public class BaselineBuilderClassTests
{
    private const double Ar750 = 750.4, O777 = 777.4, Sig = 337.1;
    private static readonly DateTime Start = new(2026, 5, 6, 8, 0, 0, DateTimeKind.Local);

    private static float[] Axis()
    {
        var wl = new float[1100];
        for (int i = 0; i < wl.Length; i++) wl[i] = (float)(300.0 + i * 0.5);
        return wl;
    }

    /// <summary>A frame of the argon-carrying process (C) or of one without it (A).</summary>
    private static float[] Frame(float[] wl, bool hasArgon, double signal, double continuum = 10)
    {
        var inten = new float[wl.Length];
        for (int i = 0; i < wl.Length; i++)
        {
            inten[i] = (float)(continuum + (i % 2 == 0 ? 1 : -1));
            if (Math.Abs(wl[i] - Ar750) <= 1.5) inten[i] = hasArgon ? 1400f : 10f;
            if (Math.Abs(wl[i] - O777) <= 1.5) inten[i] = 1000f;
            if (Math.Abs(wl[i] - Sig) <= 1.5) inten[i] = (float)signal;
        }
        return inten;
    }

    private static LineRegion Raw(string label, double nm) => new()
    {
        Label = label, CenterNm = nm, HalfWidthNm = 0.5,
        BaselineGapNm = 1.0, BaselineWidthNm = 1.0, Mode = LineExtractMode.RawMean,
    };

    private static RatioDefinition Ratio(string key, string cls) => new()
    {
        Key = key, DisplayName = key, Enabled = true, ProcessClass = cls,
        MonitorMode = MonitorMode.AbsoluteIntensity, MinSnr = 0,
        Numerator = Raw("Sig 337", Sig), Denominator = Raw("Ar 750", Ar750),
    };

    private static ProcessClassifier Classifier() => new(new ProcessClassifierSettings
    {
        Enabled = true,
        DecideAfterFrames = 3,
        FallbackClass = "A",
        Rules =
        {
            new ProcessClassRule
            {
                ClassName = "C", DisplayName = "Ar/O",
                Numerator = Raw("Ar 750", Ar750), Denominator = Raw("O 777", O777),
                Op = ComparisonOp.GreaterThan, Threshold = 0.5,
            },
        },
        Classes =
        {
            new ProcessClassDefinition { Name = "A" },
            new ProcessClassDefinition { Name = "C" },
        },
    });

    private static PlasmaGate Gate() => new(new LoggerSettings
    {
        TriggerMode = TriggerMode.Wavelength,
        TriggerWavelength = (float)O777,
        WavelengthToleranceNm = 1f,
        SaveStartThresholdIntensity = 500,
    });

    /// <summary>One 100-frame recording at 2 s, of the process asked for and at the level asked
    /// for.</summary>
    private static RecordingScan Scan(string name, bool hasArgon, double signal,
                                      IReadOnlyList<RatioDefinition> defs,
                                      ProcessClassifier? classifier)
    {
        var wl = Axis();
        var frames = new List<float[]>();
        var el = new List<double>();
        var rnd = new Random(name.GetHashCode());
        for (int i = 0; i < 100; i++)
        {
            // A little scatter, so the baseline has a sigma at all and the consistency checks
            // have something to be typical of.
            frames.Add(Frame(wl, hasArgon, signal * (1.0 + 0.01 * (rnd.NextDouble() - 0.5))));
            el.Add(i * 2.0);
        }
        return BaselineBuilder.Scan(name, name, Start, wl, frames, el, defs, Gate(), null, null,
                                    CancellationToken.None, classifier);
    }

    private static BaselineBuildResult Build(params RecordingScan[] scans) =>
        BaselineBuilder.Build(
            scans.Select(s => (s, new SteadyWindow(0, 200))).ToList(),
            new BaselineBuildOptions { RunName = "r", MinFrames = 10 });

    // ----------------------------------------------------------------

    [Fact]
    public void A_recording_is_named_by_the_same_classifier_the_engine_runs()
    {
        var defs = new[] { Ratio("R_C", "C") };
        Assert.Equal("C", Scan("c1", hasArgon: true, 500, defs, Classifier()).ProcessClass);
        Assert.Equal("A", Scan("a1", hasArgon: false, 500, defs, Classifier()).ProcessClass);
    }

    [Fact]
    public void With_no_classifier_the_recording_carries_no_class_and_every_ratio_applies()
    {
        var defs = new[] { Ratio("R_any", "") };
        var scan = Scan("x", hasArgon: true, 500, defs, classifier: null);
        Assert.Equal("", scan.ProcessClass);
        Assert.False(scan.ClassScoped);

        var result = Build(scan);
        Assert.True(result.Accepted);
        Assert.Single(result.Run.Baselines);
    }

    /// <summary>
    /// The whole point: one build, recordings of two processes, and each ratio's baseline comes
    /// only from its own. If the classes were pooled the two baselines would be equal and sit
    /// between the levels — a mean neither process ever reads.
    /// </summary>
    [Fact]
    public void One_build_produces_a_separate_baseline_per_process()
    {
        var defs = new[] { Ratio("R_C", "C"), Ratio("R_A", "A") };
        var cls = Classifier();
        var result = Build(
            Scan("c1", hasArgon: true, 500, defs, cls),
            Scan("c2", hasArgon: true, 505, defs, cls),
            Scan("a1", hasArgon: false, 120, defs, cls),
            Scan("a2", hasArgon: false, 122, defs, cls));

        Assert.True(result.Accepted);
        Assert.Equal(2, result.Run.Baselines.Count);

        var c = result.Run.Find("R_C")!;
        var a = result.Run.Find("R_A")!;
        Assert.InRange(c.Mean, 495, 510);          // process C's own level
        Assert.InRange(a.Mean, 115, 127);          // process A's own level

        // Each drew only from its own two recordings, so neither sigma carries the gap between
        // the processes — which is four times either level here.
        Assert.True(c.Sigma < 0.05 * c.Mean, $"C's sigma {c.Sigma:G4} carries the other process");
        Assert.True(a.Sigma < 0.05 * a.Mean, $"A's sigma {a.Sigma:G4} carries the other process");
    }

    /// <summary>
    /// A ratio whose process is not among the selected recordings gets no baseline and is told
    /// which process it needed. Pooling from another process would have produced a number, and a
    /// number is what nobody would have questioned.
    /// </summary>
    [Fact]
    public void A_ratio_whose_process_was_not_recorded_is_rejected_by_name()
    {
        var defs = new[] { Ratio("R_C", "C"), Ratio("R_B", "B") };
        var cls = Classifier();
        var result = Build(
            Scan("c1", hasArgon: true, 500, defs, cls),
            Scan("c2", hasArgon: true, 505, defs, cls));

        Assert.Single(result.Run.Baselines);
        Assert.Equal("R_C", result.Run.Baselines[0].Key);

        var rejection = Assert.Single(result.Rejected, r => r.Key == "R_B");
        Assert.Contains("process B", rejection.Reason);
        Assert.Contains("C", rejection.Reason);        // and what was actually selected
    }

    /// <summary>
    /// The consistency check compares a recording against its peers <em>in the same process</em>.
    /// Judged across processes, the minority process is always the outlier — it differs by
    /// design, not by fault — and the build would set aside exactly the recordings it needs.
    /// </summary>
    [Fact]
    public void The_consistency_check_does_not_call_another_process_an_outlier()
    {
        var defs = new[] { Ratio("R_C", "C"), Ratio("R_A", "A") };
        var cls = Classifier();
        var result = Build(
            Scan("c1", hasArgon: true, 500, defs, cls),
            Scan("c2", hasArgon: true, 502, defs, cls),
            Scan("c3", hasArgon: true, 498, defs, cls),
            Scan("a1", hasArgon: false, 120, defs, cls));

        Assert.Empty(result.Outliers);
        Assert.Equal(2, result.Run.Baselines.Count);
    }

    /// <summary>
    /// The spread report is per process too. Two recordings of different processes are not two
    /// measurements that disagree, and reporting them as such would put a number at the top of
    /// the panel that no adjustment could ever improve.
    /// </summary>
    [Fact]
    public void The_spread_report_compares_within_a_process()
    {
        var defs = new[] { Ratio("R_C", "C"), Ratio("R_A", "A") };
        var cls = Classifier();
        var result = Build(
            Scan("c1", hasArgon: true, 500, defs, cls),
            Scan("c2", hasArgon: true, 505, defs, cls),
            Scan("a1", hasArgon: false, 120, defs, cls),
            Scan("a2", hasArgon: false, 122, defs, cls));

        foreach (var row in result.Spread)
            Assert.True(row.RelativeSpread < 0.1,
                $"{row.RatioDisplayName} spread {row.RelativeSpread:P0} — processes were pooled");
    }
}
