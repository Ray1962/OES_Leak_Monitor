using System.IO;
using Aqusen.Secs;
using OES_Leak_Monitor;
using Secs4Net;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// The S6F11 event path against a real host over loopback — the half of the interface that
/// <see cref="SecsWireTests"/> does not touch.
///
/// <para>Until these existed the three CEIDs had been checked only as arithmetic
/// (<see cref="SecsChamberCodingTests"/>): no event report had ever left the equipment, and
/// neither the reporting switches nor the ordering guarantee had been exercised at all.
/// All three events are raised from the GUI, so the acceptance list called them "pending
/// manual verification" — which reads like a formality, and was not one.</para>
///
/// <para>What is still only human-verifiable after this: that pressing Start, Stop and
/// Acknowledge in the app reaches these three methods. The wire behaviour behind them is
/// covered here.</para>
/// </summary>
[Collection("network")]
public class SecsEventTests
{
    private const int Chamber = 2;

    private const uint CeidAcknowledged = 10227502;
    private const uint CeidAcquisitionStarted = 10227508;
    private const uint CeidAcquisitionStopped = 10227509;

    /// <summary>
    /// The two acquisition events, in the order they happened. Note the harness reports
    /// test-mode frames (<c>ReportInTestMode</c>), which is also the "allowed" half of the
    /// test-mode gate asserted below — a test only ever has synthetic frames to offer.
    /// </summary>
    [Fact]
    public async Task Starting_and_stopping_acquisition_send_CEID_508_then_509()
    {
        await using var h = await SecsHarness.StartAsync();
        h.Bridge.OnSample(SnapshotBuilder.Quiet());

        h.Bridge.OnAcquisitionChanged(true);
        h.Bridge.OnAcquisitionChanged(false);

        Assert.True(await SecsTestPort.WaitAsync(() => h.Count >= 2, 10),
            $"expected two event reports, got {h.Count}");
        Assert.Equal(new[] { CeidAcquisitionStarted, CeidAcquisitionStopped }, h.Ceids());
    }

    /// <summary>
    /// CEID 502 fires on exactly the condition that writes the <c>LeakMonitorAcknowledged</c>
    /// audit entry, so the event means what that entry means: a human ended a confirmed leak
    /// alarm. The engine does not raise it when nothing was latched.
    /// </summary>
    [Fact]
    public async Task An_acknowledged_alarm_sends_CEID_502()
    {
        await using var h = await SecsHarness.StartAsync();
        h.Bridge.OnSample(SnapshotBuilder.Leaking());

        h.Bridge.OnAcknowledged(new LeakAcknowledgedEventArgs
        {
            User = "lin",
            ClearedRatios = new[] { "N2 337.1 / Ar 750.4" },
            OldLevel = LeakAlarmLevel.Alarm,
            NewLevel = LeakAlarmLevel.Normal,
            Timestamp = new DateTime(2026, 8, 14, 22, 15, 0, DateTimeKind.Local),
        });

        Assert.True(await SecsTestPort.WaitAsync(() => h.Count >= 1, 10), "no S6F11 arrived");
        Assert.Equal(new[] { CeidAcknowledged }, h.Ceids());
    }

    /// <summary>
    /// Sends are chained so that two messages meaning opposite things cannot cross. The same
    /// queue carries alarms, and a host that receives a set after its clear is left holding an
    /// alarm that ended — so the ordering is asserted over a burst, not a pair.
    /// </summary>
    [Fact]
    public async Task Events_arrive_in_the_order_they_happened()
    {
        await using var h = await SecsHarness.StartAsync();
        h.Bridge.OnSample(SnapshotBuilder.Quiet());

        for (var i = 0; i < 3; i++)
        {
            h.Bridge.OnAcquisitionChanged(true);
            h.Bridge.OnAcquisitionChanged(false);
        }

        Assert.True(await SecsTestPort.WaitAsync(() => h.Count >= 6, 15),
            $"expected six event reports, got {h.Count}");
        Assert.Equal(
            new[]
            {
                CeidAcquisitionStarted, CeidAcquisitionStopped,
                CeidAcquisitionStarted, CeidAcquisitionStopped,
                CeidAcquisitionStarted, CeidAcquisitionStopped,
            },
            h.Ceids());
    }

