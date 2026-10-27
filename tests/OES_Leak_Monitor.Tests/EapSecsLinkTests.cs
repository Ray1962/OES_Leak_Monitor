using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Aqusen.Secs;
using OES_Leak_Monitor;
using Secs4Net;
using Xunit;
using static Secs4Net.Item;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// The OES end of the EAP link under scheme 2b (#58), against an <see cref="EquipmentSimulator"/>
/// playing the EAP over loopback: the OES connects out as host, goes online, enables the two
/// CEIDs, reads S1–S7, and hands what it reads and receives to the engine's two entry points.
///
/// <para>What this guards beyond the message order: a resend after a lost reply is applied
/// once, while the event an S7 names after an OES restart is still applied; a dropped
/// connection alone leaves the window alone, while a silence withdraws it — once; and data that
/// does not match the fixed report layout is refused with a reason rather than read wrong.</para>
/// </summary>
[Collection("eap-secs")]
public sealed class EapSecsLinkTests : IDisposable
{
    private const uint ProcessStart = 3100439529, ProcessEnd = 3100439530;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oeslm-eap2b-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<(uint Ceid, DateTime Time)> _events = new();
    private readonly ConcurrentQueue<ToolContextReport> _reports = new();
    private ToolEventAck _reportAck = ToolEventAck.Accepted;

    public EapSecsLinkTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ---------------------------------------------------------------- the fake EAP

    /// <summary>
    /// The EAP as the spec describes it: S1–S7 at 6001–6007, every tool event forwarded under
    /// CEID 6100 with the fixed report { S5, S6, S7 }, the tool link under 6101 with { S4 }.
    /// Each helper updates S1–S7 first and sends second, the order the platform is asked to keep.
    /// </summary>
    private sealed class FakeEap : IDeviceProfile
    {
        /// <param name="servesS4">False: a platform with no S4 at all (spec Q5), so an S1F3 that
        /// asks for 6004 gets an empty item back. The tool-link report then cannot exist either.</param>
        public FakeEap(bool servesS4 = true)
        {
            _servesS4 = servesS4;
            PredefinedReports = servesS4
                ? new Dictionary<uint, uint[]> { [6200] = new uint[] { 6005, 6006, 6007 }, [6201] = new uint[] { 6004 } }
                : new Dictionary<uint, uint[]> { [6200] = new uint[] { 6005, 6006, 6007 } };
            PredefinedEventLinks = servesS4
                ? new Dictionary<uint, uint[]> { [6100] = new uint[] { 6200 }, [6101] = new uint[] { 6201 } }
                : new Dictionary<uint, uint[]> { [6100] = new uint[] { 6200 } };
        }

        private readonly bool _servesS4;
        public byte State = 2, ToolLink = 1;
        public uint WindowCeid, LastCeid, Seq;
        public string WindowStart = "", LastTime = "";
        public bool TimeAsU4;   // a malformed S6

        public string Name => "fake EAP";

        public IEnumerable<SvDefinition> CreateStatusVariables(IEquipmentContext equipment)
        {
            yield return new SvDefinition(6001, "S1", "", () => U1(State));
            yield return new SvDefinition(6002, "S2", "", () => U4(WindowCeid));
            yield return new SvDefinition(6003, "S3", "", () => A(WindowStart));
            if (_servesS4) yield return new SvDefinition(6004, "S4", "", () => U1(ToolLink));
            yield return new SvDefinition(6005, "S5", "", () => U4(LastCeid));
            yield return new SvDefinition(6006, "S6", "", () => TimeAsU4 ? U4(0) : A(LastTime));
            yield return new SvDefinition(6007, "S7", "", () => U4(Seq));
        }

        public IReadOnlyDictionary<string, string[]> RemoteCommands { get; } = new Dictionary<string, string[]>();
        public IReadOnlyList<HostAction> HostActions { get; } = Array.Empty<HostAction>();
        public IReadOnlyList<EquipmentAction> EquipmentActions { get; } = Array.Empty<EquipmentAction>();

        public IReadOnlyDictionary<uint, uint[]> PredefinedReports { get; }

        public IReadOnlyDictionary<uint, uint[]> PredefinedEventLinks { get; }

