using Secs4Net;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// What a host gets back in the cases nobody had written down: an SVID the profile does not
/// have, the trace limits, a trace and the report registry across a dropped link, and what the
/// report-setup acknowledges actually check. All of it is <c>Aqusen.Secs</c> behaviour rather
/// than this app's, which is why it was left as "not verified" — and also why it is pinned
/// here: a host is written against these answers, and a library upgrade that changes one
/// should fail a test before it reaches a fab.
///
/// <para>Where a test asserts something a host would find surprising (an unknown VID accepted
/// with DRACK 0, a disabled CEID still sent), the surprise is the point: the assertion records
/// what the equipment does, not what the specification suggests it should.</para>
/// </summary>
[Collection("network")]
public class SecsHostContractTests
{
    private const uint SvidCompositeLevel = 1022700007;
    private const uint SvidTestMode = 1022700016;
    private const uint SvidNotInProfile = 1022700999;

    private const uint CeidAcquisitionStarted = 10227508;
    private const uint CeidAcquisitionStopped = 10227509;

    private const uint ReportId = 27002;

    /// <summary>Slot VIDs 101–125, 131–155, 161–185 (offsets 01–25 of slots 1–3): 75 SVIDs
    /// the shipped profile is guaranteed to have, enough to cross the per-trace limit.</summary>
    private static uint[] KnownSvids(int count) =>
        Enumerable.Range(0, 3)
            .SelectMany(slot => Enumerable.Range(1, 25).Select(offset => 100 + slot * 30 + offset))
            .Take(count)
            .Select(vid => 1022700000u + (uint)vid)
            .ToArray();

    private static SecsMessage S1F3(params uint[] svids) =>
        new(1, 3, true) { SecsItem = Item.L(svids.Select(s => Item.U4(s)).ToArray()) };

    /// <summary>S2F23 with a one-minute DSPER, so an accepted trace sends nothing during the test.</summary>
    private static SecsMessage S2F23(uint trid, params uint[] svids) =>
        new(2, 23, true)
        {
            SecsItem = Item.L(
                Item.U4(trid),
                Item.A("000100"),
                Item.U4(0u),
                Item.U4(1u),
                Item.L(svids.Select(s => Item.U4(s)).ToArray())),
        };

    private static byte Ack(SecsMessage reply) => reply.SecsItem!.FirstValue<byte>();

    /// <summary>The RPTIDs and value counts of one S6F11 body, for asserting a report's shape.</summary>
    private static (uint Rptid, int Values)[] Reports(Item body) =>
        Enumerable.Range(0, body[2].Count)
            .Select(i => (body[2][i][0].FirstValue<uint>(), body[2][i][1].Count))
            .ToArray();

    private static async Task SetUpReportAsync(SecsHarness h, params uint[] ceids)
    {
        Assert.True(await h.Host.DefineReportAsync(ReportId, new[] { SvidCompositeLevel, SvidTestMode }));
        foreach (var ceid in ceids)
        {
            Assert.True(await h.Host.LinkEventReportAsync(ceid, new[] { ReportId }));
        }
        Assert.True(await h.Host.EnableEventReportsAsync(true, ceids));
    }

    // ---- S1F3 ------------------------------------------------------------------------------

    /// <summary>
    /// An SVID the profile does not list is answered with an empty list in its place, not
    /// refused and not dropped: S1F4 keeps one item per requested SVID, so a host can still
    /// read every other value by position.
    /// </summary>
    [Fact]
    public async Task S1F3_answers_an_unknown_SVID_with_an_empty_list_in_its_place()
    {
        await using var h = await SecsHarness.StartAsync();
        h.Bridge.OnSample(SnapshotBuilder.Leaking());

        var reply = await h.SendRawAsync(S1F3(SvidNotInProfile, SvidCompositeLevel));

        var values = reply.SecsItem!;
        Assert.Equal(2, values.Count);
        Assert.Equal(SecsFormat.List, values[0].Format);
        Assert.Equal(0, values[0].Count);
        Assert.Equal(3u, values[1].FirstValue<uint>());   // Alarm, still in its own position
    }

    // ---- S2F23 limits ----------------------------------------------------------------------

