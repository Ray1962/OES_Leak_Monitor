using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Aqst.OesSpectrometer.Models;
using OES_Leak_Monitor;
using Xunit;
using Xunit.Abstractions;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// P1′ — what an injected rise in N₂ 337 does to the shipped configuration, measured on real
/// spectra.
///
/// <para><b>Why this exists and what it cannot do.</b> The plan's GO/NO-GO asks whether a leak of
/// 1×10⁻⁴ mbar·L/s moves the indicator by more than 3 σ. That has two halves. The second — how
/// much N₂ a leak of that size puts into this chamber — is a property of the chamber; no data in
/// this repository contains it (the 2026-08-19 test used valve units, and its own analysis
/// refuses to attribute the response to the valve; no calibration has ever been stored). Only the
/// tool can answer it.</para>
///
/// <para>The first half can be answered here: given a rise of x % in the band, does the shipped
/// extraction report x %, and does the chain reach an alarm. Both are worth knowing before
/// spending tool time, and the first turns out not to be free — see
/// <see cref="Injected_rise_is_partly_eaten_by_the_continuum_windows"/>.</para>
///
/// <para>An injected rise is an <b>upper bound</b> on a real leak of the same magnitude: it moves
/// the numerator and leaves the reference line alone, whereas air ingress changes the discharge
/// loading and moves both. See <see cref="BandInjector"/>.</para>
/// </summary>
public class LeakInjectionTests
{
    private readonly ITestOutputHelper _out;
    public LeakInjectionTests(ITestOutputHelper output) => _out = output;

    // ---------------------------------------------------------------- the shipped configuration

    // docs/leak-monitor-plan-zh-TW.md §4.2. Centres include the measured +0.30 nm axis offset,
    // and the peak search is off on purpose: N₂ 337 sits between two large CO bands (329.6 and
    // 348.2) and a search window is easily pulled onto a neighbour's shoulder.
    private static LineRegion N2_337 => new()
    {
        Label = "N2 337.1", CenterNm = 337.40, HalfWidthNm = 0.70,
        BaselineGapNm = 1.30, BaselineWidthNm = 1.10,
        Mode = LineExtractMode.PeakHeight, PeakSearchHalfWidthNm = 0.0,
    };

    private static LineRegion CO_329 => new()
    {
        Label = "CO 329.6", CenterNm = 329.60, HalfWidthNm = 1.06,
        BaselineGapNm = 2.10, BaselineWidthNm = 1.10,
        Mode = LineExtractMode.PeakHeight, PeakSearchHalfWidthNm = 0.0,
    };

    // Where the N₂ 337 rise is applied. Wide enough to cover the band's red-degraded tail and
    // both of N₂ 337's own continuum windows (335.4–336.5 and 338.3–339.4), and stopping clear
    // of CO 329.6's right-hand continuum window, which ends at 333.86 — reaching into that
    // brightens the denominator's continuum, lowers its peak height, and reports a larger rise
    // than was injected. See BandInjector.
    private const double InjectLoNm = 334.2, InjectHiNm = 342.6;
    private const double ContinuumLeftNm = 335.0, ContinuumRightNm = 339.8, ContinuumHalfNm = 0.55;

    private static float[] InjectN2(float[] wl, float[] inten, double fraction) =>
        BandInjector.Inject(wl, inten, InjectLoNm, InjectHiNm,
                            ContinuumLeftNm, ContinuumRightNm, ContinuumHalfNm, fraction);

    // ---------------------------------------------------------------- injector, on synthetic data

    /// <summary>A flat continuum with a triangular band on it, so the expected result is
    /// arithmetic rather than a second implementation of the injector.</summary>
    private static (float[] wl, float[] inten) Synthetic(double bandPeak)
    {
        const int n = 400;
        var wl = new float[n];
        var inten = new float[n];
        for (int i = 0; i < n; i++)
        {
            wl[i] = (float)(330.0 + i * 0.05);            // 330.0 – 349.95 nm
            inten[i] = 100f;                               // flat continuum
            double d = Math.Abs(wl[i] - 337.40);
            if (d <= 1.0) inten[i] = (float)(100.0 + bandPeak * (1.0 - d));
        }
        return (wl, inten);
    }

    [Fact]
    public void Zero_fraction_changes_nothing()
    {
        var (wl, inten) = Synthetic(500);
        var outp = InjectN2(wl, inten, 0.0);
        Assert.Equal(inten, outp);
        Assert.NotSame(inten, outp);      // still a copy — the caller's frame is never mutated
    }