        /// <summary>A tool event: SEQ + 1, S5/S6, the window per the STATE rules, then S6F11.</summary>
        public Task Forward(EquipmentSimulator eq, uint toolCeid, string time = "2026102714014000")
        {
            Seq++;
            LastCeid = toolCeid;
            LastTime = time;
            if (toolCeid == ProcessStart) { State = 1; WindowCeid = toolCeid; WindowStart = time; }
            else if (toolCeid == ProcessEnd) { State = 0; WindowCeid = 0; WindowStart = ""; }
            return eq.SendEventAsync(6100);
        }

        public Task Resend(EquipmentSimulator eq) => eq.SendEventAsync(6100);

        public Task SetToolLink(EquipmentSimulator eq, bool up)
        {
            ToolLink = up ? (byte)1 : (byte)0;
            if (!up) { State = 2; WindowCeid = 0; WindowStart = ""; }
            return eq.SendEventAsync(6101);
        }
    }

    private static EapLinkSettings To(int port) => new()
    {
        Enabled = true, RemoteHost = "127.0.0.1", RemotePort = port,
        WindowStateSvid = 6001, WindowCeidSvid = 6002, WindowStartSvid = 6003, ToolLinkSvid = 6004,
        LastCeidSvid = 6005, LastTimeSvid = 6006, SequenceSvid = 6007, ForwardCeid = 6100, LinkCeid = 6101,
    };

    private EapSecsLink Link(SystemLogger? log = null, TimeSpan? silence = null, TimeSpan? poll = null) =>
        new(log, _dir,
            (ceid, t) => { _events.Enqueue((ceid, t)); return ToolEventAck.Accepted; },
            r => { _reports.Enqueue(r); return _reportAck; })
        {
            PollInterval = poll ?? TimeSpan.FromSeconds(30),
            SilenceTimeoutOverride = silence ?? TimeSpan.FromSeconds(60),
            T3Milliseconds = 3000,
            T5Milliseconds = 500,
        };

    /// <summary>The fake EAP listening on <paramref name="port"/>, with its log captured.</summary>
    private sealed class Eap : IAsyncDisposable
    {
        private readonly List<string> _log = new();
        public EquipmentSimulator Equipment { get; }

        public Eap(FakeEap profile, int port)
        {
            Equipment = new EquipmentSimulator(
                new SecsGemOptions { IsActive = false, IpAddress = "127.0.0.1", Port = port, T5 = 500 },
                new GemOptions { ModelName = "FAKE-EAP", SoftwareRevision = "1.0", EstablishRetryIntervalMs = 500 },
                profile);
            Equipment.Log += l => { lock (_log) _log.Add(l); };
            Equipment.Start();
        }

        public bool Saw(string fragment) { lock (_log) return _log.Any(l => l.Contains(fragment)); }
        public int Count(string fragment) { lock (_log) return _log.Count(l => l.Contains(fragment)); }

        public ValueTask DisposeAsync() => Equipment.DisposeAsync();
    }

    private static Task<bool> Wait(Func<bool> condition, int seconds = 15) => SecsTestPort.WaitAsync(condition, seconds);

    // ---------------------------------------------------------------- connecting

    /// <summary>
    /// On connect the OES goes online, enables exactly the two EAP CEIDs — no S2F33/S2F35, the
    /// reports are the platform's — reads S1–S7, and never sets the EAP's clock.
    /// </summary>
    [Fact]
    public async Task OnConnect_ItGoesOnline_EnablesTheTwoCeids_AndReadsS1ToS7()
    {
        int port = SecsTestPort.Free();
        var profile = new FakeEap();
        await using var eap = new Eap(profile, port);
        using var link = Link();
        link.Configure(To(port), 30);

        Assert.True(await Wait(() => link.Status.State == EapLinkState.Connected && link.Status.LastReading is not null));
        Assert.True(eap.Saw("S2F37 enable -> S2F38 ERACK=0 (2 enabled)"));
        Assert.True(eap.Saw("S1F17"));
        Assert.True(eap.Saw("S1F3 -> S1F4 (7 SV)"));
        Assert.False(eap.Saw("S2F33"));
        Assert.False(eap.Saw("S2F35"));
        Assert.False(eap.Saw("S2F31"));

        // S1 = 2 at the start: the window is reported unknown.
        Assert.True(await Wait(() => _reports.Any(r => r.State == ToolContextReportState.Unknown)));
        Assert.Equal(2, link.Status.LastReading!.WindowState);
    }

