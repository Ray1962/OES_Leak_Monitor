using Secs4Net;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// S2F23 / S6F1 trace, and the promise the reporting switches make: <b>a status query always
/// answers</b>. Both were on the manual acceptance list (§13.4 items 2 and 4) purely because
/// nothing here had been written — neither needs a GUI or a spectrometer.
///
/// <para>Trace is the specification's own recommendation for a continuous trend (§6.2): sample
/// on a period rather than pushing an event report per frame. The equipment side comes from
/// <c>Aqusen.Secs</c>; what is being checked is that this app's bindings are reachable through
/// it and carry the values the snapshot holds.</para>
/// </summary>
[Collection("network")]
public class SecsTraceTests
{
    private const uint SvidCompositeLevel = 1022700007;
    private const uint SvidTestMode = 1022700016;

    /// <summary>DSPER is written as hhmmss[cc], so a period is a multiple of 10 ms.</summary>
    private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task A_host_trace_streams_S6F1_samples_of_the_bound_values()
    {
        await using var h = await SecsHarness.StartAsync();
        h.Bridge.OnSample(SnapshotBuilder.Leaking());   // composite Alarm, test mode

        Assert.True(
            await h.Host.StartTraceAsync(
                trid: 7, Period, totalSamples: 3, groupSize: 1,
                new[] { SvidCompositeLevel, SvidTestMode }),
            "the equipment rejected S2F23 (TIAACK != 0)");

        Assert.True(await SecsTestPort.WaitAsync(() => h.Samples >= 2, 10),
            $"expected trace samples, got {h.Samples}");

        var samples = h.TraceSamples();
        Assert.All(samples, s => Assert.Equal(7u, s.Trid));
        // SMPLN counts from 1 and does not repeat, which is how a host detects a gap.
        Assert.Equal(samples.Select((_, i) => (uint)(i + 1)), samples.Select(s => s.SampleNumber));

        var first = samples[0];
        Assert.Equal(2, first.Values.Count);                              // in the order asked for
        Assert.Equal(3u, first.Values[0].FirstValue<uint>());             // Alarm, per §5.1 (c)-1
        Assert.Equal(1u, first.Values[1].FirstValue<uint>());             // test mode
    }

    /// <summary>
    /// Reporting switched off stops the equipment volunteering anything; it does not make it
    /// lie, or go quiet, when the host asks. This is the half of §13.4 item 2 that does not
    /// need the Replay tab — including VID 016, which reports test mode whatever the switches
    /// say, so a host can tell for itself that a reading is not a measurement.
    /// </summary>
    [Fact]
    public async Task Reporting_switched_off_still_answers_a_trace_with_the_truth()
    {
        await using var h = await SecsHarness.StartAsync(s =>
        {
            s.ReportAlarms = false;
            s.ReportEvents = false;
            s.ReportInTestMode = false;
        });
        h.Bridge.OnSample(SnapshotBuilder.Leaking());

        // Nothing should be volunteered...
        h.Bridge.OnLeakLevelChanged(LeakAlarmLevel.Alarm);
        h.Bridge.OnAcquisitionChanged(true);

        // ...but the same state is there for the asking.
        Assert.True(
            await h.Host.StartTraceAsync(
                trid: 8, Period, totalSamples: 2, groupSize: 1,
                new[] { SvidCompositeLevel, SvidTestMode }),
            "the equipment rejected S2F23 (TIAACK != 0)");

        Assert.True(await SecsTestPort.WaitAsync(() => h.Samples >= 1, 10),
            "no trace sample arrived, so this test cannot tell silence from a dead link");

        var first = h.TraceSamples()[0];
        Assert.Equal(3u, first.Values[0].FirstValue<uint>());   // composite is still Alarm
        Assert.Equal(1u, first.Values[1].FirstValue<uint>());   // and still says it is test data

        Assert.Empty(h.Alids());
        Assert.Empty(h.Ceids());
    }
}
