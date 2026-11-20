using System;
using System.Collections.Generic;
using System.Linq;
using Aqst.OesSpectrometer.Models;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// Line windows (#88). Ratio Setup used to rebuild every region on Save unless the catalog
/// wavelength equalled its centre, so a tuned region on a non-catalog centre — NO 235.6 and
/// OH 308.8 on the lab machine — was reset by any save, edited or not. And a Golden Run never
/// looked at the windows, so a changed window kept a baseline for a different number.
/// </summary>
public class LineWindowTests
{
    // --- Ratio Setup save -----------------------------------------------------

    private static LineRegion Tuned(string label, double nm, double hw, double gap, double width,
                                    LineExtractMode mode = LineExtractMode.Integral, double search = 0) => new()
    {
        Label = label, CenterNm = nm, HalfWidthNm = hw, BaselineGapNm = gap, BaselineWidthNm = width,
        Mode = mode, PeakSearchHalfWidthNm = search,
    };

    private static RatioDefinition LabRatio(string key, string name, LineRegion num) => new()
    {
        Key = key, DisplayName = name, MonitorMode = MonitorMode.Ratio,
        Numerator = num,
        Denominator = Tuned("Ar 750.4", 750.4, 0.5, 1, 1, LineExtractMode.RawMean),
    };

    /// <summary>The lab machine after 11/19, without the user-line workaround: neither centre
    /// is in the catalog (NO 237, OH 308.9 are).</summary>
    private static (LeakMonitorSettings s, RatioSetupViewModel vm, int[] saves) Tab(GoldenRun? run = null)
    {
        var s = new LeakMonitorSettings { Enabled = true };
        s.Ratios.Add(LabRatio("R_NO", "NO 235.6 / Ar 750.4", Tuned("NO 235.6", 235.6, 1.2, 4, 2)));
        s.Ratios.Add(LabRatio("R_OH", "OH 308.8 / Ar 750.4", Tuned("OH 308.8", 308.8, 1.5, 6.7, 2)));
        if (run is not null) { s.GoldenRuns.Add(run); s.ActiveGoldenRun = run.Name; }
        var engine = new LeakMonitorEngine(s);
        var saves = new int[1];
        var vm = new RatioSetupViewModel(engine, () => saves[0]++);
        vm.SetRole(engineerOrHigher: true);
        return (s, vm, saves);
    }

    private static RatioEditViewModel Row(RatioSetupViewModel vm, string key) =>
        vm.Ratios.First(r => r.ToDefinition().Key == key);

    private static void AssertSame(LineRegion expected, LineRegion actual)
    {
        Assert.Equal(expected.Label, actual.Label);
        Assert.Equal(expected.CenterNm, actual.CenterNm);
        Assert.Equal(expected.WindowKey, actual.WindowKey);
    }

    [Fact]
    public void Saving_keeps_tuned_regions_on_non_catalog_centres_edited_row_or_not()
    {
        var (s, vm, saves) = Tab();
        var noBefore = s.Ratios[0].Numerator.Clone();
        var ohBefore = s.Ratios[1].Numerator.Clone();

        Row(vm, "R_NO").WarnFactor = 1.3;    // an unrelated edit on one row
        vm.SaveCommand.Execute(null);

        Assert.Equal(1, saves[0]);
        AssertSame(noBefore, s.Ratios.First(r => r.Key == "R_NO").Numerator);
        AssertSame(ohBefore, s.Ratios.First(r => r.Key == "R_OH").Numerator);
    }

    [Fact]
    public void Edited_windows_are_saved()
    {
        var (s, vm, _) = Tab();
        var oh = Row(vm, "R_OH");
        Assert.Equal(6.7, oh.SignalBaselineGapNm);   // the tuned value is what the editor shows
        oh.SignalBaselineGapNm = 7.5;
        oh.ReferenceHalfWidthNm = 0.8;
        vm.SaveCommand.Execute(null);

        var saved = s.Ratios.First(r => r.Key == "R_OH");
        Assert.Equal(7.5, saved.Numerator.BaselineGapNm);
        Assert.Equal("OH 308.8", saved.Numerator.Label);     // still the same line
        Assert.Equal(308.8, saved.Numerator.CenterNm);
        Assert.Equal(0.8, saved.Denominator.HalfWidthNm);
    }