    [Fact]
    public async Task WithIdsMissing_ItDoesNotStart()
    {
        using var link = Link();
        var settings = To(SecsTestPort.Free());
        settings.SequenceSvid = 0;
        link.Configure(settings, 30);

        Assert.Equal(EapLinkState.IdsMissing, link.Status.State);
        Assert.Contains("not all set", link.Status.Detail);
    }

    // ---------------------------------------------------------------- events

    [Fact]
    public async Task AForwardedEvent_ReachesTheEngine_WithItsCeidAndTime()
    {
        int port = SecsTestPort.Free();
        var profile = new FakeEap();
        await using var eap = new Eap(profile, port);
        using var link = Link();
        link.Configure(To(port), 30);
        Assert.True(await Wait(() => link.Status.State == EapLinkState.Connected));

        await profile.Forward(eap.Equipment, ProcessStart);

        Assert.True(await Wait(() => _events.Count == 1));
        var ev = Assert.Single(_events);
        Assert.Equal(ProcessStart, ev.Ceid);
        Assert.Equal(new DateTime(2026, 10, 27, 14, 1, 40), ev.Time);
    }

    /// <summary>The EAP resends with the same SEQ (its reply was lost): applied once — a second
    /// Start would log a spurious "window had no End".</summary>
    [Fact]
    public async Task AResend_IsAppliedOnce()
    {
        int port = SecsTestPort.Free();
        var profile = new FakeEap();
        await using var eap = new Eap(profile, port);
        using var link = Link();
        link.Configure(To(port), 30);
        Assert.True(await Wait(() => link.Status.State == EapLinkState.Connected));

        await profile.Forward(eap.Equipment, ProcessStart);
        await profile.Resend(eap.Equipment);
        await profile.Forward(eap.Equipment, ProcessEnd);

        Assert.True(await Wait(() => _events.Count >= 2));
        await Task.Delay(300);
        Assert.Equal(new[] { ProcessStart, ProcessEnd }, _events.Select(e => e.Ceid));
    }

    /// <summary>
    /// After an OES restart the first S1F3 reads an S7 naming the event the EAP is about to
    /// resend. That event has not been handled here, so it must not be taken for a duplicate.
    /// </summary>
    [Fact]
    public async Task TheEventAnS7Names_IsStillApplied()
    {
        int port = SecsTestPort.Free();
        var profile = new FakeEap { Seq = 4, LastCeid = ProcessEnd, LastTime = "2026102714000000", State = 0 };
        await using var eap = new Eap(profile, port);
        using var link = Link();
        link.Configure(To(port), 30);
        Assert.True(await Wait(() => link.Status.LastReading?.Sequence == 4));

        await profile.Resend(eap.Equipment);   // SEQ 4 again

        Assert.True(await Wait(() => _events.Count == 1));
        Assert.Equal(ProcessEnd, _events.Single().Ceid);
    }

    [Fact]
    public async Task AGapAndARestart_AreEachLogged()
    {
        using var log = new LogCapture();
        int port = SecsTestPort.Free();
        var profile = new FakeEap();
        await using (var eap = new Eap(profile, port))
        using (var link = Link(log.Logger))
        {
            link.Configure(To(port), 30);
            Assert.True(await Wait(() => link.Status.State == EapLinkState.Connected && link.Status.LastReading is not null));

            await profile.Forward(eap.Equipment, ProcessStart);   // 1
            profile.Seq += 2;                                       // 2 and 3 lost
            await profile.Forward(eap.Equipment, ProcessEnd);     // 4
            profile.Seq = 0;                                        // the EAP restarted
            await profile.Forward(eap.Equipment, ProcessStart);   // 1
            Assert.True(await Wait(() => _events.Count == 3));
        }

        var lines = log.Lines();
        var gap = Assert.Single(lines, l => l.Contains("EapEventSequenceGap"));
        Assert.Contains("2 event(s)", gap);
        Assert.Single(lines, l => l.Contains("EapSequenceReset"));
    }

