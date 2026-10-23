using System;
using System.Collections.Generic;
using System.Linq;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// The ratio slot table the engine publishes for the host (#46): slot N is row N of Ratio Setup,
/// and slot offsets 16–25 say what that slot measures. A host keys its history on that identity,
/// so it must be right without a single frame, and a change to it — and only a change to it —
/// must say "re-read" (CEID 507). Retuning a threshold changes nothing the host labelled.
/// </summary>
public class RatioSlotTableTests
{
    private static LeakMonitorEngine Engine(LeakMonitorSettings s) => new(s);

    [Fact]
    public void FactoryRatioSet_HasItsIdentityBeforeAnyFrame()
    {
        var engine = Engine(LeakMonitorSettings.CreateDefault());
        var table = engine.SlotTable;

        var s1 = table.Slot(1)!;
        Assert.Equal("R_N2Ar", s1.Key);
        Assert.Equal("N₂ 337 / Ar 750", s1.DisplayName);
        Assert.True(s1.Enabled);
        Assert.Equal(MonitorMode.Ratio, s1.MonitorMode);
        Assert.True(s1.ValueHasPedestal);
        Assert.Equal(LineExtractMode.RawMean, s1.ExtractMode);
        Assert.Equal(337.1, s1.SignalWavelengthNm, 6);
        Assert.Equal(750.4, s1.ReferenceWavelengthNm, 6);
        Assert.Equal("N2", s1.SignalSpecies);
        Assert.Equal("Ar", s1.ReferenceSpecies);

        var s3 = table.Slot(3)!;
        Assert.False(s3.ValueHasPedestal);
        Assert.Equal(LineExtractMode.PeakHeight, s3.ExtractMode);

        // Absolute intensity divides by nothing, so it names no reference.
        var s4 = table.Slot(4)!;
        Assert.Equal("R_N2357Abs", s4.Key);
        Assert.False(s4.Enabled);
        Assert.Equal(MonitorMode.AbsoluteIntensity, s4.MonitorMode);
        Assert.Equal(0.0, s4.ReferenceWavelengthNm);
        Assert.Equal("", s4.ReferenceSpecies);

        Assert.Null(table.Slot(5));
        Assert.Null(table.Slot(RatioSlotTable.SlotCount));
    }

    [Fact]
    public void Wavelengths_AreTheCorrectedOnes_AndSettingsKeepTheCatalogValue()
    {
        var settings = LeakMonitorSettings.CreateDefault();
        settings.WavelengthCorrections.Add(new WavelengthCorrection { Species = "N2", WavelengthNm = 337.1, OffsetNm = 0.10 });
        settings.WavelengthCorrections.Add(new WavelengthCorrection { Species = "Ar", WavelengthNm = 750.4, OffsetNm = 0.30 });

        var slot = Engine(settings).SlotTable.Slot(1)!;

        Assert.Equal(337.2, slot.SignalWavelengthNm, 6);
        Assert.Equal(750.7, slot.ReferenceWavelengthNm, 6);
        Assert.Equal(337.1, settings.Ratios[0].Numerator.CenterNm, 6);
        Assert.Equal(750.4, settings.Ratios[0].Denominator.CenterNm, 6);
    }

    [Fact]
    public void UserLineSpecies_KeepsItsMarker()
    {
        var settings = LeakMonitorSettings.CreateDefault();
        settings.Ratios[2].Numerator.Label = "uN2 357.7";

        Assert.Equal("uN2", Engine(settings).SlotTable.Slot(3)!.SignalSpecies);
    }

    [Fact]
    public void Reordering_MovesEachKeyToItsNewSlot()
    {
        var settings = LeakMonitorSettings.CreateDefault();
        var engine = Engine(settings);

        var first = settings.Ratios[0];
        settings.Ratios.RemoveAt(0);
        settings.Ratios.Insert(2, first);   // R_NOAr, R_N2357Ar, R_N2Ar, R_N2357Abs
        engine.ReloadRatios();

        var table = engine.SlotTable;
        Assert.Equal(new[] { "R_NOAr", "R_N2357Ar", "R_N2Ar", "R_N2357Abs" },
            table.Slots.Select(s => s.Key).ToArray());
        Assert.Equal(3, table.SlotOf("R_N2Ar"));
        Assert.Equal(337.1, table.Slot(3)!.SignalWavelengthNm, 6);
        Assert.Equal(0, table.SlotOf("nope"));
    }

