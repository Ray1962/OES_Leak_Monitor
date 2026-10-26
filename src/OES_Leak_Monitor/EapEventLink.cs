using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OES_Leak_Monitor;

/// <summary>Where the EAP link is in its connect / read cycle.</summary>
public enum EapLinkState
{
    /// <summary>Switched off in settings, or not configured with an address.</summary>
    Disabled,
    Connecting,
    Connected,
    /// <summary>The last attempt failed or the connection dropped; the next attempt is pending.</summary>
    Waiting,
}

/// <summary>The last heartbeat as the EAP sent it, before the CEID table has read it.</summary>
public sealed record EapHeartbeat(ToolContextReportState State, uint? WindowCeid, DateTime? WindowStart,
    bool ToolLink, uint Seq);

/// <summary>
/// What the SECS tab shows for the link. Immutable and replaced whole, like
/// <see cref="ToolEventStatus"/>, so the UI thread reads it without a lock.
/// </summary>
/// <param name="Silent">No line for longer than the heartbeat timeout: the link has told the
/// engine the window is no longer known.</param>
/// <param name="Detail">Why it is not connected, in words; "" when connected.</param>
public sealed record EapLinkStatus(EapLinkState State, string Endpoint, DateTime? ConnectedSince,
    DateTime? LastLineAt, EapHeartbeat? LastHeartbeat, bool Silent, string Detail)
{
    public static readonly EapLinkStatus Disabled = new(EapLinkState.Disabled, "", null, null, null, false, "");
}

/// <summary>What a received line is, before anything is done with it.</summary>
public enum EapLineKind
{
    Blank,
    Event,
    Heartbeat,
    /// <summary>The first field is not a message type this end knows: <c>ERR,1</c>.</summary>
    UnknownType,
    /// <summary>A known type whose fields do not parse: answered with code 3 in the form
    /// <see cref="EapLine.ReplyAs"/> says.</summary>
    Malformed,
}

/// <summary>
/// One line from the EAP, parsed. For a <see cref="EapLineKind.Malformed"/> line,
/// <see cref="ReplyAs"/> is the message type the answer takes — <c>ACK</c> when the sequence
/// number could still be read, so the EAP can tell which event was refused, else <c>HBACK</c>
/// or <c>ERR</c>.
/// </summary>
public sealed record EapLine(EapLineKind Kind, uint? Seq = null, uint Ceid = 0, DateTime? Time = null,
    EapHeartbeat? Heartbeat = null, string ReplyAs = "", string Error = "");

/// <summary>
/// The OES end of the EAP's plain-TCP event link (<c>docs/eap-tool-event-tcp-spec-zh-TW.md</c>).
/// It exists because the site's SECS host is the FDC: HSMS-SS allows one host per session, so the
/// EAP has no way to send <c>TOOL_EVENT</c>, and forwards the tool's CEIDs here instead.
///
/// <para><b>The OES is the client.</b> The EAP listens on the address it gave; this end connects,
/// and retries every <see cref="ReconnectDelay"/> for as long as it is switched on. Every line is
/// answered with exactly one line.</para>
///
/// <para><b>It parses and hands on; it decides nothing.</b> An <c>EVT</c> goes to the engine's
/// <c>ReceiveToolEvent</c> exactly as an S2F41 does, and a heartbeat to
/// <c>ReportToolContext</c>, which compares it with the window held and corrects a difference.
/// The CEID table, the windows and VIDs 030–032 are the engine's, unchanged.</para>
///
/// <para><b>A dropped connection by itself changes nothing.</b> Only
/// <see cref="ToolEventTcpSettings.HeartbeatTimeoutSeconds"/> with no line at all makes the link
/// tell the engine the window is unknown — the reconnect plus the heartbeat the EAP sends on
/// accept restores it sooner than that, and flipping the window on every blip would switch the
/// class-scoped ratios between two ways of judging for nothing. It says so once per silence, and
/// only after having heard something: a link that has never spoken has nothing to withdraw, and
/// the same events may be arriving over SECS.</para>
/// </summary>
public sealed class EapEventLink : IDisposable
{
    /// <summary>Longest line accepted, newline excluded.</summary>
    public const int MaxLineLength = 256;