    /// <summary>A report that does not match the fixed layout is refused with a reason and never
    /// applied — reading a U4 where a time should be would date the window wrong.</summary>
    [Fact]
    public async Task AMalformedReport_IsRejected_AndNotApplied()
    {
        using var log = new LogCapture();
        int port = SecsTestPort.Free();
        var profile = new FakeEap();
        await using (var eap = new Eap(profile, port))
        using (var link = Link(log.Logger))
        {
            link.Configure(To(port), 30);
            Assert.True(await Wait(() => link.Status.State == EapLinkState.Connected));

            profile.TimeAsU4 = true;
            await profile.Forward(eap.Equipment, ProcessStart);
            await Task.Delay(500);
            Assert.Empty(_events);
        }

        var line = Assert.Single(log.Lines(), l => l.Contains("EapReportRejected"));
        Assert.Contains("U4/U4/U4", line);
    }

    [Fact]
    public async Task ToolLinkDown_ReportsUnknown()
    {
        int port = SecsTestPort.Free();
        var profile = new FakeEap { State = 0 };
        await using var eap = new Eap(profile, port);
        using var link = Link();
        link.Configure(To(port), 30);
        Assert.True(await Wait(() => link.Status.State == EapLinkState.Connected && link.Status.LastReading is not null));
        while (_reports.TryDequeue(out _)) { }

        await profile.SetToolLink(eap.Equipment, up: false);

        Assert.True(await Wait(() => _reports.Any(r => r.State == ToolContextReportState.Unknown)));
        Assert.Contains("no SECS link", _reports.First(r => r.State == ToolContextReportState.Unknown).Reason);
    }

    // ---------------------------------------------------------------- S4 / tool-link CEID optional (#64)

    private static EapLinkSettings WithoutToolLink(int port, bool keepLinkCeid = false)
    {
        var s = To(port);
        s.ToolLinkSvid = 0;
        if (!keepLinkCeid) s.LinkCeid = 0;
        return s;
    }

    /// <summary>
    /// The case #64 exists for: a platform with no S4 and no tool-link CEID. Configured with
    /// both left 0, the OES still connects, enables the forwarding CEID alone, reads six
    /// variables, takes the window from S1/S2 and the events as before — and logs, once, what it
    /// can no longer see.
    /// </summary>
    [Fact]
    public async Task WithNeitherProvided_TheLinkStillDeliversEventsAndTheWindow()
    {
        using var log = new LogCapture();
        int port = SecsTestPort.Free();
        var profile = new FakeEap(servesS4: false) { State = 1, WindowCeid = ProcessStart, WindowStart = "2026102714014000" };
        await using (var eap = new Eap(profile, port))
        using (var link = Link(log.Logger, poll: TimeSpan.FromMilliseconds(300)))
        {
            link.Configure(WithoutToolLink(port), 30);

            Assert.True(await Wait(() => link.Status.State == EapLinkState.Connected && link.Status.LastReading is not null));
            Assert.True(eap.Saw("S2F37 enable -> S2F38 ERACK=0 (1 enabled)"));
            Assert.True(eap.Saw("S1F3 -> S1F4 (6 SV)"));
            Assert.Null(link.Status.LastReading!.ToolLink);
            Assert.Contains("neither S4 nor the tool-link CEID", link.Status.ToolLinkGap);
            Assert.True(await Wait(() => _reports.Any(r => r.State == ToolContextReportState.InWindow && r.StartCeid == ProcessStart)));

            await profile.Forward(eap.Equipment, ProcessEnd);
            Assert.True(await Wait(() => _events.Any(e => e.Ceid == ProcessEnd)));

            // Without S4 the EAP's own S1 = 2 is what says it lost the tool.
            profile.State = 2;
            Assert.True(await Wait(() => _reports.Any(r => r.State == ToolContextReportState.Unknown)));
            // Several polls and a reconnect-free run: still one entry.
            await Task.Delay(700);
        }

        var lines = log.Lines();
        Assert.Single(lines, l => l.Contains("EapToolLinkNotMonitored"));
        Assert.DoesNotContain(lines, l => l.Contains("EapReportRejected"));
    }

