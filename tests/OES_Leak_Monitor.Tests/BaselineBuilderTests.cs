using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Aqst.OesSpectrometer.Models;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// The offline baseline builder. The claim it has to earn is narrow and total: a Golden Run built
/// from a recording must be the same numbers the live capture would have produced from the same
/// frames — it is stored in the same field and judged by the same thresholds, so anything else
/// makes two baselines incomparable while both look fine.
/// </summary>
public class BaselineBuilderTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);

    // --- synthetic fixtures --------------------------------------------------

    private const double LineNm = 350.0;

    private static float[] Axis()
    {
        var wl = new float[1000];
        for (int i = 0; i < wl.Length; i++) wl[i] = (float)(300.0 + i * 0.5);
        return wl;
    }

    /// <summary>A flat continuum with one line on it at 350 nm.</summary>
    private static float[] Frame(float[] wl, double lineLevel)
    {
        var inten = new float[wl.Length];
        for (int i = 0; i < wl.Length; i++)
        {
            inten[i] = 1000f + (i % 2 == 0 ? 1f : -1f);
            if (Math.Abs(wl[i] - LineNm) <= 0.5) inten[i] = (float)lineLevel;
        }
        return inten;
    }

    private static RatioDefinition RawAbsolute() => new()
    {
        Key = "R_test",
        DisplayName = "test line",
        Enabled = true,
        MonitorMode = MonitorMode.AbsoluteIntensity,
        MinSnr = 0,
        Numerator = new LineRegion
        {
            Label = "X 350", CenterNm = LineNm, HalfWidthNm = 0.5,
            BaselineGapNm = 1.0, BaselineWidthNm = 1.0, Mode = LineExtractMode.RawMean,
        },
        Denominator = new LineRegion
        {
            Label = "X 500", CenterNm = 500.0, HalfWidthNm = 0.5,
            BaselineGapNm = 1.0, BaselineWidthNm = 1.0, Mode = LineExtractMode.RawMean,
        },
    };

    private static PlasmaGate OpenGate() => new(new LoggerSettings
    {
        TriggerMode = TriggerMode.SpectrumPercentile,
        TriggerPercentile = 99,
        SaveStartThresholdIntensity = 100,
    });

    /// <summary>One recording: <paramref name="levels"/> is the line level per frame, 1 s apart.</summary>
    private static RecordingScan Fake(string path, IReadOnlyList<double> levels)
    {
        var wl = Axis();
        var frames = levels.Select(v => Frame(wl, v)).ToList();
        var elapsed = Enumerable.Range(0, levels.Count).Select(i => (double)i).ToList();
        return BaselineBuilder.Scan(path, path, Epoch, wl, frames, elapsed,
            new[] { RawAbsolute() }, OpenGate(), null, null, CancellationToken.None);
    }

    private static IReadOnlyList<double> Flat(int n, double level, double jitter = 1.0) =>
        Enumerable.Range(0, n).Select(i => level + (i % 2 == 0 ? jitter : -jitter)).ToList();

    // --- pooling -------------------------------------------------------------

    [Fact]
    public void PoolsEveryWindowIntoOneBaseline()
    {
        var picks = new[]
        {
            (Fake("a.csv", Flat(100, 2000)), new SteadyWindow(0, 99)),
            (Fake("b.csv", Flat(100, 2000)), new SteadyWindow(0, 99)),
        };

        var result = BaselineBuilder.Build(picks, new BaselineBuildOptions
        {
            RunName = "pooled", MinFrames = 60,
        });

        Assert.True(result.Accepted, result.Error);
        var b = Assert.Single(result.Run.Baselines);
        Assert.Equal(200, b.SampleCount);              // both windows, not one
        Assert.Equal(2000, b.Mean, 6);
        Assert.Equal(GoldenRunSource.OfflineBuild, result.Run.Source!.Kind);
        Assert.Equal(2, result.Run.Source.Files.Count);
    }

    /// <summary>
    /// The short-process case: no single run holds enough frames, several together do. Frames and
    /// not seconds, because that is what σ's reliability actually follows.
    /// </summary>
    [Fact]
    public void TooFewFramesIsRefusedWithTheCountInTheReason()
    {
        var picks = new[] { (Fake("short.csv", Flat(20, 2000)), new SteadyWindow(0, 19)) };

        var result = BaselineBuilder.Build(picks,
            new BaselineBuildOptions { RunName = "short", MinFrames = 60 });

        Assert.False(result.Accepted);
        var r = Assert.Single(result.Rejected);
        Assert.Contains("20", r.Reason);
        Assert.Contains("60", r.Reason);
    }

    // --- consistency ---------------------------------------------------------

    [Fact]
    public void ARecordingThatDisagreesWithItsPeersIsSetAsideByDefault()
    {
        var picks = new[]
        {
            (Fake("a.csv", Flat(100, 2000)), new SteadyWindow(0, 99)),
            (Fake("b.csv", Flat(100, 2000)), new SteadyWindow(0, 99)),
            (Fake("c.csv", Flat(100, 2000)), new SteadyWindow(0, 99)),
            (Fake("leaky.csv", Flat(100, 2600)), new SteadyWindow(0, 99)),
        };

        var result = BaselineBuilder.Build(picks,
            new BaselineBuildOptions { RunName = "peers", MinFrames = 60 });

        var outlier = Assert.Single(result.Outliers);
        Assert.Equal("leaky.csv", outlier.Path);
        // and it is genuinely out of the numbers, not merely reported
        Assert.Equal(300, Assert.Single(result.Run.Baselines).SampleCount);
        Assert.Equal(2000, result.Run.Baselines[0].Mean, 6);
        Assert.Single(result.Run.Source!.Excluded);
    }

    [Fact]
    public void AnExcludedRecordingCanBePutBack()
    {
        var picks = new[]
        {
            (Fake("a.csv", Flat(100, 2000)), new SteadyWindow(0, 99)),
            (Fake("b.csv", Flat(100, 2000)), new SteadyWindow(0, 99)),
            (Fake("c.csv", Flat(100, 2000)), new SteadyWindow(0, 99)),
            (Fake("odd.csv", Flat(100, 2100)), new SteadyWindow(0, 99)),
        };

        var result = BaselineBuilder.Build(picks, new BaselineBuildOptions
        {
            RunName = "peers", MinFrames = 60, ForceInclude = new[] { "odd.csv" },
        });

        Assert.Equal(400, Assert.Single(result.Run.Baselines).SampleCount);
        Assert.Empty(result.Run.Source!.Excluded);      // the judgement was overruled, so it is not recorded as made
        Assert.Single(result.Outliers);                 // but the operator was still told
    }

    /// <summary>
    /// Overruling the consistency check does not get you a baseline regardless: pooling a run that
    /// really is somewhere else inflates σ, and the existing mean &gt; 10 σ floor throws the result
    /// out. The two guards were written for different reasons and happen to cover each other.
    /// </summary>
    [Fact]
    public void ForcingInAFarOutRecordingStillFailsTheMeanOverSigmaFloor()
    {
        var picks = new[]
        {
            (Fake("a.csv", Flat(100, 2000)), new SteadyWindow(0, 99)),
            (Fake("b.csv", Flat(100, 2000)), new SteadyWindow(0, 99)),
            (Fake("c.csv", Flat(100, 2000)), new SteadyWindow(0, 99)),
            (Fake("leaky.csv", Flat(100, 2600)), new SteadyWindow(0, 99)),
        };

        var result = BaselineBuilder.Build(picks, new BaselineBuildOptions
        {
            RunName = "forced", MinFrames = 60, ForceInclude = new[] { "leaky.csv" },
        });

        Assert.False(result.Accepted);
        Assert.Contains("not clear of zero", Assert.Single(result.Rejected).Reason);
    }

    /// <summary>Two recordings cannot vote: with no majority there is no odd one out.</summary>
    [Fact]
    public void TwoRecordingsAreNeverJudgedAgainstEachOther()
    {
        var picks = new[]
        {
            (Fake("a.csv", Flat(100, 2000)), new SteadyWindow(0, 99)),
            (Fake("b.csv", Flat(100, 2600)), new SteadyWindow(0, 99)),
        };

        var result = BaselineBuilder.Build(picks,
            new BaselineBuildOptions { RunName = "pair", MinFrames = 60 });

        Assert.Empty(result.Outliers);
    }

    // --- refusals ------------------------------------------------------------

    [Fact]
    public void DifferentWavelengthAxesAreRefused()
    {
        var shortAxis = new float[500];
        for (int i = 0; i < shortAxis.Length; i++) shortAxis[i] = (float)(300.0 + i * 1.0);
        var other = BaselineBuilder.Scan("other.csv", "other", Epoch, shortAxis,
            Enumerable.Range(0, 100).Select(_ => Frame(shortAxis, 2000)).ToList(),
            Enumerable.Range(0, 100).Select(i => (double)i).ToList(),
            new[] { RawAbsolute() }, OpenGate(), null, null, CancellationToken.None);

        var result = BaselineBuilder.Build(
            new[] { (Fake("a.csv", Flat(100, 2000)), new SteadyWindow(0, 99)), (other, new SteadyWindow(0, 99)) },
            new BaselineBuildOptions { RunName = "mixed", MinFrames = 10 });

        Assert.False(result.Accepted);
        Assert.Contains("different wavelength axes", result.Error);
    }

    // --- suggestion ----------------------------------------------------------

    /// <summary>The soft-start case: ten seconds of ramp, then a plateau. The suggestion has one
    /// job — put the cursor past the ramp.</summary>
    [Fact]
    public void SuggestsThePlateauRatherThanTheRamp()
    {
        var levels = new List<double>();
        for (int i = 0; i < 10; i++) levels.Add(200 + i * 180);      // ramp 200 → 1820
        levels.AddRange(Flat(90, 2000));                              // plateau

        var scan = Fake("ramp.csv", levels);
        var best = BaselineBuilder.Suggest(scan, windowSeconds: 40).FirstOrDefault();

        Assert.NotNull(best);
        Assert.True(best!.FromSec >= 10, $"suggested window starts at {best.FromSec} s, inside the ramp");
    }

    // --- how steady is the window ------------------------------------------

    /// <summary>
    /// The failure this was written from: three whole recordings selected end to end, each
    /// containing the excursion the baseline was supposed to exclude. One ratio squeaked over the
    /// mean &gt; 10 σ floor at 10.2 and looked, on the Leak Monitor tab, exactly like a clean one —
    /// with thresholds ten times wider.
    /// </summary>
    [Fact]
    public void AWindowOverARampClearsTheFloorButIsFlaggedAsMarginal()
    {
        var ramp = Enumerable.Range(0, 100).Select(i => 2000.0 + i * 6).ToList();   // 2000 → 2594

        var result = BaselineBuilder.Build(
            new[] { (Fake("ramp.csv", ramp), new SteadyWindow(0, 99)) },
            new BaselineBuildOptions { RunName = "ramp", MinFrames = 60 });

        var b = Assert.Single(result.Run.Baselines);
        double meanOverSigma = b.Mean / b.Sigma;
        Assert.True(meanOverSigma > BaselineBuilder.MinBaselineMeanToSigma,
            $"expected it to clear the floor, got {meanOverSigma:0.#}");
        Assert.True(meanOverSigma < BaselineBuilder.MarginalMeanToSigma,
            $"expected it to read as marginal, got {meanOverSigma:0.#}");
    }

    /// <summary>
    /// σ alone cannot tell a ramp from noise, and the two want different answers: one means the
    /// window is in the wrong place, the other that the plasma is unsteady.
    /// </summary>
    [Fact]
    public void DriftSeparatesARampFromNoise()
    {
        var ramp = Enumerable.Range(0, 100).Select(i => 2000.0 + i * 6).ToList();
        var noisy = Enumerable.Range(0, 100).Select(i => 2300.0 + (i % 2 == 0 ? 170 : -170)).ToList();

        var rampBuild = BaselineBuilder.Build(
            new[] { (Fake("ramp.csv", ramp), new SteadyWindow(0, 99)) },
            new BaselineBuildOptions { RunName = "ramp", MinFrames = 60 });
        var noiseBuild = BaselineBuilder.Build(
            new[] { (Fake("noise.csv", noisy), new SteadyWindow(0, 99)) },
            new BaselineBuildOptions { RunName = "noise", MinFrames = 60 });

        var rampRow = Assert.Single(rampBuild.Steadiness);
        var noiseRow = Assert.Single(noiseBuild.Steadiness);

        // Comparable scatter…
        Assert.True(rampRow.RelativeSigma > 0.05);
        Assert.True(noiseRow.RelativeSigma > 0.05);
        // …and only one of them is going anywhere.
        Assert.True(rampRow.RelativeDrift > 0.1, $"ramp drift {rampRow.RelativeDrift:P1}");
        Assert.True(noiseRow.RelativeDrift < 0.01, $"noise drift {noiseRow.RelativeDrift:P1}");
    }

    /// <summary>
    /// A window that swallows its whole recording silently disables the back-check — there is
    /// nothing outside it left to compare. Saying so is the point: an empty section reads as a
    /// clean bill of health.
    /// </summary>
    [Fact]
    public void AWholeRecordingWindowIsReportedAndLeavesNoBackCheck()
    {
        var result = BaselineBuilder.Build(
            new[] { (Fake("whole.csv", Flat(100, 2000)), new SteadyWindow(0, 99)) },
            new BaselineBuildOptions { RunName = "whole", MinFrames = 60 });

        Assert.True(Assert.Single(result.Steadiness).WindowCoversWholeRecording);
        Assert.Empty(result.BackChecks);
    }

    [Fact]
    public void APartialWindowIsNotReportedAsCoveringTheRecording()
    {
        var result = BaselineBuilder.Build(
            new[] { (Fake("part.csv", Flat(200, 2000)), new SteadyWindow(0, 99)) },
            new BaselineBuildOptions { RunName = "part", MinFrames = 60 });

        Assert.False(Assert.Single(result.Steadiness).WindowCoversWholeRecording);
        Assert.NotEmpty(result.BackChecks);
    }

    // --- the claim -----------------------------------------------------------

    /// <summary>
    /// Offline equals live, on a real plasma recording: the same frames through
    /// <see cref="LeakMonitorEngine"/>'s capture and through the builder have to produce the same
    /// baseline. Everything else here tests the builder's own arithmetic; this one tests that the
    /// arithmetic is the engine's.
    /// </summary>
    [SkippableFact]
    public void OnARealRecording_TheBuilderAgreesWithTheLiveCapture()
    {
        var path = RecordedRun.ResolvePath();
        Skip.If(path is null, RecordedRun.SkipReason);

        var run = RecordedRun.Load(path!);
        var settings = LeakMonitorSettings.CreateDefault();
        var trigger = new LoggerSettings
        {
            TriggerMode = TriggerMode.SpectrumPercentile,
            TriggerPercentile = 99,
            SaveStartThresholdIntensity = 2000,
        };
        const double captureSeconds = 60.0;

        // --- live: replay into the engine, capturing from the first frame ---
        GoldenRun? live = null;
        using (var engine = new LeakMonitorEngine(settings))
        {
            engine.ConfigureTrigger(trigger);
            engine.GoldenRunCaptureFinished += (_, r) => live ??= r.Run;
            engine.BeginGoldenRunCapture("live", captureSeconds);
            for (int i = 0; i < run.FrameCount && live is null; i++)
                engine.ProcessSample(run.Frame(i, Epoch));
        }
        Assert.NotNull(live);
        Assert.NotEmpty(live!.Baselines);

        // --- offline: the same window, through the builder ---
        // The engine finalises on the first frame at or past the window, and that frame is
        // already accumulated — so the window ends there, inclusive.
        double t0 = run.Frame(0, Epoch).Timestamp.Subtract(Epoch).TotalSeconds;
        double last = t0;
        for (int i = 0; i < run.FrameCount; i++)
        {
            double t = run.Frame(i, Epoch).Timestamp.Subtract(Epoch).TotalSeconds;
            last = t;
            if (t - t0 >= captureSeconds) break;
        }

        var frames = new List<float[]>();
        var elapsed = new List<double>();
        for (int i = 0; i < run.FrameCount; i++)
        {
            var f = run.Frame(i, Epoch);
            frames.Add(f.Intensities);
            elapsed.Add(f.Timestamp.Subtract(Epoch).TotalSeconds);
        }

        var lookup = WavelengthCalibration.Build(settings.WavelengthCorrections);
        var defs = settings.Ratios.Select(d => WavelengthCalibration.Correct(d, lookup)).ToList();
        var scan = BaselineBuilder.Scan(path!, "recorded", Epoch, run.Wavelengths, frames, elapsed,
            defs, new PlasmaGate(trigger), null, null, CancellationToken.None);

        var built = BaselineBuilder.Build(
            new[] { (scan, new SteadyWindow(t0, last)) },
            new BaselineBuildOptions { RunName = "offline", MinFrames = 1 });

        Assert.True(built.Accepted, built.Error);
        Assert.Equal(live.Baselines.Count, built.Run.Baselines.Count);
        foreach (var b in live.Baselines)
        {
            var o = built.Run.Baselines.Single(x => x.Key == b.Key);
            Assert.Equal(b.SampleCount, o.SampleCount);
            Assert.Equal(b.Mean, o.Mean, 9);
            Assert.Equal(b.Sigma, o.Sigma, 9);
            Assert.Equal(b.Mode, o.Mode);
            Assert.Equal(b.ReferenceLabel, o.ReferenceLabel);
        }

        // The plasma floors come out of the same frames, so they have to match too.
        foreach (var f in live.PlasmaFloors)
        {
            var o = built.Run.PlasmaFloors.Single(x => x.ReferenceKey == f.ReferenceKey);
            Assert.Equal(f.Floor, o.Floor, 6);
        }
    }
}