    private readonly SystemLogger? _log;
    private readonly string _logDirectory;
    private readonly Func<uint, DateTime, ToolEventAck> _eventSink;
    private readonly Func<ToolContextReport, ToolEventAck> _contextSink;

    private readonly object _gate = new();
    private ToolEventTcpSettings? _settings;
    private int _retentionDays = -1;
    private SecsLogFile? _trace;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    // Its own lock: the session task clears it on the way out, while Configure holds _gate and
    // waits for that task — one lock for both would make every reconfigure wait out the timeout.
    private readonly object _sessionGate = new();
    private CancellationTokenSource? _sessionCts;
    private readonly object _statusGate = new();
    private volatile EapLinkStatus _status = EapLinkStatus.Disabled;

    // Last line received (ticks of DateTime.Now) and whether a silence would withdraw anything —
    // see the class note.
    private long _lastLineTicks;
    private volatile bool _silenceArmed;

    // Sequence bookkeeping. Read and written by the session task only. Two numbers because they
    // answer different questions: _lastEventSeq is the last EVT actually handled, which is what a
    // resend is a duplicate of; _lastKnownSeq is the highest number heard from either kind, which
    // is what a gap is measured from. A heartbeat must not feed the first — after an OES restart
    // it names the EVT the EAP is about to resend, and treating that as already handled would
    // drop it.
    private uint? _lastEventSeq;
    private uint? _lastKnownSeq;

    // Log throttling.
    private int _failedAttempts;
    private DateTime? _outageSince;
    private DateTime _lastFailureLogged;
    private readonly Dictionary<string, DateTime> _rejectLogged = new();
    private readonly HashSet<uint> _ignoredLogged = new();

    public EapEventLink(SystemLogger? systemLogger, string logDirectory,
        Func<uint, DateTime, ToolEventAck> eventSink, Func<ToolContextReport, ToolEventAck> contextSink)
    {
        _log = systemLogger;
        _logDirectory = logDirectory;
        _eventSink = eventSink;
        _contextSink = contextSink;
    }

    /// <summary>Pause between connection attempts. Ten seconds in production.</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Replaces <see cref="ToolEventTcpSettings.HeartbeatTimeoutSeconds"/>, whose saved range
    /// starts at 35 s — so a test can watch a silence without waiting for one.
    /// </summary>
    public TimeSpan? HeartbeatTimeoutOverride { get; init; }

    /// <summary>How often a continuing connect failure is summarised in the system log after
    /// the first one. An EAP down for an hour would otherwise write 360 entries.</summary>
    public TimeSpan FailureSummaryInterval { get; init; } = TimeSpan.FromHours(1);

    public EapLinkStatus Status => _status;

    /// <summary>Raised on every status change, on a worker thread.</summary>
    public event Action<EapLinkStatus>? StatusChanged;

    /// <summary>The traffic file being written, or "" before the first line.</summary>
    public string LogFilePath => _trace?.CurrentPath ?? "";

    /// <summary>
    /// Applies the settings. A link already running with the same settings is left alone: the
    /// SECS tab saves everything at once, and dropping the EAP connection because someone
    /// changed a SECS timer would put a needless gap in the trace.
    /// </summary>
    public void Configure(ToolEventTcpSettings? settings, int retentionDays)
    {
        var next = (settings ?? new ToolEventTcpSettings()).Clone();
        lock (_gate)
        {
            if (retentionDays != _retentionDays)
            {
                _trace?.Dispose();
                _trace = new SecsLogFile(_logDirectory, retentionDays, "eap_");
                _trace.WriteFailed += m => _log?.LogSystemEvent(LogSeverity.Warning, "EapLogWriteFailed",
                    "The EAP traffic log could not be written; the link keeps running without it", value: m);
                _retentionDays = retentionDays;
            }
            if (_settings is not null && Same(_settings, next) && (_runTask is not null) == Runnable(next))
                return;
            StopLocked();
            _settings = next;
            _lastEventSeq = null;
            _lastKnownSeq = null;
            if (!Runnable(next))
            {
                Update(_ => EapLinkStatus.Disabled);
                return;
            }
            _runCts = new CancellationTokenSource();
            var token = _runCts.Token;
            Interlocked.Exchange(ref _lastLineTicks, DateTime.Now.Ticks);
            _silenceArmed = false;
            _runTask = Task.WhenAll(
                Task.Run(() => RunAsync(next, token)),
                Task.Run(() => WatchSilenceAsync(next, token)));
        }
    }