    /// <summary>64 SVIDs per trace (<c>MaxTraceSvids</c>); the 65th is TIAACK 1.</summary>
    [Fact]
    public async Task A_trace_takes_64_SVIDs_and_refuses_65_with_TIAACK_1()
    {
        await using var h = await SecsHarness.StartAsync();

        Assert.Equal(1, Ack(await h.SendRawAsync(S2F23(1, KnownSvids(65)))));
        Assert.Equal(0, Ack(await h.SendRawAsync(S2F23(1, KnownSvids(64)))));
    }

    /// <summary>Four concurrent traces (<c>MaxTraces</c>); a fifth TRID is TIAACK 2, while
    /// re-sending one of the four replaces it rather than counting as a fifth.</summary>
    [Fact]
    public async Task Four_traces_run_at_once_and_a_fifth_is_refused_with_TIAACK_2()
    {
        await using var h = await SecsHarness.StartAsync();

        for (uint trid = 1; trid <= 4; trid++)
        {
            Assert.Equal(0, Ack(await h.SendRawAsync(S2F23(trid, SvidCompositeLevel))));
        }
        Assert.Equal(2, Ack(await h.SendRawAsync(S2F23(5, SvidCompositeLevel))));
        Assert.Equal(0, Ack(await h.SendRawAsync(S2F23(4, SvidTestMode))));
    }

    // ---- across a dropped link -------------------------------------------------------------

    /// <summary>
    /// A host that drops and reconnects does not have to re-send S2F23: the trace kept running
    /// on the equipment, its samples while the link was down were lost (not spooled — nobody
    /// asked for S6 to be spooled), and SMPLN carries on, so the gap is visible in the numbers.
    /// Restart interface, by contrast, builds a new equipment instance and every trace ends.
    /// </summary>
    [Fact]
    public async Task A_trace_survives_a_host_reconnect_but_not_a_restart()
    {
        await using var h = await SecsHarness.StartAsync();
        h.Bridge.OnSample(SnapshotBuilder.Quiet());

        Assert.True(await h.Host.StartTraceAsync(
            trid: 9, TimeSpan.FromMilliseconds(200), totalSamples: 0, groupSize: 1,
            new[] { SvidCompositeLevel }));
        Assert.True(await SecsTestPort.WaitAsync(() => h.Samples >= 1, 10), "the trace never started");

        await h.ReconnectHostAsync();
        var before = h.Samples;
        var lastBefore = h.TraceSamples()[before - 1].SampleNumber;

        Assert.True(await SecsTestPort.WaitAsync(() => h.Samples >= before + 2, 10),
            "no trace sample reached the reconnected host without a new S2F23");
        var after = h.TraceSamples().Skip(before).ToArray();
        Assert.All(after, s => Assert.Equal(9u, s.Trid));
        Assert.True(after[0].SampleNumber > lastBefore,
            $"SMPLN restarted ({after[0].SampleNumber} after {lastBefore}) — the trace was re-created, not continued");

        await h.RestartAsync();
        await Task.Delay(1000);                      // five periods
        var settled = h.Samples;
        await Task.Delay(1000);
        Assert.Equal(settled, h.Samples);
    }

    /// <summary>
    /// The report registry (S2F33/35/37) belongs to the equipment instance, not the
    /// connection: it survives a host reconnect, and is gone after Restart interface — at which
    /// point the CEID still goes out, with no reports in it.
    /// </summary>
    [Fact]
    public async Task Report_setup_survives_a_host_reconnect_but_not_a_restart()
    {
        await using var h = await SecsHarness.StartAsync();
        h.Bridge.OnSample(SnapshotBuilder.Quiet());
        await SetUpReportAsync(h, CeidAcquisitionStarted);

        await h.ReconnectHostAsync();
        h.Bridge.OnAcquisitionChanged(true);
        Assert.True(await SecsTestPort.WaitAsync(() => h.Count >= 1, 10), "no S6F11 after the reconnect");
        Assert.Equal(new[] { (ReportId, 2) }, Reports(h.EventBodies()[0]));

        await h.RestartAsync();
        h.Bridge.OnAcquisitionChanged(true);
        Assert.True(await SecsTestPort.WaitAsync(() => h.Count >= 2, 10), "no S6F11 after the restart");
        Assert.Equal(CeidAcquisitionStarted, h.Ceids()[1]);
        Assert.Empty(Reports(h.EventBodies()[1]));
    }