    [Fact]
    public void The_band_scales_and_the_continuum_does_not()
    {
        var (wl, inten) = Synthetic(500);
        var outp = InjectN2(wl, inten, 0.20);

        int peak = Array.FindIndex(wl, w => Math.Abs(w - 337.40) < 0.026);
        // Continuum is 100, band excess at the peak is 500 → 100 + 500·1.2 = 700.
        Assert.Equal(600.0, inten[peak], 1);
        Assert.Equal(700.0, outp[peak], 1);

        int far = Array.FindIndex(wl, w => w > 345.0);     // outside the injection bounds
        Assert.Equal(inten[far], outp[far]);
    }

    [Fact]
    public void A_frame_with_no_band_gains_nothing()
    {
        var (wl, inten) = Synthetic(bandPeak: 0);          // continuum only
        var outp = InjectN2(wl, inten, 0.50);
        Assert.Equal(inten, outp);
    }

    [Fact]
    public void A_negative_fraction_reduces_the_band()
    {
        var (wl, inten) = Synthetic(500);
        var outp = InjectN2(wl, inten, -0.30);
        int peak = Array.FindIndex(wl, w => Math.Abs(w - 337.40) < 0.026);
        Assert.Equal(450.0, outp[peak], 1);                // 100 + 500·0.7
    }

    // ---------------------------------------------------------------- on the real recordings

    /// <summary>The 2026-08-20 17:04–17:09 cycle: one step of each process, back to back.</summary>
    private static readonly (string Process, string File)[] References =
    {
        ("B", "P_OES1_0820170407.csv"),
        ("A", "P_OES1_0820170737.csv"),
        ("C", "P_OES1_0820170833.csv"),
    };

    private const string DirVariable = "OES_TEST_RECORDING_DIR";
    private const string DefaultDir = @"C:\DualOES\202608\20";

    private static string? ResolveDir()
    {
        var configured = Environment.GetEnvironmentVariable(DirVariable);
        var dir = string.IsNullOrWhiteSpace(configured) ? DefaultDir : configured;
        return References.All(r => File.Exists(Path.Combine(dir, r.File))) ? dir : null;
    }

    private static string SkipReason =>
        $"the 2026-08-20 17:04–17:09 recordings are not present — set {DirVariable} to a folder " +
        $"holding {string.Join(", ", References.Select(r => r.File))}";

    /// <summary>
    /// Median of N₂ 337 ÷ CO 329.6 over the plan's sampling window, with a rise injected into
    /// every frame. The window is 10–30 s from the recording's first row; the recorder back-fills
    /// at most <c>StartConfirmSeconds</c> (2 s by default) ahead of the gate opening, so that is
    /// within a frame or two of "10–30 s from gate open", which is what the plan specifies.
    /// </summary>
    private static double WindowMedian(string path, double fraction)
    {
        var run = RecordedRun.Load(path);
        var wl = run.Wavelengths;
        var epoch = new DateTime(2026, 1, 1);
        var num = N2_337; var den = CO_329;
        var vals = new List<double>();
        for (int i = 0; i < run.FrameCount; i++)
        {
            var f = run.Frame(i, epoch);
            double t = (f.Timestamp - epoch).TotalSeconds;
            if (t < 10 || t >= 30) continue;
            var inten = fraction == 0 ? f.Intensities : InjectN2(wl, f.Intensities, fraction);
            double a = LineIntensityExtractor.Extract(wl, inten, num).Value;
            double b = LineIntensityExtractor.Extract(wl, inten, den).Value;
            if (b > 0 && !double.IsNaN(a)) vals.Add(a / b);
        }
        vals.Sort();
        return vals.Count == 0 ? double.NaN : vals[vals.Count / 2];
    }

