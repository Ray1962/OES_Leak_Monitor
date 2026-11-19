using System.Linq;
using System.Text.Json;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// How the step-judging settings (#90) arrive from <c>settings.json</c>. The contract is that a
/// file written before they existed behaves exactly as it did: Golden Run judging on, step
/// judging off, factory windows — no migration, nothing to edit.
/// </summary>
public class StepJudgingSettingsTests
{
    /// <summary>Mirrors <c>SettingsService.JsonOptions</c> — camelCase is what is on disk.</summary>
    private static readonly JsonSerializerOptions OnDisk = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private const string BeforeStepJudging = """
        {"leakMonitor":{"requireTwoForAlarm":true,"ratios":[
          {"key":"R_8d476877","displayName":"NO 235.6 / Ar 750.4","enabled":true,
           "numerator":{"label":"uNO 235.6","centerNm":235.6,"halfWidthNm":1.2,"baselineGapNm":4,"baselineWidthNm":2,"mode":"Integral"},
           "denominator":{"label":"Ar 750.4","centerNm":750.4,"halfWidthNm":0.5,"baselineGapNm":1,"baselineWidthNm":1,"mode":"RawMean"},
           "warnFactor":1.1,"alarmFactor":1.2}]}}
        """;

    [Fact]
    public void A_file_written_before_step_judging_loads_as_Golden_Run_only()
    {
        var s = JsonSerializer.Deserialize<AppSettings>(BeforeStepJudging, OnDisk)!.LeakMonitor;
        var r = Assert.Single(s.Ratios);

        Assert.True(r.GoldenRunJudging);
        Assert.False(r.StepJudging);
        Assert.Equal(RatioDefinition.DefaultStepWarnRise, r.StepWarnRise);
        Assert.Equal(RatioDefinition.DefaultStepAlarmRise, r.StepAlarmRise);
        Assert.Equal(LeakMonitorSettings.DefaultStepLookbackSeconds, s.StepLookbackSeconds);
        Assert.Equal(LeakMonitorSettings.DefaultStepGapSeconds, s.StepGapSeconds);
        Assert.Equal(LeakMonitorSettings.DefaultStepMinHistorySeconds, s.StepMinHistorySeconds);
        Assert.Empty(s.NormalizeStepJudging());
    }

    [Fact]
    public void Step_settings_survive_a_save_and_load()
    {
        var s = JsonSerializer.Deserialize<AppSettings>(BeforeStepJudging, OnDisk)!;
        var r = s.LeakMonitor.Ratios[0];
        r.GoldenRunJudging = false;
        r.StepJudging = true;
        r.StepWarnRise = 0.02;
        r.StepAlarmRise = 0.03;
        s.LeakMonitor.StepLookbackSeconds = 240;
        s.LeakMonitor.StepGapSeconds = 30;
        s.LeakMonitor.StepMinHistorySeconds = 25;

        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, OnDisk), OnDisk)!.LeakMonitor;
        var b = back.Ratios[0];
        Assert.False(b.GoldenRunJudging);
        Assert.True(b.StepJudging);
        Assert.Equal(0.02, b.StepWarnRise);
        Assert.Equal(0.03, b.StepAlarmRise);
        Assert.Equal(240, back.StepLookbackSeconds);
        Assert.Equal(30, back.StepGapSeconds);
        Assert.Equal(25, back.StepMinHistorySeconds);
    }

    [Fact]
    public void A_copy_of_a_definition_keeps_its_judging_settings()
    {
        // The engine judges wavelength-corrected clones, so a field Clone drops is one the
        // engine never sees — however correctly it was saved.
        var r = new RatioDefinition { GoldenRunJudging = false, StepJudging = true, StepWarnRise = 0.02, StepAlarmRise = 0.03 };
        var c = r.Clone();
        Assert.False(c.GoldenRunJudging);
        Assert.True(c.StepJudging);
        Assert.Equal(0.02, c.StepWarnRise);
        Assert.Equal(0.03, c.StepAlarmRise);
    }

    [Fact]
    public void The_factory_ratio_set_ships_with_step_judging_off()
    {
        var s = LeakMonitorSettings.CreateDefault();
        Assert.All(s.Ratios, r =>
        {
            Assert.False(r.StepJudging, r.Key);
            Assert.True(r.GoldenRunJudging, r.Key);
        });
    }

    [Fact]
    public void Derived_flags_are_not_written_to_disk()
    {
        var json = JsonSerializer.Serialize(new RatioDefinition { StepJudging = true }, OnDisk);
        Assert.DoesNotContain("stepJudgingActive", json);
        Assert.Contains("\"stepJudging\":true", json);
    }
}
