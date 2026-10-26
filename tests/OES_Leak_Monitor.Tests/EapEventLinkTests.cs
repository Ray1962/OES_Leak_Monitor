using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// The line rules of <c>docs/eap-tool-event-tcp-spec-zh-TW.md</c> §4–§5, without a socket.
///
/// <para>Strict on purpose: the EAP's side is new code, and a line refused with code 3 at
/// acceptance is cheaper than one guessed at in production.</para>
/// </summary>
public sealed class EapLineParseTests
{
    [Fact]
    public void AnEvent_Parses()
    {
        var line = EapEventLink.Parse("EVT,3100439529,2026102614014000,128");
        Assert.Equal(EapLineKind.Event, line.Kind);
        Assert.Equal(3100439529u, line.Ceid);
        Assert.Equal(128u, line.Seq);
        Assert.Equal(new DateTime(2026, 10, 26, 14, 1, 40), line.Time);
    }

    [Theory]
    [InlineData("EVT,3100439529,20261026140140,1")]   // 14-digit TIME
    [InlineData("EVT,3100439529,,1")]                  // TIME left empty
    public void AnEvent_AcceptsTheOtherTimeForms(string text) =>
        Assert.Equal(EapLineKind.Event, EapEventLink.Parse(text).Kind);

    [Fact]
    public void AHeartbeat_InWindow_Parses()
    {
        var line = EapEventLink.Parse("HB,1,3100439529,2026102614014000,1,128");
        Assert.Equal(EapLineKind.Heartbeat, line.Kind);
        var hb = line.Heartbeat!;
        Assert.Equal(ToolContextReportState.InWindow, hb.State);
        Assert.Equal(3100439529u, hb.WindowCeid);
        Assert.True(hb.ToolLink);
        Assert.Equal(128u, hb.Seq);
    }