    /// <summary>
    /// The response curve: what fraction of a rise in the band survives the extraction.
    ///
    /// <para>Not one-for-one, and not the same in every process. The N₂ 337 band's red-degraded
    /// tail runs under the right-hand window the extractor draws its continuum from, so part of
    /// any added light is subtracted back out. The loss is a fixed fraction — the extraction is
    /// linear, so the transfer is constant across the sweep — but it differs by process, because
    /// the share of the band lying under the continuum windows does: the weaker the band relative
    /// to its own continuum, the more of it is treated as continuum.</para>
    ///
    /// <para><b>It moves the plan's number.</b> The GO/NO-GO threshold of 9.3 % is stated on the
    /// reported indicator, so the <em>band</em> has to rise by 9.3 % ÷ transfer — about 9.5 % in
    /// process C and 10.6 % in A. Small, but it is a real bias and it is in the pessimistic
    /// direction, so it belongs in the acceptance arithmetic rather than in a footnote.</para>
    /// </summary>
    [SkippableFact]
    public void Injected_rise_is_reported_almost_one_for_one()
    {
        var dir = ResolveDir();
        Skip.If(dir is null, SkipReason);

        double[] fractions = { 0.02, 0.05, 0.10, 0.20, 0.30 };
        _out.WriteLine("injected % -> reported % on N2 337 / CO 329.6 (10-30 s window median)");

        foreach (var (process, file) in References)
        {
            var path = Path.Combine(dir!, file);
            double baseline = WindowMedian(path, 0.0);
            Assert.False(double.IsNaN(baseline), $"{file}: no usable frames in the window");

            var reported = new List<double>();
            foreach (var f in fractions)
                reported.Add(WindowMedian(path, f) / baseline - 1.0);

            _out.WriteLine($"  {process} ({file}) base={baseline:F5}");
            for (int i = 0; i < fractions.Length; i++)
                _out.WriteLine($"    inject {100 * fractions[i],5:F1} % -> report {100 * reported[i],6:F2} %" +
                               $"  (transfer {reported[i] / fractions[i]:F3})");

            for (int i = 1; i < reported.Count; i++)
                Assert.True(reported[i] > reported[i - 1],
                    $"{file}: reported rise is not monotonic at {fractions[i]:P0}");
            Assert.True(reported[0] > 0, $"{file}: a 2 % rise did not read as a rise at all");

            // Constant, because the extraction is linear. A transfer that drifts with the
            // injected fraction means the injection bounds are touching something the
            // denominator reads — that is how the first version of this harness reported more
            // rise than it injected. See BandInjector.
            var transfers = reported.Select((r, i) => r / fractions[i]).ToList();
            Assert.True(transfers.Max() - transfers.Min() < 0.01,
                $"{file}: transfer drifts with the injected fraction " +
                $"({transfers.Min():F3}–{transfers.Max():F3}) — check the injection bounds");

            // Some is lost to the continuum windows. Below 0.8 the window geometry would be
            // throwing away enough to be worth re-cutting before any tool time is spent.
            Assert.InRange(transfers[0], 0.80, 1.00);
        }
    }

    // ---------------------------------------------------------------- the chain, end to end

    private static LeakMonitorEngine BuildEngine(out LeakMonitorSettings settings)
    {
        settings = new LeakMonitorSettings
        {
            Enabled = true,
            SuppressAlarmsInTestMode = false,   // replayed frames; the transitions are the point
            RequireTwoForAlarm = false,         // one ratio here
            Ratios =
            {
                new RatioDefinition
                {
                    Key = "R_N2CO_C", DisplayName = "N2 337 / CO 330 (C)",
                    Enabled = true, MonitorMode = MonitorMode.Ratio,
                    Numerator = N2_337, Denominator = CO_329,
                    WarnFactor = 1.05, AlarmFactor = 1.12,
                    SigmaWarn = 3, SigmaAlarm = 6,
                    EmaTauSeconds = 5, ConfirmSeconds = 15, MinSnr = 3,
                },
            },
        };
        var engine = new LeakMonitorEngine(settings);
        engine.ConfigureTrigger(new LoggerSettings
        {
            TriggerMode = TriggerMode.SpectrumPercentile,
            TriggerPercentile = 99,
            SaveStartThresholdIntensity = 100,
        });
        return engine;
    }

    private const double InjectFromSeconds = 40.0;

    /// <summary>Replays the process-C step, baseline captured from its head, with
    /// <paramref name="rise"/> injected from <see cref="InjectFromSeconds"/> onward.</summary>
    private List<(double T, RatioSnapshot R)> ReplayWithInjection(string dir, double rise)
    {
        var path = Path.Combine(dir, References.Single(r => r.Process == "C").File);
        var run = RecordedRun.Load(path);
        var epoch = new DateTime(2026, 1, 1);

        using var engine = BuildEngine(out _);
        var seen = new List<(double, RatioSnapshot)>();
        engine.SampleProcessed += (_, s) =>
            seen.Add(((s.Timestamp - epoch).TotalSeconds, s.Ratios[0]));

        engine.BeginGoldenRunCapture("injection-baseline", seconds: 20);
        for (int i = 0; i < run.FrameCount; i++)
        {
            var f = run.Frame(i, epoch);
            double t = (f.Timestamp - epoch).TotalSeconds;
            var inten = t < InjectFromSeconds
                ? f.Intensities
                : InjectN2(f.Wavelengths, f.Intensities, rise);
            engine.ProcessSample(new SpectrumSample
            {
                Timestamp = f.Timestamp,
                Wavelengths = f.Wavelengths,
                Intensities = inten,
                IntegrationTime = f.IntegrationTime,
                AverageCount = f.AverageCount,
                SerialNumber = f.SerialNumber,
                IsTestMode = true,
            });
        }
        return seen;
    }