    /// <summary>The same platform with S4 still configured: the S1F3 asks for a variable the EAP
    /// does not have, every reading is refused, and the window is never taken from it — the
    /// failure leaving S4 at 0 avoids.</summary>
    [Fact]
    public async Task WithS4ConfiguredButNotServed_EveryReadingIsRefused()
    {
        using var log = new LogCapture();
        int port = SecsTestPort.Free();
        await using (var eap = new Eap(new FakeEap(servesS4: false) { State = 1, WindowCeid = ProcessStart }, port))
        using (var link = Link(log.Logger))
        {
            link.Configure(To(port), 30);
            Assert.True(await Wait(() => eap.Saw("S1F3 -> S1F4 (7 SV)")));
            await Task.Delay(300);
            Assert.Null(link.Status.LastReading);
            Assert.DoesNotContain(_reports, r => r.State == ToolContextReportState.InWindow);
        }

        Assert.Contains(log.Lines(), l => l.Contains("EapReportRejected"));
    }

    /// <summary>No S4 but the tool-link CEID: the event alone still withdraws the window.</summary>
    [Fact]
    public async Task WithOnlyTheToolLinkCeid_ALinkDownEventStillReportsUnknown()
    {
        int port = SecsTestPort.Free();
        var profile = new FakeEap { State = 0 };
        await using var eap = new Eap(profile, port);
        using var link = Link();
        link.Configure(WithoutToolLink(port, keepLinkCeid: true), 30);
        Assert.True(await Wait(() => link.Status.State == EapLinkState.Connected && link.Status.LastReading is not null));
        Assert.True(eap.Saw("S1F3 -> S1F4 (6 SV)"));
        Assert.Contains("no S4", link.Status.ToolLinkGap);
        while (_reports.TryDequeue(out _)) { }

        await profile.SetToolLink(eap.Equipment, up: false);

        Assert.True(await Wait(() => _reports.Any(r => r.State == ToolContextReportState.Unknown && r.Reason.Contains("no SECS link"))));
    }

    // ---------------------------------------------------------------- polling

    /// <summary>Each S1F3 states the window: in one, with the Start CEID, or unknown when the EAP
    /// has lost the tool whatever S1 says.</summary>
    [Fact]
    public async Task ThePoll_StatesTheWindow()
    {
        int port = SecsTestPort.Free();
        var profile = new FakeEap { State = 1, WindowCeid = ProcessStart, WindowStart = "2026102714014000" };
        await using var eap = new Eap(profile, port);
        using var link = Link(poll: TimeSpan.FromMilliseconds(300));
        link.Configure(To(port), 30);

        Assert.True(await Wait(() => _reports.Any(r => r.State == ToolContextReportState.InWindow && r.StartCeid == ProcessStart)));

        profile.ToolLink = 0;
        Assert.True(await Wait(() => _reports.Any(r => r.State == ToolContextReportState.Unknown && r.Reason.Contains("no SECS link"))));
    }

    /// <summary>An S2 the CEID table calls an End is a contradiction: refused and logged.</summary>
    [Fact]
    public async Task APollTheTableContradicts_IsRejected()
    {
        using var log = new LogCapture();
        _reportAck = ToolEventAck.Invalid;
        int port = SecsTestPort.Free();
        await using (var eap = new Eap(new FakeEap { State = 1, WindowCeid = ProcessEnd }, port))
        using (var link = Link(log.Logger))
        {
            link.Configure(To(port), 30);
            Assert.True(await Wait(() => link.Status.LastReading is not null));
            await Task.Delay(300);
        }
        Assert.Contains(log.Lines(), l => l.Contains("EapReportRejected") && l.Contains("End event"));
    }

    // ---------------------------------------------------------------- disconnects and silence

    /// <summary>
    /// The EAP goes away and comes back inside the timeout: the OES reconnects and reads again,
    /// and nothing tells the engine the window is unknown.
    /// </summary>
    [Fact]
    public async Task ADroppedConnection_Reconnects_WithoutWithdrawingTheWindow()
    {
        int port = SecsTestPort.Free();
        var profile = new FakeEap { State = 0 };
        using var link = Link(silence: TimeSpan.FromSeconds(60));
        link.Configure(To(port), 30);
        await using (var eap = new Eap(profile, port))
        {
            Assert.True(await Wait(() => link.Status.State == EapLinkState.Connected && link.Status.LastReading is not null));
        }
        Assert.True(await Wait(() => link.Status.State != EapLinkState.Connected));
        await using (var eap2 = new Eap(profile, port))
        {
            Assert.True(await Wait(() => link.Status.State == EapLinkState.Connected, 30));
        }
        Assert.DoesNotContain(_reports, r => r.State == ToolContextReportState.Unknown);
    }