    // ---- what the report-setup acks check --------------------------------------------------

    /// <summary>
    /// DRACK and LRACK check the message's shape and nothing else. An undefined VID is
    /// accepted with DRACK 0 and later reported as an empty list; an RPTID that was never
    /// defined is linked with LRACK 0 and later reported with no values. A host that relies on
    /// the acknowledge to catch a typo will not be told.
    /// </summary>
    [Fact]
    public async Task Undefined_VIDs_and_RPTIDs_are_accepted_and_reported_empty()
    {
        await using var h = await SecsHarness.StartAsync();
        h.Bridge.OnSample(SnapshotBuilder.Leaking());

        Assert.True(await h.Host.DefineReportAsync(ReportId, new[] { SvidNotInProfile, SvidCompositeLevel }));
        Assert.True(await h.Host.LinkEventReportAsync(CeidAcquisitionStarted, new[] { ReportId, 27999u }));
        Assert.True(await h.Host.EnableEventReportsAsync(true, new[] { CeidAcquisitionStarted }));

        h.Bridge.OnAcquisitionChanged(true);
        Assert.True(await SecsTestPort.WaitAsync(() => h.Count >= 1, 10), "no S6F11 arrived");

        var body = h.EventBodies()[0];
        Assert.Equal(new[] { (ReportId, 2), (27999u, 0) }, Reports(body));
        var values = body[2][0][1];
        Assert.Equal(SecsFormat.List, values[0].Format);   // the undefined VID
        Assert.Equal(0, values[0].Count);
        Assert.Equal(3u, values[1].FirstValue<uint>());    // composite Alarm
    }

    /// <summary>A malformed report entry — not an <c>L{2} RPTID, L{VID}</c> — is the one
    /// thing DRACK refuses, with 3. The same rule gives LRACK 3.</summary>
    [Fact]
    public async Task A_malformed_report_or_link_entry_is_refused_with_ack_3()
    {
        await using var h = await SecsHarness.StartAsync();

        var badDefine = new SecsMessage(2, 33, true)
        {
            SecsItem = Item.L(Item.U4(0u), Item.L(Item.U4(ReportId))),
        };
        var badLink = new SecsMessage(2, 35, true)
        {
            SecsItem = Item.L(Item.U4(0u), Item.L(Item.U4(CeidAcquisitionStarted))),
        };

        Assert.Equal(3, Ack(await h.SendRawAsync(badDefine)));
        Assert.Equal(3, Ack(await h.SendRawAsync(badLink)));
    }

    /// <summary>ERACK is always 0 — even for a CEID this equipment never sends (501).</summary>
    [Fact]
    public async Task S2F37_acknowledges_any_CEID_with_ERACK_0()
    {
        await using var h = await SecsHarness.StartAsync();

        var enable = new SecsMessage(2, 37, true)
        {
            SecsItem = Item.L(Item.Boolean(true), Item.L(Item.U4(10227501u))),
        };

        Assert.Equal(0, Ack(await h.SendRawAsync(enable)));
    }

    /// <summary>
    /// Disabling a CEID with S2F37 does not stop it being sent: the S6F11 still goes out, with
    /// an empty report list. A host that disables an event to silence it has to ignore it.
    /// </summary>
    [Fact]
    public async Task A_CEID_disabled_with_S2F37_is_still_sent_with_no_reports()
    {
        await using var h = await SecsHarness.StartAsync();
        h.Bridge.OnSample(SnapshotBuilder.Quiet());
        await SetUpReportAsync(h, CeidAcquisitionStarted, CeidAcquisitionStopped);
        Assert.True(await h.Host.EnableEventReportsAsync(false, new[] { CeidAcquisitionStarted }));

        h.Bridge.OnAcquisitionChanged(true);
        h.Bridge.OnAcquisitionChanged(false);
        Assert.True(await SecsTestPort.WaitAsync(() => h.Count >= 2, 10), $"expected two S6F11, got {h.Count}");

        Assert.Equal(new[] { CeidAcquisitionStarted, CeidAcquisitionStopped }, h.Ceids());
        Assert.Empty(Reports(h.EventBodies()[0]));
        Assert.Equal(new[] { (ReportId, 2) }, Reports(h.EventBodies()[1]));
    }
}