    /// <summary>
    /// The measurement chain carries the rise: extraction, the ratio, the EMA and the
    /// % -of-baseline all move by what was injected, on real spectra with the real axis and the
    /// real noise.
    ///
    /// <para>This is the half of the GO/NO-GO an offline harness can settle. It says nothing
    /// about how large a leak has to be to produce the rise.</para>
    /// </summary>
    [SkippableFact]
    public void A_sustained_rise_propagates_through_the_chain()
    {
        var dir = ResolveDir();
        Skip.If(dir is null, SkipReason);

        var seen = ReplayWithInjection(dir!, rise: 0.25);

        var before = seen.Where(x => x.T is >= 24 and < InjectFromSeconds)
                         .Select(x => x.R.PercentOfBaseline)
                         .Where(v => !double.IsNaN(v)).ToList();
        var after = seen.Where(x => x.T is >= 50 and <= 72)
                        .Select(x => x.R.PercentOfBaseline)
                        .Where(v => !double.IsNaN(v)).ToList();

        Assert.NotEmpty(before);
        Assert.NotEmpty(after);
        _out.WriteLine($"% of baseline: before {before.Average():F1}, after {after.Average():F1}");

        Assert.InRange(before.Average(), 90, 110);   // sitting on its own baseline
        Assert.True(after.Average() > 118,
            $"a 25 % rise moved % -of-baseline only to {after.Average():F1}");
    }

    /// <summary>
    /// <b>A step change widens its own Warn threshold, and on a step this short it never
    /// narrows back.</b>
    ///
    /// <para>The Warn threshold is <c>mean + SigmaWarn · max(baselineσ, liveσ)</c>, and the live
    /// σ is an EWMA of the raw ratio's scatter. A step change is scatter: the jump inflates the
    /// live σ on the frame it happens, so the threshold rises with the signal and the margin
    /// barely opens. It decays back over roughly <c>2τ·ln(liveσ/baseσ)</c> — the same mechanism
    /// <c>docs/ratio-csv-sigma-score-zh-TW.md</c> §4.1 works through for the σ-score — but with
    /// τ = 5 s that is longer than what remains of an 84 s process step.</para>
    ///
    /// <para><b>Consequence for the plan.</b> The per-step coarse screen R7 kept as a secondary
    /// role cannot work on this chamber's step lengths with these settings: a real leak arriving
    /// as a step will not confirm a Warning inside one step. The cross-batch comparison, which
    /// is the plan's primary mechanism, is untouched — it compares window medians and uses
    /// neither the EMA nor the live σ. Deciding between a shorter τ, standing the live-σ widening
    /// down, and dropping the per-step screen is a P3 decision; this test exists so it is made
    /// deliberately rather than discovered on the tool.</para>
    ///
    /// <para>If this test starts failing, the behaviour changed on purpose or by accident —
    /// read the plan before adjusting the numbers here.</para>
    /// </summary>
    [SkippableFact]
    public void A_step_change_widens_its_own_warn_threshold()
    {
        var dir = ResolveDir();
        Skip.If(dir is null, SkipReason);

        var seen = ReplayWithInjection(dir!, rise: 0.25);

        double WarnAt(double t) => seen.Where(x => x.T <= t).Select(x => x.R.WarnThreshold)
                                       .Last(v => !double.IsNaN(v));

        double warnBefore = WarnAt(InjectFromSeconds - 0.5);
        double warnAfter = WarnAt(InjectFromSeconds + 0.5);
        _out.WriteLine($"warn threshold: before {warnBefore:F5}, on the step {warnAfter:F5} " +
                       $"({warnAfter / warnBefore - 1:P0})");

        Assert.True(warnAfter > warnBefore * 1.15,
            "the step did not widen its own threshold — the live-σ widening this test documents " +
            "may have been removed; see the plan before changing the expectation");

        _out.WriteLine("t / ema / warn / margin after the step:");
        foreach (var (t, r) in seen.Where(x => x.T >= InjectFromSeconds && x.T <= 74))
            _out.WriteLine($"  t={t,5:F1} ema={r.SmoothedRatio:F5} warn={r.WarnThreshold:F5} " +
                           $"margin={(r.SmoothedRatio / r.WarnThreshold - 1) * 100,6:F1} % {r.State}");

        // ...and the consequence: no Warning is confirmed before the step ends.
        Assert.DoesNotContain(seen, x => x.T >= InjectFromSeconds &&
                                         x.R.State is RatioState.Warning or RatioState.Alarm);
    }
}
