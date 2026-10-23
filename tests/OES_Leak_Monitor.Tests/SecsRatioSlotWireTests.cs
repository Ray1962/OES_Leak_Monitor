using System.Text.RegularExpressions;
using Aqusen.Secs;
using OES_Leak_Monitor;
using Secs4Net;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// The ratio slot SVIDs against a real host over loopback (#47): S1F3 for identity and live
/// values, the CEID 507 that tells a host to re-read identity, and an S2F23 trace — the last
/// being what shows that 250 more status variables needed nothing new from <c>Aqusen.Secs</c>.
/// Bridge and engine are wired the way <c>MainViewModel</c> wires them.
/// </summary>
[Collection("network")]
public class SecsRatioSlotWireTests
{
    private const uint CeidRatioConfigReloaded = 10227507;

    /// <summary>Slot <paramref name="n"/>, <paramref name="offset"/> at Ch_2.</summary>
    private static uint Svid(int n, int offset) => (uint)(1022700000 + 100 + (n - 1) * 30 + offset);

    private static LeakMonitorEngine Wire(SecsHarness h, LeakMonitorSettings settings)
    {
        var engine = new LeakMonitorEngine(settings);
        h.Bridge.OnSlotTable(engine.SlotTable);
        engine.SlotIdentityChanged += h.Bridge.OnSlotIdentityChanged;
        return engine;
    }