    [Fact]
    public void Picking_another_line_resets_its_windows_to_the_modes_defaults()
    {
        var (_, vm, _) = Tab();
        var oh = Row(vm, "R_OH");
        oh.SignalLine = vm.LineCatalog.Cast<SpectralLineOption>().First(o => o.Species == "Ar" && Math.Abs(o.WavelengthNm - 811.5) < 0.1);
        Assert.Equal(1.0, oh.SignalHalfWidthNm);       // Integral default
        Assert.Equal(1.0, oh.SignalBaselineGapNm);
        Assert.Equal(1.0, oh.SignalBaselineWidthNm);
        Assert.Equal(1.0, oh.SignalPeakSearchNm);
        Assert.StartsWith("Ar 811", oh.ToDefinition().Numerator.Label);
    }

    [Fact]
    public void Changing_only_the_mode_keeps_where_the_windows_are()
    {
        var (_, vm, _) = Tab();
        var oh = Row(vm, "R_OH");
        oh.SignalMode = LineExtractMode.PeakHeight;
        Assert.Equal(1.5, oh.SignalHalfWidthNm);
        Assert.Equal(6.7, oh.SignalBaselineGapNm);
        Assert.Equal(2, oh.SignalBaselineWidthNm);
        Assert.Equal(308.8, oh.ToDefinition().Numerator.CenterNm);

        oh.SignalMode = LineExtractMode.RawMean;          // a raw reading never peak-searches
        Assert.Equal(0, oh.SignalPeakSearchNm);
        Assert.False(oh.SignalPeakSearchEditable);
        oh.SignalMode = LineExtractMode.Integral;
        Assert.Equal(1.0, oh.SignalPeakSearchNm);
        Assert.True(oh.SignalPeakSearchEditable);
    }

    [Theory]
    [InlineData("SignalHalfWidthNm", 0.0, "signal half-width")]
    [InlineData("SignalBaselineWidthNm", 0.0, "signal baseline width")]
    [InlineData("SignalBaselineGapNm", -1.0, "signal baseline gap")]
    [InlineData("ReferencePeakSearchNm", 25.0, "reference peak search")]
    public void An_out_of_range_window_is_refused_naming_the_field(string field, double value, string named)
    {
        var (s, vm, saves) = Tab();
        var row = Row(vm, "R_NO");
        typeof(RatioEditViewModel).GetProperty(field)!.SetValue(row, value);
        vm.SaveCommand.Execute(null);
        Assert.Equal(0, saves[0]);
        Assert.Contains(named, vm.StatusMessage);
        Assert.Equal(1.2, s.Ratios[0].Numerator.HalfWidthNm);   // nothing written
    }

    [Fact]
    public void Save_names_the_ratios_whose_Golden_Run_baseline_the_edit_invalidates()
    {
        var run = new GoldenRun { Name = "Recipe 3" };
        run.Baselines.Add(new GoldenRunRatioBaseline { Key = "R_NO", Mean = 0.02, Sigma = 0.001 });
        run.Baselines.Add(new GoldenRunRatioBaseline { Key = "R_OH", Mean = 0.3, Sigma = 0.002 });
        var (_, vm, saves) = Tab(run);

        Row(vm, "R_OH").SignalBaselineGapNm = 7.0;
        Row(vm, "R_NO").WarnFactor = 1.4;               // not a window change
        vm.SaveCommand.Execute(null);

        Assert.Equal(1, saves[0]);
        Assert.Contains("OH 308.8 / Ar 750.4", vm.StatusMessage);
        Assert.DoesNotContain("NO 235.6", vm.StatusMessage);
        Assert.Contains("capture a new Golden Run", vm.StatusMessage);
    }

    // --- Golden Run acceptance ------------------------------------------------