    private static bool Runnable(ToolEventTcpSettings s) =>
        s.Enabled && !string.IsNullOrWhiteSpace(s.RemoteHost) && s.RemotePort is > 0 and <= 65535;

    private static bool Same(ToolEventTcpSettings a, ToolEventTcpSettings b) =>
        a.Enabled == b.Enabled &&
        string.Equals(a.RemoteHost?.Trim(), b.RemoteHost?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        a.RemotePort == b.RemotePort &&
        a.HeartbeatTimeoutSeconds.Equals(b.HeartbeatTimeoutSeconds);

    /// <summary>Caller holds <c>_gate</c>.</summary>
    private void StopLocked()
    {
        if (_runCts is null) return;
        _runCts.Cancel();
        lock (_sessionGate) _sessionCts?.Cancel();
        try { _runTask?.Wait(TimeSpan.FromSeconds(3)); } catch { /* cancellation surfaces here */ }
        _runCts.Dispose();
        _runCts = null;
        _runTask = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopLocked();
            _trace?.Dispose();
            _trace = null;
        }
    }

    // ---- connect loop ----------------------------------------------------------

    private async Task RunAsync(ToolEventTcpSettings s, CancellationToken token)
    {
        string host = s.RemoteHost.Trim();
        string endpoint = $"{host}:{s.RemotePort}";
        while (!token.IsCancellationRequested)
        {
            Update(st => st with { State = EapLinkState.Connecting, Endpoint = endpoint, ConnectedSince = null });
            using var client = new TcpClient();
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                connectCts.CancelAfter(ConnectTimeout);
                await client.ConnectAsync(host, s.RemotePort, connectCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                string reason = ex is OperationCanceledException
                    ? $"no answer within {ConnectTimeout.TotalSeconds:0} s"
                    : ex.Message;
                OnConnectFailed(endpoint, reason);
                Update(st => st with { State = EapLinkState.Waiting, Detail = $"Could not connect: {reason}" });
                if (!await DelayAsync(ReconnectDelay, token)) return;
                continue;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            OnConnected(endpoint);
            string lostReason;
            using (var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                lock (_sessionGate) _sessionCts = sessionCts;
                try
                {
                    lostReason = await ReadSessionAsync(client, sessionCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    Trace("connection closed (link reconfigured or stopped)");
                    return;
                }
                catch (OperationCanceledException)
                {
                    lostReason = $"no line for {Timeout(s).TotalSeconds:0} s";
                }
                catch (Exception ex)
                {
                    lostReason = ex.Message;
                }
                finally
                {
                    lock (_sessionGate) _sessionCts = null;
                }
            }

            Trace($"connection lost: {lostReason}");
            _log?.LogSystemEvent(LogSeverity.Warning, "EapLinkLost",
                $"The connection to the EAP at {endpoint} was lost ({lostReason}); reconnecting every " +
                $"{ReconnectDelay.TotalSeconds:0} s. The process window is kept unless nothing is " +
                $"heard for {Timeout(s).TotalSeconds:0} s",
                related: $"Endpoint={endpoint}");
            _outageSince = DateTime.Now;
            _failedAttempts = 0;
            Update(st => st with { State = EapLinkState.Waiting, ConnectedSince = null, Detail = $"Connection lost: {lostReason}" });
            if (!await DelayAsync(ReconnectDelay, token)) return;
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken token)
    {
        try { await Task.Delay(delay, token).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) { return false; }
    }

    private void OnConnectFailed(string endpoint, string reason)
    {
        var now = DateTime.Now;
        _failedAttempts++;
        _outageSince ??= now;
        Trace($"connect to {endpoint} failed: {reason}");
        if (_failedAttempts == 1)
        {
            _lastFailureLogged = now;
            _log?.LogSystemEvent(LogSeverity.Warning, "EapLinkConnectFailed",
                $"Could not connect to the EAP at {endpoint} ({reason}); retrying every " +
                $"{ReconnectDelay.TotalSeconds:0} s, summarised here every " +
                $"{FailureSummaryInterval.TotalMinutes:0} min until it succeeds",
                related: $"Endpoint={endpoint}");
        }
        else if (now - _lastFailureLogged >= FailureSummaryInterval)
        {
            _lastFailureLogged = now;
            _log?.LogSystemEvent(LogSeverity.Warning, "EapLinkConnectFailed",
                $"Still cannot connect to the EAP at {endpoint}: {_failedAttempts} attempts since " +
                $"{_outageSince:HH:mm:ss} (last: {reason})",
                related: $"Endpoint={endpoint},Attempts={_failedAttempts}");
        }
    }

    private void OnConnected(string endpoint)
    {
        var now = DateTime.Now;
        string after = _outageSince is { } since && (_failedAttempts > 0 || now - since > TimeSpan.FromSeconds(1))
            ? $" after {(now - since).TotalMinutes:0.#} min disconnected, {_failedAttempts} failed attempt(s)"
            : "";
        Trace($"connected to {endpoint}");
        _log?.LogSystemEvent(LogSeverity.Information, "EapLinkConnected",
            $"Connected to the EAP at {endpoint}{after}", related: $"Endpoint={endpoint}");
        _failedAttempts = 0;
        _outageSince = null;
        Update(st => st with { State = EapLinkState.Connected, Endpoint = endpoint, ConnectedSince = now, Detail = "" });
    }

    // ---- reading -----------------------------------------------------------------

    /// <summary>Reads lines until the EAP closes; returns why it ended.</summary>
    private async Task<string> ReadSessionAsync(TcpClient client, CancellationToken token)
    {
        var stream = client.GetStream();
        var buffer = new byte[4096];
        var line = new List<byte>(MaxLineLength + 2);
        bool overflow = false;
        while (true)
        {
            int n = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (n == 0) return "the EAP closed the connection";
            for (int i = 0; i < n; i++)
            {
                byte b = buffer[i];
                if (b != (byte)'\n')
                {
                    // One byte of slack for the CR of a CRLF line.
                    if (line.Count <= MaxLineLength) line.Add(b);
                    else overflow = true;
                    continue;
                }
                if (line.Count > 0 && line[^1] == (byte)'\r') line.RemoveAt(line.Count - 1);
                bool tooLong = overflow || line.Count > MaxLineLength;
                var bytes = line.ToArray();
                line.Clear();
                overflow = false;

                string? reply = HandleBytes(bytes, tooLong);
                if (reply is null) continue;
                Trace("OES -> EAP  " + reply);
                var outBytes = Encoding.ASCII.GetBytes(reply + "\r\n");
                await stream.WriteAsync(outBytes, token).ConfigureAwait(false);
            }
        }
    }

    private string? HandleBytes(byte[] bytes, bool tooLong)
    {
        // Latin-1 keeps every byte visible in the trace; Parse refuses anything outside ASCII.
        string text = Encoding.Latin1.GetString(bytes);
        if (text.Trim().Length == 0) return null;   // blank lines are ignored, not answered
        Trace("EAP -> OES  " + (tooLong ? text[..Math.Min(text.Length, MaxLineLength)] + "…" : text));
        Interlocked.Exchange(ref _lastLineTicks, DateTime.Now.Ticks);
        _silenceArmed = true;

        var parsed = tooLong
            ? new EapLine(EapLineKind.Malformed, ReplyAs: "ERR", Error: $"line longer than {MaxLineLength} characters")
            : Parse(text);
        string reply = Handle(parsed, text);
        Update(st => st with { LastLineAt = DateTime.Now, Silent = false });
        return reply;
    }

    /// <summary>Acts on one parsed line and returns the answer. Session task only.</summary>
    private string Handle(EapLine line, string raw)
    {
        switch (line.Kind)
        {
            case EapLineKind.Event:
                return HandleEvent(line);
            case EapLineKind.Heartbeat:
                return HandleHeartbeat(line.Heartbeat!, raw);
            case EapLineKind.UnknownType:
                LogRejected("type", raw, "a message type this end does not know");
                return "ERR,1";
            default:
                LogRejected("format", raw, line.Error);
                return line.ReplyAs switch
                {
                    "ACK" => $"ACK,{line.Seq},3",
                    "HBACK" => "HBACK,3",
                    _ => "ERR,3",
                };
        }
    }

    private string HandleEvent(EapLine line)
    {
        uint seq = line.Seq!.Value;
        if (_lastEventSeq == seq)
        {
            Trace($"(SEQ {seq} already handled — a resend; acknowledged, not applied again)");
            return $"ACK,{seq},0";
        }
        CheckSequence(seq, fromEvent: true);
        var ack = _eventSink(line.Ceid, line.Time ?? DateTime.Now);
        if (ack == ToolEventAck.Ignored) LogIgnoredOnce(line.Ceid);
        _lastEventSeq = seq;
        return $"ACK,{seq},{Code(ack)}";
    }

    private string HandleHeartbeat(EapHeartbeat hb, string raw)
    {
        var report = !hb.ToolLink
            ? new ToolContextReport(ToolContextReportState.Unknown, 0, "the EAP reports no SECS link to the tool")
            : hb.State switch
            {
                ToolContextReportState.Unknown => new ToolContextReport(ToolContextReportState.Unknown, 0,
                    "the EAP reports PM3's state unknown"),
                ToolContextReportState.InWindow => new ToolContextReport(ToolContextReportState.InWindow,
                    hb.WindowCeid ?? 0,
                    $"EAP heartbeat: PM3 in the window opened by CEID {hb.WindowCeid}" +
                    (hb.WindowStart is { } w ? $" at {w:HH:mm:ss}" : "")),
                _ => new ToolContextReport(ToolContextReportState.Outside, 0,
                    "EAP heartbeat: PM3 outside any process window"),
            };
        var ack = _contextSink(report);
        if (ack == ToolEventAck.Invalid)
        {
            LogRejected("format", raw, $"WINCEID {hb.WindowCeid} is an End event in the CEID table");
            return "HBACK,3";
        }
        CheckSequence(hb.Seq, fromEvent: false);
        Update(st => st with { LastHeartbeat = hb });
        return $"HBACK,{Code(ack)}";
    }

    private static int Code(ToolEventAck ack) => ack switch
    {
        ToolEventAck.Accepted or ToolEventAck.Ignored => 0,
        ToolEventAck.Disabled => 2,
        _ => 3,
    };

    /// <summary>
    /// Gap and restart detection against the highest number heard. An <c>EVT</c> is one past the
    /// last; a heartbeat repeats the last <c>EVT</c> sent, so anything above what was heard is
    /// that many lost. Smaller means the EAP started counting again.
    /// </summary>
    private void CheckSequence(uint seq, bool fromEvent)
    {
        if (_lastKnownSeq is not { } last)
        {
            _lastKnownSeq = seq;   // first number since this end started: nothing to compare
            return;
        }
        uint expected = fromEvent ? last + 1 : last;
        if (seq < last)
        {
            _log?.LogSystemEvent(LogSeverity.Information, "EapSequenceReset",
                $"The EAP's sequence number went back from {last} to {seq} — it has restarted",
                related: $"From={last},To={seq}");
            _lastEventSeq = null;
        }
        else if (seq > expected)
        {
            uint missing = seq - expected;
            _log?.LogSystemEvent(LogSeverity.Warning, "EapEventSequenceGap",
                $"{missing} event(s) from the EAP never arrived (sequence {last} then {seq}); the " +
                "heartbeat corrects the process window, the events themselves are not recovered",
                value: missing.ToString(CultureInfo.InvariantCulture),
                related: $"From={last},To={seq}");
        }
        if (seq < last || seq > last) _lastKnownSeq = seq;
    }

    private void LogRejected(string kind, string raw, string why)
    {
        var now = DateTime.Now;
        if (_rejectLogged.TryGetValue(kind, out var last) && now - last < TimeSpan.FromHours(1)) return;
        _rejectLogged[kind] = now;
        _log?.LogSystemEvent(LogSeverity.Warning, "EapLineRejected",
            $"A line from the EAP was refused ({why}); further refusals of this kind are logged at " +
            "most once an hour, the EAP traffic log has every one",
            value: raw.Length > 120 ? raw[..120] + "…" : raw);
    }

    private void LogIgnoredOnce(uint ceid)
    {
        if (!_ignoredLogged.Add(ceid)) return;
        _log?.LogSystemEvent(LogSeverity.Information, "ToolEventIgnored",
            $"Main tool event CEID {ceid} is not in the tool event table — acknowledged, nothing done " +
            "(logged once per CEID; the EAP traffic log has every occurrence)",
            related: $"CEID={ceid}");
    }

    // ---- silence -------------------------------------------------------------------

    private TimeSpan Timeout(ToolEventTcpSettings s) =>
        HeartbeatTimeoutOverride ??
        TimeSpan.FromSeconds(s.HeartbeatTimeoutSeconds > 0
            ? s.HeartbeatTimeoutSeconds
            : ToolEventTcpSettings.DefaultHeartbeatTimeoutSeconds);

    private async Task WatchSilenceAsync(ToolEventTcpSettings s, CancellationToken token)
    {
        var timeout = Timeout(s);
        var step = TimeSpan.FromMilliseconds(Math.Clamp(timeout.TotalMilliseconds / 5, 20, 1000));
        while (await DelayAsync(step, token))
        {
            if (!_silenceArmed) continue;
            var since = new DateTime(Interlocked.Read(ref _lastLineTicks));
            if (DateTime.Now - since < timeout) continue;

            _silenceArmed = false;
            string reason = $"nothing from the EAP for {timeout.TotalSeconds:0} s";
            Trace(reason + " — process window set to unknown, connection closed");
            _contextSink(new ToolContextReport(ToolContextReportState.Unknown, 0, reason));
            Update(st => st with { Silent = true });
            lock (_sessionGate) _sessionCts?.Cancel();   // the spec closes a silent connection and reconnects
        }
    }

    // ---- plumbing -----------------------------------------------------------------

    private void Trace(string line) => _trace?.Write(line);

    /// <summary>Read-modify-write of the status: the session and the silence watcher both
    /// change it, each touching different fields.</summary>
    private void Update(Func<EapLinkStatus, EapLinkStatus> change)
    {
        EapLinkStatus next;
        lock (_statusGate)
        {
            next = change(_status);
            if (next == _status) return;
            _status = next;
        }
        StatusChanged?.Invoke(next);
    }

    // ---- parsing ---------------------------------------------------------------------

    /// <summary>
    /// Parses one line (newline already removed). Pure, so the wire rules can be tested without a
    /// socket. Strict on purpose: the EAP's implementation is new, and a malformed line answered
    /// with code 3 at acceptance is cheaper than one guessed at in production.
    /// </summary>
    public static EapLine Parse(string text)
    {
        if (text.Trim().Length == 0) return new EapLine(EapLineKind.Blank);
        foreach (char c in text)
            if (c > 127)
                return new EapLine(EapLineKind.Malformed, ReplyAs: "ERR", Error: "a character outside ASCII");

        var f = text.Split(',');
        switch (f[0])
        {
            case "EVT":
            {
                if (f.Length != 4)
                    return new EapLine(EapLineKind.Malformed, ReplyAs: "ERR", Error: $"EVT with {f.Length} fields, 4 expected");
                if (ParseNumber(f[3]) is not { } seq)
                    return new EapLine(EapLineKind.Malformed, ReplyAs: "ERR", Error: $"unreadable SEQ '{f[3]}'");
                if (ParseNumber(f[1]) is not { } ceid)
                    return new EapLine(EapLineKind.Malformed, seq, ReplyAs: "ACK", Error: $"unreadable CEID '{f[1]}'");
                if (!TryParseTime(f[2], out var time))
                    return new EapLine(EapLineKind.Malformed, seq, ReplyAs: "ACK", Error: $"unreadable TIME '{f[2]}'");
                return new EapLine(EapLineKind.Event, seq, ceid, time);
            }
            case "HB":
            {
                if (f.Length != 6)
                    return new EapLine(EapLineKind.Malformed, ReplyAs: "HBACK", Error: $"HB with {f.Length} fields, 6 expected");
                ToolContextReportState? state = f[1] switch
                {
                    "0" => ToolContextReportState.Outside,
                    "1" => ToolContextReportState.InWindow,
                    "2" => ToolContextReportState.Unknown,
                    _ => null,
                };
                if (state is null)
                    return new EapLine(EapLineKind.Malformed, ReplyAs: "HBACK", Error: $"STATE '{f[1]}' is not 0, 1 or 2");
                bool? link = f[4] switch { "0" => false, "1" => true, _ => null };
                if (link is null)
                    return new EapLine(EapLineKind.Malformed, ReplyAs: "HBACK", Error: $"TOOLLINK '{f[4]}' is not 0 or 1");
                if (ParseNumber(f[5]) is not { } seq)
                    return new EapLine(EapLineKind.Malformed, ReplyAs: "HBACK", Error: $"unreadable SEQ '{f[5]}'");
                uint? winCeid = null;
                DateTime? winTime = null;
                if (state == ToolContextReportState.InWindow)
                {
                    if (ParseNumber(f[2]) is not { } c)
                        return new EapLine(EapLineKind.Malformed, ReplyAs: "HBACK", Error: $"STATE 1 with unreadable WINCEID '{f[2]}'");
                    if (!TryParseTime(f[3], out winTime))
                        return new EapLine(EapLineKind.Malformed, ReplyAs: "HBACK", Error: $"unreadable WINTIME '{f[3]}'");
                    winCeid = c;
                }
                else if (f[2].Length > 0 || f[3].Length > 0)
                {
                    return new EapLine(EapLineKind.Malformed, ReplyAs: "HBACK", Error: "WINCEID / WINTIME given with STATE other than 1");
                }
                return new EapLine(EapLineKind.Heartbeat, seq,
                    Heartbeat: new EapHeartbeat(state.Value, winCeid, winTime, link.Value, seq));
            }
            default:
                return new EapLine(EapLineKind.UnknownType, ReplyAs: "ERR", Error: $"unknown message type '{f[0]}'");
        }
    }

    /// <summary>1–10 ASCII digits that fit in 32 bits; nothing else — no sign, no spaces.</summary>
    private static uint? ParseNumber(string s) =>
        s.Length is >= 1 and <= 10 &&
        uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>Empty (no time given), or <c>YYYYMMDDhhmmsscc</c> / <c>YYYYMMDDhhmmss</c> local time.</summary>
    private static bool TryParseTime(string s, out DateTime? time)
    {
        time = null;
        if (s.Length == 0) return true;
        string? format = s.Length switch { 16 => "yyyyMMddHHmmssff", 14 => "yyyyMMddHHmmss", _ => null };
        if (format is null || !DateTime.TryParseExact(s, format, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var t))
            return false;
        time = t;
        return true;
    }
}