    /// <summary>Nothing for the timeout — no S1F3 answered, no event — withdraws the window once,
    /// not once per check.</summary>
    [Fact]
    public async Task Silence_WithdrawsTheWindowOnce()
    {
        int port = SecsTestPort.Free();
        var profile = new FakeEap { State = 0 };
        using var link = Link(silence: TimeSpan.FromMilliseconds(1500));
        link.Configure(To(port), 30);
        await using (var eap = new Eap(profile, port))
        {
            Assert.True(await Wait(() => link.Status.LastReading is not null));
        }
        Assert.True(await Wait(() => link.Status.Silent));
        await Task.Delay(2000);

        var unknown = Assert.Single(_reports, r => r.State == ToolContextReportState.Unknown);
        Assert.Contains("nothing from the EAP", unknown.Reason);
    }

    /// <summary>A link that has never heard the EAP has no window to withdraw — the same events
    /// may be arriving over SECS TOOL_EVENT.</summary>
    [Fact]
    public async Task ALinkThatNeverSpoke_WithdrawsNothing()
    {
        using var link = Link(silence: TimeSpan.FromMilliseconds(500));
        link.Configure(To(SecsTestPort.Free()), 30);   // nobody listening
        await Task.Delay(1500);
        Assert.Empty(_reports);
    }

    /// <summary>
    /// An EAP that is not there: the status says so, the audit log says it once — not once per
    /// attempt — and the link connects by itself when the EAP appears.
    /// </summary>
    [Fact]
    public async Task AnUnreachableEap_IsLoggedOnce_AndConnectedToWhenItAppears()
    {
        using var log = new LogCapture();
        int port = SecsTestPort.Free();
        using (var link = Link(log.Logger))
        {
            link.Configure(To(port), 30);
            Assert.True(await Wait(() => link.Status.Detail.Contains("cannot reach the EAP")));
            await Task.Delay(1500);   // several more attempts at T5 = 0.5 s
            await using var eap = new Eap(new FakeEap(), port);
            Assert.True(await Wait(() => link.Status.State == EapLinkState.Connected, 30));
        }

        var lines = log.Lines();
        Assert.Single(lines, l => l.Contains("EapLinkConnectFailed"));
        Assert.Contains(lines, l => l.Contains("EapLinkConnected") && l.Contains("failed attempt"));
    }

    // ---------------------------------------------------------------- configuration and trace

    /// <summary>The tab saves everything at once; saving it with the link settings unchanged
    /// must not drop the EAP connection.</summary>
    [Fact]
    public async Task ReconfiguringWithTheSameSettings_KeepsTheConnection()
    {
        int port = SecsTestPort.Free();
        await using var eap = new Eap(new FakeEap(), port);
        using var link = Link();
        link.Configure(To(port), 30);
        Assert.True(await Wait(() => link.Status.State == EapLinkState.Connected));
        var since = link.Status.ConnectedSince;

        link.Configure(To(port), 30);
        await Task.Delay(500);

        Assert.Equal(EapLinkState.Connected, link.Status.State);
        Assert.Equal(since, link.Status.ConnectedSince);
        Assert.Equal(1, eap.Count("S2F37 enable"));
    }

    [Fact]
    public async Task TheTrafficGoesToItsOwnDayFile()
    {
        int port = SecsTestPort.Free();
        await using (var eap = new Eap(new FakeEap(), port))
        using (var link = Link())
        {
            link.Configure(To(port), 30);
            Assert.True(await Wait(() => link.Status.LastReading is not null));
        }

        var file = Assert.Single(Directory.GetFiles(_dir, "eap_*.log"));
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var text = new StreamReader(fs).ReadToEnd();
        Assert.Contains("S1F3", text);
        Assert.Contains("S2F37", text);
        Assert.Empty(Directory.GetFiles(_dir, "secs_*.log"));
    }
}

/// <summary>Socket tests for the EAP link share one collection so they never fight over ports.</summary>
[CollectionDefinition("eap-secs", DisableParallelization = true)]
public sealed class EapSecsCollection { }