    /// <summary>
    /// Switching event reporting off stops the equipment volunteering events — and nothing
    /// else. The alarm assertion is the point of the test: "no events" must mean the switch
    /// worked, not that the connection was dead.
    /// </summary>
    [Fact]
    public async Task Event_reporting_off_silences_events_but_not_alarms()
    {
        await using var h = await SecsHarness.StartAsync(s => s.ReportEvents = false);
        h.Bridge.OnSample(SnapshotBuilder.Leaking());

        h.Bridge.OnAcquisitionChanged(true);
        h.Bridge.OnLeakLevelChanged(LeakAlarmLevel.Alarm);

        Assert.True(await SecsTestPort.WaitAsync(() => h.Alarms >= 1, 10),
            "the alarm did not arrive, so this test cannot tell a working switch from a dead link");
        await Task.Delay(300);   // let an event that should not exist arrive
        Assert.Empty(h.Ceids());
    }

    /// <summary>
    /// The default: synthetic and replayed frames are not measurements, and an event arriving
    /// at the host carries no marking that says so. VID 016 reports the truth either way, which
    /// is why suppressing the event hides nothing from a host that asks.
    /// </summary>
    [Fact]
    public async Task Test_mode_frames_send_no_events_by_default()
    {
        await using var h = await SecsHarness.StartAsync(s => s.ReportInTestMode = false);
        h.Bridge.OnSample(SnapshotBuilder.Quiet());   // TestMode = true

        h.Bridge.OnAcquisitionChanged(true);
        h.Bridge.OnAcquisitionChanged(false);

        await Task.Delay(500);
        Assert.Empty(h.Ceids());
    }

    /// <summary>
    /// The other half of the same gate, and the asymmetry in it: a leak alarm derived from
    /// synthetic frames is withheld, but a spectrometer off the bus is a fact about the tool
    /// whatever the frames contain, so the fault goes out regardless. The fault doubles as the
    /// proof that the link was alive while the leak alarm was being suppressed.
    /// </summary>
    [Fact]
    public async Task Test_mode_suppresses_leak_alarms_but_never_equipment_faults()
    {
        await using var h = await SecsHarness.StartAsync(s => s.ReportInTestMode = false);
        h.Bridge.OnSample(SnapshotBuilder.Leaking());   // TestMode = true

        h.Bridge.OnLeakLevelChanged(LeakAlarmLevel.Alarm);
        h.Bridge.ReportFault(SecsFault.ConnectionLost, set: true, detail: "cable pulled");

        Assert.True(await SecsTestPort.WaitAsync(() => h.Alarms >= 1, 10), "no S5F1 arrived at all");
        await Task.Delay(300);   // let the suppressed leak alarm arrive, if it is going to
        Assert.Equal(new[] { "10227012" }, h.Alids());
    }

    /// <summary>
    /// A bridge with the interface switched off holds no port and must not throw when the app
    /// reports something to it — the app calls these methods whatever the SECS settings say.
    /// </summary>
    [Fact]
    public void A_disabled_interface_swallows_events_without_complaining()
    {
        var folder = Path.Combine(Path.GetTempPath(), "oes-secs-off-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var bridge = new SecsBridge(null, folder, folder,
                () => new SecsBridge.AcquisitionInfo(120f, 4u));
            bridge.Configure(new SecsSettings { Enabled = false, ChamberCode = Chamber });

            bridge.OnSample(SnapshotBuilder.Quiet());
            bridge.OnAcquisitionChanged(true);
            bridge.OnAcknowledged(new LeakAcknowledgedEventArgs { User = "lin" });

            Assert.Equal(SecsRunState.Disabled, bridge.State);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* best effort */ }
        }
    }
}