    /// <summary>
    /// S1F3 for <paramref name="svids"/>, returning the S1F4 values in order, as text (an ASCII
    /// value without its quotes). <c>GemHost.RequestStatusAsync</c> does not hand the reply back,
    /// so it is read from the SML the host logs for it — the same text a person reads in a trace.
    /// </summary>
    private static async Task<IReadOnlyList<string>> StatusAsync(SecsHarness h, params uint[] svids)
    {
        var log = new System.Text.StringBuilder();
        void Capture(string line) { lock (log) log.Append(line).Append('\n'); }
        h.Host.Log += Capture;
        try
        {
            await h.Host.RequestStatusAsync(svids);
            Assert.True(await SecsTestPort.WaitAsync(() => { lock (log) return log.ToString().Contains("[GEM] S1F3 -> S1F4"); }, 10),
                "no S1F4 in the host log");
        }
        finally
        {
            h.Host.Log -= Capture;
        }

        string text;
        lock (log) text = log.ToString();
        var reply = text[text.IndexOf("'S1F4'", StringComparison.Ordinal)..text.IndexOf("[GEM] S1F3 -> S1F4", StringComparison.Ordinal)];
        return Regex.Matches(reply, @"<(\w+) \[\d+\] (.*)>\s*$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value == "A" ? m.Groups[2].Value.Trim('"') : m.Groups[2].Value)
            .ToArray();
    }

    [Fact]
    public async Task S1F3_reads_slot_identity_with_no_frame_at_all()
    {
        await using var h = await SecsHarness.StartAsync();
        Wire(h, LeakMonitorSettings.CreateDefault());

        var v = await StatusAsync(h,
            Svid(1, 25), Svid(1, 18), Svid(1, 20), Svid(1, 23),
            Svid(2, 25), Svid(2, 22),
            Svid(3, 19), Svid(3, 24),
            Svid(1, 5));

        Assert.Equal("R_N2Ar", v[0]);
        Assert.Equal("1", v[1]);
        Assert.Equal("337.1", v[2]);
        Assert.Equal("N2", v[3]);
        Assert.Equal("R_NOAr", v[4]);
        Assert.Equal("NO 237 / Ar 750", v[5]);
        Assert.Equal("0", v[6]);          // PeakHeight
        Assert.Equal("Ar", v[7]);
        Assert.Equal("3", v[8]);          // enabled, nothing measured yet
    }

    [Fact]
    public async Task S1F3_reads_a_slot_live_values_from_the_last_frame()
    {
        await using var h = await SecsHarness.StartAsync();
        Wire(h, LeakMonitorSettings.CreateDefault());
        h.Bridge.OnSample(new LeakMonitorSnapshot
        {
            Timestamp = new DateTime(2026, 10, 23, 10, 0, 0, DateTimeKind.Local),
            Ratios = new[]
            {
                Row("R_NOAr", RatioState.Normal, raw: 0.01, pct: 101),
                Row("R_N2Ar", RatioState.Alarm, raw: 0.0625, pct: 158),
            },
        });

        var v = await StatusAsync(h, Svid(1, 1), Svid(1, 3), Svid(1, 5), Svid(2, 5), Svid(3, 5));

        Assert.Equal("0.0625", v[0]);
        Assert.Equal("158", v[1]);
        Assert.Equal("2", v[2]);          // Alarm
        Assert.Equal("0", v[3]);          // Normal
        Assert.Equal("3", v[4]);          // not in that frame
    }

    [Fact]
    public async Task Reordering_the_ratios_sends_CEID_507_and_the_new_identity()
    {
        await using var h = await SecsHarness.StartAsync();
        var settings = LeakMonitorSettings.CreateDefault();
        var engine = Wire(h, settings);

        var first = settings.Ratios[0];
        settings.Ratios.RemoveAt(0);
        settings.Ratios.Add(first);
        engine.ReloadRatios();

        Assert.True(await SecsTestPort.WaitAsync(() => h.Count >= 1, 10), "no S6F11 arrived");
        Assert.Equal(new[] { CeidRatioConfigReloaded }, h.Ceids());
        var v = await StatusAsync(h, Svid(1, 25), Svid(4, 25));
        Assert.Equal("R_NOAr", v[0]);
        Assert.Equal("R_N2Ar", v[1]);
    }

    [Fact]
    public async Task Retuning_thresholds_sends_nothing()
    {
        await using var h = await SecsHarness.StartAsync();
        var settings = LeakMonitorSettings.CreateDefault();
        var engine = Wire(h, settings);

        foreach (var r in settings.Ratios) { r.SigmaWarn = 4; r.EmaTauSeconds = 9; }
        engine.ReloadRatios();
        // Something that is sent, behind it on the same queue: once it has arrived, a 507
        // would have too.
        h.Bridge.OnAcquisitionChanged(true);

        Assert.True(await SecsTestPort.WaitAsync(() => h.Count >= 1, 10), "no S6F11 arrived");
        Assert.Equal(new[] { 10227508u }, h.Ceids());
    }

    [Fact]
    public async Task A_host_trace_can_follow_slot_values()
    {
        await using var h = await SecsHarness.StartAsync();
        Wire(h, LeakMonitorSettings.CreateDefault());
        h.Bridge.OnSample(new LeakMonitorSnapshot
        {
            Timestamp = new DateTime(2026, 10, 23, 10, 0, 0, DateTimeKind.Local),
            Ratios = new[] { Row("R_N2Ar", RatioState.Warning, raw: 0.0625, pct: 131) },
        });

        Assert.True(
            await h.Host.StartTraceAsync(
                trid: 9, TimeSpan.FromMilliseconds(200), totalSamples: 2, groupSize: 1,
                new[] { Svid(1, 3), Svid(1, 5), Svid(1, 25) }),
            "the equipment rejected S2F23 (TIAACK != 0)");

        Assert.True(await SecsTestPort.WaitAsync(() => h.Samples >= 1, 10), "no trace sample arrived");
        var first = h.TraceSamples()[0];
        Assert.Equal(131f, first.Values[0].FirstValue<float>());
        Assert.Equal(1u, first.Values[1].FirstValue<uint>());
        Assert.Equal("R_N2Ar", first.Values[2].GetString());
    }

    private static RatioSnapshot Row(string key, RatioState state, double raw, double pct) =>
        new(key, key, state,
            RawRatio: raw, SmoothedRatio: raw,
            HasBaseline: true, BaselineMean: 0.05, BaselineSigma: 0.001,
            PercentOfBaseline: pct, WarnThreshold: 0.0525, AlarmThreshold: 0.056,
            SlopePerMinute: 0, PlasmaPresent: true,
            NumeratorIntensity: 900, DenominatorIntensity: 18000,
            RatioNoiseSigma: 0.0008, NumeratorSnr: 40, DenominatorSnr: 120,
            Mode: MonitorMode.Ratio, Role: RatioRole.Alarm, HasPedestal: false);
}
