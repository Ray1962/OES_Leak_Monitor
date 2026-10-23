using System.IO;
using Aqusen.Secs;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// The ratio slot SVIDs (Satellite spec §1.4(b), #47): slot N is row N of Ratio Setup, at
/// <c>VID = 100 + (N−1)×30 + offset</c>. Offsets 16–25 read the engine's slot table, so they
/// are right with no frame at all; 01–15 read the last snapshot <em>by key</em>, so a snapshot
/// taken before a reorder cannot put one ratio's numbers in another's slot.
///
/// <para>The template rows themselves are checked in <see cref="SecsProfileTests"/>. Values are
/// read through the same <see cref="SvBindings"/> the equipment answers S1F3 from, with the
/// format the shipped profile gives each offset.</para>
/// </summary>
public class SecsRatioSlotTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "oes-secs-slot-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    // ---- the bindings -------------------------------------------------------

    private SecsBridge Bridge(LeakMonitorEngine engine)
    {
        var bridge = new SecsBridge(null, _folder, _folder, () => default);
        bridge.OnSlotTable(engine.SlotTable);
        return bridge;
    }

    private static float F4(SvBindings b, int slot, string field) =>
        b.Read($"oes.slot{slot}.{field}", "F4").FirstValue<float>();

    private static uint U4(SvBindings b, int slot, string field) =>
        b.Read($"oes.slot{slot}.{field}", "U4").FirstValue<uint>();

    private static string A(SvBindings b, int slot, string field) =>
        b.Read($"oes.slot{slot}.{field}", "A").GetString();

    [Fact]
    public void Identity_is_there_before_any_frame()
    {
        var b = Bridge(new LeakMonitorEngine(LeakMonitorSettings.CreateDefault())).CreateBindings();

        Assert.Equal(1u, U4(b, 1, "enabled"));
        Assert.Equal(0u, U4(b, 1, "monitorMode"));
        Assert.Equal(1u, U4(b, 1, "valueSemantics"));      // RawMean / Ar: a σ-score, ratio mode or not
        Assert.Equal(2u, U4(b, 1, "extractionMode"));
        Assert.Equal(337.1f, F4(b, 1, "signalWavelength"));
        Assert.Equal(750.4f, F4(b, 1, "referenceWavelength"));
        Assert.Equal("N₂ 337 / Ar 750", A(b, 1, "displayName"));
        Assert.Equal("N2", A(b, 1, "signalSpecies"));
        Assert.Equal("Ar", A(b, 1, "referenceSpecies"));
        Assert.Equal("R_N2Ar", A(b, 1, "key"));

        Assert.Equal(0u, U4(b, 3, "valueSemantics"));      // PeakHeight: a percentage
        Assert.Equal(0u, U4(b, 3, "extractionMode"));

        Assert.Equal(0u, U4(b, 4, "enabled"));
        Assert.Equal(1u, U4(b, 4, "monitorMode"));
        Assert.Equal(1u, U4(b, 4, "valueSemantics"));
        Assert.Equal(0f, F4(b, 4, "referenceWavelength"));
        Assert.Equal("", A(b, 4, "referenceSpecies"));

        // No frame yet: an enabled slot reads NoPlasma, a disabled one Disabled, numbers 0.
        Assert.Equal(3u, U4(b, 1, "state"));
        Assert.Equal(6u, U4(b, 4, "state"));
        Assert.Equal(0f, F4(b, 1, "monitoredValue"));
    }

    [Fact]
    public void An_empty_slot_answers_with_zeros_and_Disabled()
    {
        var b = Bridge(new LeakMonitorEngine(LeakMonitorSettings.CreateDefault())).CreateBindings();

        Assert.Equal(0u, U4(b, 5, "enabled"));
        Assert.Equal(6u, U4(b, 5, "state"));
        Assert.Equal(0f, F4(b, 5, "monitoredValue"));
        Assert.Equal(0f, F4(b, 10, "signalWavelength"));
        Assert.Equal("", A(b, 5, "displayName"));
        Assert.Equal("", A(b, 10, "key"));
    }

    [Fact]
    public void A_snapshot_from_before_a_reorder_is_matched_by_key_not_by_position()
    {
        var settings = LeakMonitorSettings.CreateDefault();
        var engine = new LeakMonitorEngine(settings);
        var bridge = Bridge(engine);
        // Taken under the factory order: R_N2Ar first.
        bridge.OnSample(Snapshot(
            Row("R_N2Ar", RatioState.Alarm, raw: 0.9),
            Row("R_NOAr", RatioState.Normal, raw: 0.1)));

        var first = settings.Ratios[0];
        settings.Ratios.RemoveAt(0);
        settings.Ratios.Add(first);                       // R_NOAr, R_N2357Ar, R_N2357Abs, R_N2Ar
        engine.ReloadRatios();
        bridge.OnSlotIdentityChanged(engine.SlotTable);
        var b = bridge.CreateBindings();

        Assert.Equal("R_NOAr", A(b, 1, "key"));
        Assert.Equal(0.1f, F4(b, 1, "monitoredValue"));
        Assert.Equal(0u, U4(b, 1, "state"));
        Assert.Equal(0.9f, F4(b, 4, "monitoredValue"));
        Assert.Equal(2u, U4(b, 4, "state"));
        // R_N2357Ar is in the new table but not in the old snapshot: the no-value rule.
        Assert.Equal(3u, U4(b, 2, "state"));
        Assert.Equal(0f, F4(b, 2, "monitoredValue"));
    }

    [Fact]
    public void Live_values_come_from_the_slot_row_and_a_non_number_goes_out_as_zero()
    {
        var bridge = Bridge(new LeakMonitorEngine(LeakMonitorSettings.CreateDefault()));
        bridge.OnSample(Snapshot(
            Row("R_N2Ar", RatioState.Warning, raw: 0.0625) with
            {
                SmoothedRatio = 0.06, PercentOfBaseline = 131, SlopePerMinute = 4.25,
                NumeratorSnr = double.NaN, DenominatorSnr = 88, NumeratorIntensity = 3745,
                DenominatorIntensity = 60000, BaselineMean = 0.05, BaselineSigma = 0.0004,
                WarnThreshold = 0.0512, AlarmThreshold = 0.0524,
            },
            // Absolute intensity still reads its reference for the plasma gate; the slot says 0.
            Row("R_N2357Abs", RatioState.Disabled, raw: double.PositiveInfinity) with
            {
                Mode = MonitorMode.AbsoluteIntensity, DenominatorSnr = 50, DenominatorIntensity = 9000,
            }));
        var b = bridge.CreateBindings();

        Assert.Equal(0.0625f, F4(b, 1, "monitoredValue"));
        Assert.Equal(0.06f, F4(b, 1, "smoothedValue"));
        Assert.Equal(131f, F4(b, 1, "percentOfBaseline"));
        Assert.Equal(4.25f, F4(b, 1, "slope"));
        Assert.Equal(1u, U4(b, 1, "state"));
        Assert.Equal(0f, F4(b, 1, "signalSnr"));
        Assert.Equal(88f, F4(b, 1, "referenceSnr"));
        Assert.Equal(3745f, F4(b, 1, "signalIntensity"));
        Assert.Equal(60000f, F4(b, 1, "referenceIntensity"));
        Assert.Equal(0.05f, F4(b, 1, "baselineMean"));
        Assert.Equal(0.0004f, F4(b, 1, "baselineSigma"));
        Assert.Equal(0.0512f, F4(b, 1, "warnThreshold"));
        Assert.Equal(0.0524f, F4(b, 1, "alarmThreshold"));

        Assert.Equal(0f, F4(b, 4, "monitoredValue"));
        Assert.Equal(0f, F4(b, 4, "referenceSnr"));
        Assert.Equal(0f, F4(b, 4, "referenceIntensity"));
    }

    [Fact]
    public void Per_ratio_leak_rate_is_found_by_key()
    {
        var bridge = Bridge(new LeakMonitorEngine(LeakMonitorSettings.CreateDefault()));
        bridge.OnSample(new LeakMonitorSnapshot
        {
            Timestamp = new DateTime(2026, 10, 23, 10, 0, 0, DateTimeKind.Local),
            Ratios = new[] { Row("R_N2Ar", RatioState.Normal, raw: 0.05) },
            LeakRate = new LeakRateEstimate
            {
                HasEstimate = true, LeakRate = 2e-4, Sigma = 3e-5, Confidence = 0.8,
                PerRatio = new[]
                {
                    new RatioLeakEstimate("R_NOAr", true, 2e-4, 1e9, false, null),
                    new RatioLeakEstimate("R_N2Ar", false, double.NaN, 0, false, "no calibration fit"),
                },
            },
        });
        var b = bridge.CreateBindings();

        Assert.Equal(2e-4f, F4(b, 2, "leakRate"));
        Assert.Equal(1u, U4(b, 2, "usedInFusion"));
        Assert.Equal(0f, F4(b, 1, "leakRate"));
        Assert.Equal(0u, U4(b, 1, "usedInFusion"));
        Assert.Equal(0f, F4(b, 3, "leakRate"));            // not in PerRatio at all
        Assert.Equal(0u, U4(b, 3, "usedInFusion"));
    }

    // ---- snapshots ----------------------------------------------------------

    private static LeakMonitorSnapshot Snapshot(params RatioSnapshot[] rows) => new()
    {
        Timestamp = new DateTime(2026, 10, 23, 10, 0, 0, DateTimeKind.Local),
        Overall = LeakAlarmLevel.Normal,
        Ratios = rows,
    };

    private static RatioSnapshot Row(string key, RatioState state, double raw) =>
        new(key, key, state,
            RawRatio: raw, SmoothedRatio: raw,
            HasBaseline: true, BaselineMean: 0.05, BaselineSigma: 0.001,
            PercentOfBaseline: 100, WarnThreshold: 0.0525, AlarmThreshold: 0.056,
            SlopePerMinute: 0, PlasmaPresent: true,
            NumeratorIntensity: 900, DenominatorIntensity: 18000,
            RatioNoiseSigma: 0.0008, NumeratorSnr: 40, DenominatorSnr: 120,
            Mode: MonitorMode.Ratio, Role: RatioRole.Alarm, HasPedestal: false);
}