    private const double LineNm = 350.0;
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);

    private static SpectrumSample Frame(double seconds)
    {
        const int n = 1000;
        var wl = new float[n];
        var inten = new float[n];
        for (int i = 0; i < n; i++)
        {
            wl[i] = (float)(300.0 + i * 0.5);
            inten[i] = 1000f + (i % 2 == 0 ? 1f : -1f);
            if (Math.Abs(wl[i] - LineNm) <= 0.5) inten[i] = 3000f;
        }
        return new SpectrumSample
        {
            Timestamp = Epoch.AddSeconds(seconds), Wavelengths = wl, Intensities = inten,
            IntegrationTime = 0, AverageCount = 0, SerialNumber = "T", IsSimulated = true,
        };
    }

    private static RatioDefinition Probe(double halfWidth = 0.5) => new()
    {
        Key = "R_X", DisplayName = "X / Y", Enabled = true, MonitorMode = MonitorMode.Ratio, MinSnr = 0,
        EmaTauSeconds = 0.1, ConfirmSeconds = 1,
        Numerator = Tuned("X 350", LineNm, halfWidth, 1, 1, LineExtractMode.PeakHeight),
        Denominator = Tuned("Y 500", 500, 0.5, 1, 1, LineExtractMode.RawMean),
    };

    private static RatioSnapshot Judge(RatioDefinition def, GoldenRunRatioBaseline b, List<WavelengthCorrection>? corrections = null)
    {
        var s = new LeakMonitorSettings { Enabled = true, SuppressAlarmsInTestMode = false };
        s.Ratios.Add(def);
        if (corrections is not null) s.WavelengthCorrections = corrections;
        var run = new GoldenRun { Name = "G" };
        b.Key = def.Key; b.Mean = 3.0; b.Sigma = 0.01; b.ExtractionRevision = LeakMonitorEngine.CurrentExtractionRevision;
        run.Baselines.Add(b);
        s.GoldenRuns.Add(run); s.ActiveGoldenRun = "G";
        using var engine = new LeakMonitorEngine(s);
        engine.ConfigureTrigger(new LoggerSettings
        {
            TriggerMode = TriggerMode.SpectrumPercentile, TriggerPercentile = 99, SaveStartThresholdIntensity = 100,
        });
        LeakMonitorSnapshot? last = null;
        engine.SampleProcessed += (_, snap) => last = snap;
        engine.ProcessSample(Frame(0));
        return last!.Ratios.Single();
    }

    private static GoldenRunRatioBaseline Recorded(RatioDefinition def) => new()
    {
        ReferenceLabel = def.Denominator.Label,
        NumeratorWindows = def.Numerator.WindowKey,
        DenominatorWindows = def.Denominator.WindowKey,
    };

    [Fact]
    public void A_baseline_recorded_with_the_same_windows_is_used()
    {
        Assert.True(Judge(Probe(), Recorded(Probe())).HasBaseline);
    }

    [Fact]
    public void A_baseline_recorded_with_other_windows_is_not_used()
    {
        Assert.False(Judge(Probe(halfWidth: 1.0), Recorded(Probe(halfWidth: 0.5))).HasBaseline);
    }

    [Fact]
    public void A_baseline_from_before_windows_were_recorded_is_accepted_as_before()
    {
        Assert.True(Judge(Probe(halfWidth: 1.0), new GoldenRunRatioBaseline { ReferenceLabel = "Y 500" }).HasBaseline);
    }

    [Fact]
    public void A_wavelength_correction_alone_does_not_cost_the_baseline()
    {
        var corrections = new List<WavelengthCorrection> { new() { Species = "X", WavelengthNm = 350, OffsetNm = 0.2 } };
        Assert.True(Judge(Probe(), Recorded(Probe()), corrections).HasBaseline);
    }

    [Fact]
    public void A_live_capture_records_the_windows()
    {
        var s = new LeakMonitorSettings { Enabled = true, SuppressAlarmsInTestMode = false };
        s.Ratios.Add(Probe());
        using var engine = new LeakMonitorEngine(s);
        engine.ConfigureTrigger(new LoggerSettings
        {
            TriggerMode = TriggerMode.SpectrumPercentile, TriggerPercentile = 99, SaveStartThresholdIntensity = 100,
        });
        engine.BeginGoldenRunCapture("cap", seconds: 2);
        for (double t = 0; t <= 3; t += 0.25) engine.ProcessSample(Frame(t));
        var b = s.GoldenRuns.Single(r => r.Name == "cap").Baselines.Single();
        Assert.Equal(Probe().Numerator.WindowKey, b.NumeratorWindows);
        Assert.Equal(Probe().Denominator.WindowKey, b.DenominatorWindows);
    }
}