    /// <summary>Reloads after <paramref name="edit"/> and returns how many times the engine asked
    /// for CEID 507.</summary>
    private static int IdentityChangesAfter(Action<LeakMonitorSettings> edit)
    {
        var settings = LeakMonitorSettings.CreateDefault();
        var engine = Engine(settings);
        int raised = 0;
        RatioSlotTable? published = null;
        engine.SlotIdentityChanged += t => { raised++; published = t; };

        edit(settings);
        engine.ReloadRatios();

        if (raised > 0) Assert.Same(engine.SlotTable, published);
        return raised;
    }

    [Fact]
    public void ReloadingUnchanged_DoesNotNotify() =>
        Assert.Equal(0, IdentityChangesAfter(_ => { }));

    [Fact]
    public void RetuningThresholdsAndSmoothing_DoesNotNotify() =>
        Assert.Equal(0, IdentityChangesAfter(s =>
        {
            foreach (var r in s.Ratios)
            {
                r.WarnFactor = 1.3; r.AlarmFactor = 1.6;
                r.SigmaWarn = 4; r.SigmaAlarm = 8;
                r.EmaTauSeconds = 9; r.ConfirmSeconds = 30; r.MinSnr = 2;
            }
        }));

    public static IEnumerable<object[]> IdentityEdits() => new[]
    {
        new object[] { "signal line", (Action<LeakMonitorSettings>)(s => s.Ratios[1].Numerator.CenterNm = 238.0) },
        new object[] { "reference line", (Action<LeakMonitorSettings>)(s => s.Ratios[0].Denominator.CenterNm = 811.5) },
        new object[] { "extraction", (Action<LeakMonitorSettings>)(s => s.Ratios[2].Numerator.Mode = LineExtractMode.Integral) },
        new object[] { "monitor mode", (Action<LeakMonitorSettings>)(s => s.Ratios[2].MonitorMode = MonitorMode.AbsoluteIntensity) },
        new object[] { "enabled", (Action<LeakMonitorSettings>)(s => s.Ratios[3].Enabled = true) },
        new object[] { "display name", (Action<LeakMonitorSettings>)(s => s.Ratios[0].DisplayName = "renamed") },
        new object[] { "removed", (Action<LeakMonitorSettings>)(s => s.Ratios.RemoveAt(3)) },
        new object[] { "added", (Action<LeakMonitorSettings>)(s =>
            { var c = s.Ratios[0].Clone(); c.Key = "R_new"; s.Ratios.Add(c); }) },
        new object[] { "reordered", (Action<LeakMonitorSettings>)(s =>
            { var r = s.Ratios[0]; s.Ratios.RemoveAt(0); s.Ratios.Add(r); }) },
        new object[] { "wavelength correction", (Action<LeakMonitorSettings>)(s =>
            s.WavelengthCorrections.Add(new WavelengthCorrection { Species = "NO", WavelengthNm = 237.0, OffsetNm = 0.2 })) },
    };

    [Theory]
    [MemberData(nameof(IdentityEdits))]
    public void ChangingWhatASlotMeasures_NotifiesOnce(string what, Action<LeakMonitorSettings> edit) =>
        Assert.True(IdentityChangesAfter(edit) == 1, $"{what}: expected one CEID 507 notification");

    [Fact]
    public void AnEleventhRatio_IsNotASlot_SoItDoesNotNotify()
    {
        var settings = LeakMonitorSettings.CreateDefault();
        while (settings.Ratios.Count < RatioSlotTable.SlotCount + 1)
        {
            var c = settings.Ratios[0].Clone();
            c.Key = $"R_{settings.Ratios.Count}";
            settings.Ratios.Add(c);
        }
        var engine = Engine(settings);
        int raised = 0;
        engine.SlotIdentityChanged += _ => raised++;

        settings.Ratios[^1].DisplayName = "beyond the SVID range";
        engine.ReloadRatios();

        Assert.Equal(0, raised);
    }
}