    [Theory]
    [InlineData("HB,0,,,1,127", ToolContextReportState.Outside, true)]
    [InlineData("HB,2,,,0,0", ToolContextReportState.Unknown, false)]
    public void AHeartbeat_OutsideOrUnknown_Parses(string text, ToolContextReportState state, bool link)
    {
        var hb = EapEventLink.Parse(text).Heartbeat!;
        Assert.Equal(state, hb.State);
        Assert.Equal(link, hb.ToolLink);
        Assert.Null(hb.WindowCeid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankLine_IsBlank(string text) => Assert.Equal(EapLineKind.Blank, EapEventLink.Parse(text).Kind);

    /// <summary>Lower case, a typo, a greeting: not a type this end knows — <c>ERR,1</c>.</summary>
    [Theory]
    [InlineData("HELLO")]
    [InlineData("evt,3100439529,,1")]
    public void AnUnknownType_IsSaidSo(string text) =>
        Assert.Equal(EapLineKind.UnknownType, EapEventLink.Parse(text).Kind);

    /// <summary>
    /// A bad field in an <c>EVT</c> whose sequence number can still be read is answered with
    /// <c>ACK,SEQ,3</c>, so the EAP knows which event was refused; without a readable SEQ there is
    /// nothing to acknowledge and the answer is <c>ERR,3</c>.
    /// </summary>
    [Theory]
    [InlineData("EVT,31004395X9,2026102614014000,131", "ACK", 131u)]
    [InlineData("EVT,3100439529,2026-10-26,131", "ACK", 131u)]
    [InlineData("EVT,3100439529,20261399140140,131", "ACK", 131u)]   // month 13
    [InlineData("EVT,99999999999,,131", "ACK", 131u)]                // 11 digits
    [InlineData("EVT, 3100439529,,131", "ACK", 131u)]                // a space
    [InlineData("EVT,3100439529,,x", "ERR", null)]
    [InlineData("EVT,3100439529,1", "ERR", null)]                    // 3 fields
    [InlineData("EVT,3100439529,,1,extra", "ERR", null)]             // 5 fields
    [InlineData("EVT,3100439529,,1é", "ERR", null)]                  // not ASCII
    public void AMalformedEvent_IsAnsweredInTheRightForm(string text, string replyAs, uint? seq)
    {
        var line = EapEventLink.Parse(text);
        Assert.Equal(EapLineKind.Malformed, line.Kind);
        Assert.Equal(replyAs, line.ReplyAs);
        Assert.Equal(seq, line.Seq);
    }

    [Theory]
    [InlineData("HB,3,,,1,1")]                         // STATE out of range
    [InlineData("HB,1,,,1,1")]                         // in window with no WINCEID
    [InlineData("HB,0,3100439529,,1,1")]               // WINCEID with STATE 0
    [InlineData("HB,0,,,2,1")]                         // TOOLLINK out of range
    [InlineData("HB,0,,,1")]                           // 5 fields
    public void AMalformedHeartbeat_IsAnsweredWithHbAck(string text)
    {
        var line = EapEventLink.Parse(text);
        Assert.Equal(EapLineKind.Malformed, line.Kind);
        Assert.Equal("HBACK", line.ReplyAs);
    }
}

/// <summary>
/// The link against a fake EAP over loopback: the OES connects out, every line is answered once,
/// and the window is handed to the engine's two entry points.
///
/// <para>What this guards, beyond the line rules: that a resend after a lost ACK is applied once;
/// that a dropped connection alone leaves the window alone while a silence withdraws it — once;
/// and that saving unrelated settings does not drop the connection.</para>
/// </summary>
public sealed class EapEventLinkWireTests : IDisposable
{
    private const uint ProcessStart = 3100439529, ProcessEnd = 3100439530;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oeslm-eap-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<(uint Ceid, DateTime Time)> _events = new();
    private readonly ConcurrentQueue<ToolContextReport> _reports = new();
    private ToolEventAck _eventAck = ToolEventAck.Accepted;
    private ToolEventAck _reportAck = ToolEventAck.Accepted;

    public EapEventLinkWireTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private EapEventLink Link(SystemLogger? log = null, TimeSpan? silence = null) =>
        new(log, _dir,
            (ceid, t) => { _events.Enqueue((ceid, t)); return _eventAck; },
            r => { _reports.Enqueue(r); return _reportAck; })
        {
            ReconnectDelay = TimeSpan.FromMilliseconds(100),
            ConnectTimeout = TimeSpan.FromSeconds(2),
            HeartbeatTimeoutOverride = silence ?? TimeSpan.FromSeconds(30),
        };

    private static ToolEventTcpSettings To(int port) => new()
    {
        Enabled = true, RemoteHost = "127.0.0.1", RemotePort = port,
    };

    /// <summary>The EAP end: listens, accepts the OES, sends lines and reads the answers.</summary>
    private sealed class FakeEap : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private TcpClient? _client;
        private StreamReader? _reader;
        private Stream? _stream;
        public int Accepted;

        public FakeEap() => _listener.Start();

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public async Task AcceptAsync(int seconds = 10)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            _client = await _listener.AcceptTcpClientAsync(cts.Token);
            Interlocked.Increment(ref Accepted);
            _stream = _client.GetStream();
            _reader = new StreamReader(_stream, Encoding.ASCII);
        }

        public async Task SendRawAsync(string text) =>
            await _stream!.WriteAsync(Encoding.ASCII.GetBytes(text));

        /// <summary>Sends one line and returns the answer.</summary>
        public async Task<string> AskAsync(string line)
        {
            await SendRawAsync(line + "\r\n");
            return await ReadAsync();
        }

        public async Task<string> ReadAsync(int seconds = 5)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            return await _reader!.ReadLineAsync(cts.Token) ?? "<closed>";
        }

        /// <summary>Whether the OES closed its end — a read that returns nothing.</summary>
        public async Task<bool> ClosedByPeerAsync(int seconds = 5)
        {
            try { return await ReadAsync(seconds) == "<closed>"; }
            catch (IOException) { return true; }
        }

        public void DropClient()
        {
            _client?.Close();
            _client = null;
        }

