using System.Linq;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// Ratio Setup and the two judging paths (#95): what a save keeps, and what it refuses.
///
/// <para>The trap this guards is the one #88 found for line windows: Ratio Setup rebuilds every
/// definition on Save, so a field the editor does not carry is reset on every ratio — including
/// ones nobody opened — with nothing on screen to say so. For step judging that would turn the
/// rolling-baseline path off on the lab machine the first time anyone saved the tab.</para>
/// </summary>
public class RatioSetupJudgingTests
{
    private static LineRegion Region(string label, double nm, LineExtractMode mode) => new()
    {
        Label = label, CenterNm = nm, HalfWidthNm = 1.2,
        BaselineGapNm = 4, BaselineWidthNm = 2, Mode = mode, PeakSearchHalfWidthNm = 0,
    };

    /// <summary>A step-judged ratio on a user line (exact centre kept) and one whose numerator is
    /// a centre no catalog or user line names, so Save rebuilds its region.</summary>
    private static (LeakMonitorSettings settings, RatioSetupViewModel vm, int[] saves) Tab()
    {
        var s = new LeakMonitorSettings { Enabled = true };
        s.UserSpectralLines.Add(new UserSpectralLine { Species = "uNO", WavelengthNm = 235.6 });
        s.Ratios.Add(new RatioDefinition
        {
            Key = "R_NO", DisplayName = "NO 235.6 / Ar 750.4", MonitorMode = MonitorMode.Ratio,
            Numerator = Region("uNO 235.6", 235.6, LineExtractMode.Integral),
            Denominator = Region("Ar 750.4", 750.4, LineExtractMode.RawMean),
            GoldenRunJudging = false, StepJudging = true, StepWarnRise = 0.10, StepAlarmRise = 0.15,
        });
        s.Ratios.Add(new RatioDefinition
        {
            Key = "R_OH", DisplayName = "OH / Ar", MonitorMode = MonitorMode.Ratio,
            Numerator = Region("OH 308.8", 308.8, LineExtractMode.Integral),   // not a catalog centre
            Denominator = Region("Ar 750.4", 750.4, LineExtractMode.RawMean),
            GoldenRunJudging = true, StepJudging = true, StepWarnRise = 0.02, StepAlarmRise = 0.03,
        });
        var engine = new LeakMonitorEngine(s);
        var saves = new int[1];
        var vm = new RatioSetupViewModel(engine, () => saves[0]++);
        vm.SetRole(engineerOrHigher: true);
        return (s, vm, saves);
    }

    private static RatioEditViewModel Row(RatioSetupViewModel vm, string key) =>
        vm.Ratios.First(r => r.ToDefinition().Key == key);

    [Fact]
    public void Saving_keeps_the_judging_settings_of_every_ratio_including_untouched_and_rebuilt_ones()
    {
        var (s, vm, saves) = Tab();
        Row(vm, "R_NO").WarnFactor = 1.3;              // edit something unrelated on one row only
        vm.SaveCommand.Execute(null);
        Assert.Equal(1, saves[0]);

        var no = s.Ratios.First(r => r.Key == "R_NO");
        Assert.False(no.GoldenRunJudging);
        Assert.True(no.StepJudging);
        Assert.Equal(0.10, no.StepWarnRise, 9);
        Assert.Equal(0.15, no.StepAlarmRise, 9);

        var oh = s.Ratios.First(r => r.Key == "R_OH");    // untouched; its region gets rebuilt
        Assert.True(oh.GoldenRunJudging);
        Assert.True(oh.StepJudging);
        Assert.Equal(0.02, oh.StepWarnRise, 9);
        Assert.Equal(0.03, oh.StepAlarmRise, 9);
    }

    [Fact]
    public void The_editor_shows_rises_in_percent_and_stores_fractions()
    {
        var (s, vm, _) = Tab();
        var row = Row(vm, "R_NO");
        Assert.Equal(10, row.StepWarnRisePercent, 9);
        row.StepAlarmRisePercent = 20;
        vm.SaveCommand.Execute(null);
        Assert.Equal(0.20, s.Ratios.First(r => r.Key == "R_NO").StepAlarmRise, 9);
    }

    [Fact]
    public void A_ratio_with_neither_path_is_refused_and_pointed_at_TrendOnly()
    {
        var (s, vm, saves) = Tab();
        Row(vm, "R_NO").StepJudging = false;           // Golden Run was already off
        vm.SaveCommand.Execute(null);

        Assert.Equal(0, saves[0]);
        Assert.Contains("TrendOnly", vm.StatusMessage);
        Assert.Equal("R_NO", vm.SelectedRatio!.ToDefinition().Key);
        Assert.True(s.Ratios.First(r => r.Key == "R_NO").StepJudging);   // nothing written

        Row(vm, "R_NO").Role = RatioRole.TrendOnly;    // the way out the message names
        vm.SaveCommand.Execute(null);
        Assert.Equal(1, saves[0]);
    }

    [Theory]
    [InlineData(0.0, 15.0)]
    [InlineData(10.0, 5.0)]
    public void Step_rises_must_put_Alarm_at_or_above_a_positive_Warn(double warnPct, double alarmPct)
    {
        var (_, vm, saves) = Tab();
        var row = Row(vm, "R_NO");
        row.StepWarnRisePercent = warnPct;
        row.StepAlarmRisePercent = alarmPct;
        vm.SaveCommand.Execute(null);
        Assert.Equal(0, saves[0]);
        Assert.Contains("rise", vm.StatusMessage);
    }

    [Fact]
    public void Step_judging_is_unavailable_for_a_raw_reading_and_the_Golden_Run_fields_grey_only_when_step_is_alone()
    {
        var (_, vm, _) = Tab();
        var row = Row(vm, "R_NO");
        Assert.True(row.StepAvailable);
        Assert.False(row.GoldenRunFieldsEnabled);      // step only
        row.GoldenRunJudging = true;
        Assert.True(row.GoldenRunFieldsEnabled);

        row.SignalMode = LineExtractMode.RawMean;       // now carries the pedestal
        Assert.False(row.StepAvailable);
        row.GoldenRunJudging = false;
        Assert.Contains("raw", row.JudgingError);       // step cannot stand in for it
    }
}