        public void Dispose()
        {
            _client?.Close();
            _listener.Stop();
        }
    }

    // ---------------------------------------------------------------- the conversation

    [Fact]
    public async Task TheOesConnectsOut_AndAnswersEveryLineOnce()
    {
        using var eap = new FakeEap();
        using var link = Link();
        link.Configure(To(eap.Port), 30);
        await eap.AcceptAsync();
        Assert.True(await SecsTestPort.WaitAsync(() => link.Status.State == EapLinkState.Connected));

        Assert.Equal("HBACK,0", await eap.AskAsync("HB,0,,,1,127"));
        await eap.SendRawAsync("\r\n");   // blank: ignored, not answered
        Assert.Equal("ACK,128,0", await eap.AskAsync("EVT,3100439529,2026102614014000,128"));
        Assert.Equal("HBACK,0", await eap.AskAsync("HB,1,3100439529,2026102614014000,1,128"));

        var ev = Assert.Single(_events);
        Assert.Equal(ProcessStart, ev.Ceid);
        Assert.Equal(new DateTime(2026, 10, 26, 14, 1, 40), ev.Time);
        Assert.Equal(new[] { ToolContextReportState.Outside, ToolContextReportState.InWindow },
            _reports.Select(r => r.State));
        Assert.Equal(ProcessStart, _reports.Last().StartCeid);
        Assert.Equal(128u, link.Status.LastHeartbeat!.Seq);
    }

    /// <summary>Both line endings are accepted.</summary>
    [Fact]
    public async Task ALineEndingInLfAlone_IsAccepted()
    {
        using var eap = new FakeEap();
        using var link = Link();
        link.Configure(To(eap.Port), 30);
        await eap.AcceptAsync();

        await eap.SendRawAsync("EVT,3100439529,,1\n");
        Assert.Equal("ACK,1,0", await eap.ReadAsync());
    }

    /// <summary>
    /// The ACK was lost and the EAP resends with the same SEQ (spec §7.4): acknowledged again,
    /// applied once — a second Start would log a spurious "window had no End".
    /// </summary>
    [Fact]
    public async Task AResend_IsAcknowledged_ButAppliedOnce()
    {
        using var eap = new FakeEap();
        using var link = Link();
        link.Configure(To(eap.Port), 30);
        await eap.AcceptAsync();

        Assert.Equal("ACK,5,0", await eap.AskAsync("EVT,3100439529,,5"));
        Assert.Equal("ACK,5,0", await eap.AskAsync("EVT,3100439529,,5"));
        Assert.Single(_events);
    }

    /// <summary>
    /// After an OES restart the first heartbeat names the EVT the EAP is about to resend. That
    /// event has not been handled here, so it must not be taken for a duplicate.
    /// </summary>
    [Fact]
    public async Task TheEventAHeartbeatNames_IsStillApplied()
    {
        using var eap = new FakeEap();
        using var link = Link();
        link.Configure(To(eap.Port), 30);
        await eap.AcceptAsync();

        Assert.Equal("HBACK,0", await eap.AskAsync("HB,0,,,1,129"));
        Assert.Equal("ACK,129,0", await eap.AskAsync("EVT,3100439530,,129"));
        Assert.Equal(ProcessEnd, Assert.Single(_events).Ceid);
    }

    [Fact]
    public async Task AGapAndARestart_AreEachLogged()
    {
        using var log = new LogCapture();
        using (var eap = new FakeEap())
        using (var link = Link(log.Logger))
        {
            link.Configure(To(eap.Port), 30);
            await eap.AcceptAsync();

            await eap.AskAsync("EVT,3100439529,,10");
            await eap.AskAsync("EVT,3100439530,,13");    // 11 and 12 never arrived
            await eap.AskAsync("HB,2,,,1,0");             // the EAP restarted
            await eap.AskAsync("EVT,3100439529,,1");
            Assert.Equal(3, _events.Count);
        }

        var lines = log.Lines();
        var gap = Assert.Single(lines, l => l.Contains("EapEventSequenceGap"));
        Assert.Contains("2 event(s)", gap);
        Assert.Single(lines, l => l.Contains("EapSequenceReset"));
    }

    // ---------------------------------------------------------------- answers

    [Fact]
    public async Task EachKindOfBadLine_GetsItsAnswer_AndTheConnectionStays()
    {
        using var eap = new FakeEap();
        using var link = Link();
        link.Configure(To(eap.Port), 30);
        await eap.AcceptAsync();

        Assert.Equal("ACK,131,3", await eap.AskAsync("EVT,31004395X9,2026102614014000,131"));
        Assert.Equal("ERR,3", await eap.AskAsync("EVT,1,2"));
        Assert.Equal("HBACK,3", await eap.AskAsync("HB,7,,,1,1"));
        Assert.Equal("ERR,1", await eap.AskAsync("HELLO"));
        Assert.Equal("ERR,3", await eap.AskAsync(new string('9', EapEventLink.MaxLineLength + 1)));
        Assert.Equal("ACK,132,0", await eap.AskAsync("EVT,3100439529,,132"));   // still talking
        Assert.Single(_events);
    }

    [Fact]
    public async Task WithToolEventsOff_TheAnswerIs2()
    {
        _eventAck = ToolEventAck.Disabled;
        _reportAck = ToolEventAck.Disabled;
        using var eap = new FakeEap();
        using var link = Link();
        link.Configure(To(eap.Port), 30);
        await eap.AcceptAsync();

        Assert.Equal("ACK,1,2", await eap.AskAsync("EVT,3100439529,,1"));
        Assert.Equal("HBACK,2", await eap.AskAsync("HB,0,,,1,1"));
    }

    /// <summary>An End named as the window's opening event: the engine refuses, the EAP is told.</summary>
    [Fact]
    public async Task AHeartbeatTheTableContradicts_IsAnswered3()
    {
        _reportAck = ToolEventAck.Invalid;
        using var eap = new FakeEap();
        using var link = Link();
        link.Configure(To(eap.Port), 30);
        await eap.AcceptAsync();

        Assert.Equal("HBACK,3", await eap.AskAsync("HB,1,3100439530,,1,1"));
    }

    /// <summary>A heartbeat saying the EAP has lost the tool sets the window unknown, whatever
    /// its STATE says.</summary>
    [Fact]
    public async Task ToolLinkDown_ReportsUnknown()
    {
        using var eap = new FakeEap();
        using var link = Link();
        link.Configure(To(eap.Port), 30);
        await eap.AcceptAsync();

        await eap.AskAsync("HB,0,,,0,1");
        var report = Assert.Single(_reports);
        Assert.Equal(ToolContextReportState.Unknown, report.State);
        Assert.Contains("no SECS link", report.Reason);
    }

    // ---------------------------------------------------------------- disconnects and silence

    /// <summary>
    /// The EAP drops the connection and takes it back within the timeout: the OES reconnects by
    /// itself, and nothing tells the engine the window is unknown — the heartbeat on accept puts
    /// it right sooner than a silence ever would.
    /// </summary>
    [Fact]
    public async Task ADroppedConnection_Reconnects_WithoutWithdrawingTheWindow()
    {
        using var eap = new FakeEap();
        using var link = Link(silence: TimeSpan.FromSeconds(5));
        link.Configure(To(eap.Port), 30);
        await eap.AcceptAsync();
        await eap.AskAsync("HB,1,3100439529,,1,1");

        eap.DropClient();
        await eap.AcceptAsync();
        Assert.Equal("HBACK,0", await eap.AskAsync("HB,1,3100439529,,1,1"));

        Assert.Equal(2, eap.Accepted);
        Assert.DoesNotContain(_reports, r => r.State == ToolContextReportState.Unknown);
    }

    /// <summary>
    /// Nothing at all for the timeout: the window is withdrawn — once, not once per check — and
    /// the OES closes the silent connection and connects again (spec §7.3).
    /// </summary>
    [Fact]
    public async Task Silence_WithdrawsTheWindowOnce_AndReconnects()
    {
        using var eap = new FakeEap();
        using var link = Link(silence: TimeSpan.FromMilliseconds(400));
        link.Configure(To(eap.Port), 30);
        await eap.AcceptAsync();
        await eap.AskAsync("HB,1,3100439529,,1,1");

        Assert.True(await eap.ClosedByPeerAsync());
        Assert.True(await SecsTestPort.WaitAsync(() => link.Status.Silent));
        await eap.AcceptAsync();
        await Task.Delay(1200);   // several more timeouts' worth, with no line

        Assert.Single(_reports, r => r.State == ToolContextReportState.Unknown);
        Assert.Contains("nothing from the EAP", _reports.Last(r => r.State == ToolContextReportState.Unknown).Reason);
    }

    /// <summary>A link that has never heard anything has no window to withdraw — the same
    /// events may be arriving over SECS.</summary>
    [Fact]
    public async Task ALinkThatNeverSpoke_WithdrawsNothing()
    {
        using var eap = new FakeEap();
        using var link = Link(silence: TimeSpan.FromMilliseconds(300));
        link.Configure(To(eap.Port), 30);
        await eap.AcceptAsync();
        await Task.Delay(1000);

        Assert.Empty(_reports);
    }

    // ---------------------------------------------------------------- configuration

    [Fact]
    public async Task UnreachableEap_KeepsRetrying_AndSaysWhy()
    {
        int port = SecsTestPort.Free();   // nobody listening
        using var link = Link();
        link.Configure(To(port), 30);

        Assert.True(await SecsTestPort.WaitAsync(() => link.Status.State == EapLinkState.Waiting));
        Assert.StartsWith("Could not connect", link.Status.Detail);

        using var eap = new FakeEapOn(port);
        Assert.True(await SecsTestPort.WaitAsync(() => link.Status.State == EapLinkState.Connected));
    }

    /// <summary>The SECS tab saves everything at once; saving it with the link settings unchanged
    /// must not drop the EAP connection.</summary>
    [Fact]
    public async Task ReconfiguringWithTheSameSettings_KeepsTheConnection()
    {
        using var eap = new FakeEap();
        using var link = Link();
        link.Configure(To(eap.Port), 30);
        await eap.AcceptAsync();
        Assert.True(await SecsTestPort.WaitAsync(() => link.Status.State == EapLinkState.Connected));

        link.Configure(To(eap.Port), 30);
        Assert.Equal("ACK,1,0", await eap.AskAsync("EVT,3100439529,,1"));
        Assert.Equal(1, eap.Accepted);
    }

    [Fact]
    public async Task SwitchedOff_ClosesTheConnection()
    {
        using var eap = new FakeEap();
        using var link = Link();
        link.Configure(To(eap.Port), 30);
        await eap.AcceptAsync();

        var off = To(eap.Port);
        off.Enabled = false;
        link.Configure(off, 30);

        Assert.Equal(EapLinkState.Disabled, link.Status.State);
        Assert.True(await eap.ClosedByPeerAsync());
    }

    /// <summary>Every line in and out lands in its own day file, beside — not inside — the SECS trace.</summary>
    [Fact]
    public async Task TheTrafficGoesToItsOwnDayFile()
    {
        using (var eap = new FakeEap())
        using (var link = Link())
        {
            link.Configure(To(eap.Port), 30);
            await eap.AcceptAsync();
            await eap.AskAsync("EVT,3100439529,,1");
        }

        var file = Assert.Single(Directory.GetFiles(_dir, "eap_*.log"));
        Assert.Equal($"eap_{DateTime.Now:yyyyMMdd}.log", Path.GetFileName(file));
        var text = ReadShared(file);
        Assert.Contains("EAP -> OES  EVT,3100439529,,1", text);
        Assert.Contains("OES -> EAP  ACK,1,0", text);
        Assert.Empty(Directory.GetFiles(_dir, "secs_*.log"));
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new StreamReader(fs).ReadToEnd();
    }

    /// <summary>A listener on a given port, for the test that starts with nobody listening.</summary>
    private sealed class FakeEapOn : IDisposable
    {
        private readonly TcpListener _listener;
        public FakeEapOn(int port)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _ = _listener.AcceptTcpClientAsync();
        }
        public void Dispose() => _listener.Stop();
    }
}
