using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Aqusen.Secs;
using Secs4Net;

namespace OES_Leak_Monitor;

/// <summary>Where the EAP link is.</summary>
public enum EapLinkState
{
    /// <summary>Switched off in settings.</summary>
    Disabled,
    /// <summary>Switched on, but the nine EAP ids are not all set (or clash): nothing is started.</summary>
    IdsMissing,
    /// <summary>Connecting or establishing communication.</summary>
    Connecting,
    /// <summary>Communicating and set up (online, events enabled).</summary>
    Connected,
}

/// <summary>S1–S7 as the last successful S1F3 read them.</summary>
public sealed record EapStatusReading(byte WindowState, uint WindowCeid, string WindowStart, byte ToolLink,
    uint LastCeid, string LastTime, uint Sequence, DateTime ReadAt);

/// <summary>
/// What the EAP tab shows for the link. Immutable and replaced whole, like
/// <see cref="ToolEventStatus"/>, so the UI thread reads it without a lock.
/// </summary>
/// <param name="Silent">No successful exchange for longer than the silence timeout: the link
/// has told the engine the window is no longer known.</param>
/// <param name="Detail">Why it is not connected, in words; "" when connected.</param>
public sealed record EapLinkStatus(EapLinkState State, string Endpoint, DateTime? ConnectedSince,
    EapStatusReading? LastReading, string LastEvent, bool Silent, string Detail)
{
    public static readonly EapLinkStatus Disabled = new(EapLinkState.Disabled, "", null, null, "", false, "");
}

/// <summary>
/// The OES end of the EAP link under scheme 2b (<c>docs/eap-tool-event-secs-spec-zh-TW.md</c>):
/// the OES is a SECS <b>host</b> connecting out to the EAP, which plays equipment. It exists
/// because the site's SECS host is the FDC — HSMS-SS allows one host per session, so the EAP
/// has no way to send <c>TOOL_EVENT</c> into the OES's own interface.
///
/// <para>On every connect it goes online (S1F17), enables the two EAP CEIDs (S2F37 — the
/// platform fixes its reports, so no S2F33/S2F35) and reads S1–S7 (S1F3); then it reads them
/// again every <see cref="PollInterval"/>. The poll is what the TCP link's heartbeat was: it
/// re-syncs the window after an OES restart and corrects one the EAP failed to move.</para>
///
/// <para><b>It parses and hands on; it decides nothing.</b> A forwarded event goes to the
/// engine's <c>ReceiveToolEvent</c> exactly as an S2F41 does; a poll, and a tool-link event, go
/// to <c>ReportToolContext</c>. The CEID table, the windows and VIDs 030–032 are the engine's.</para>
///
/// <para><b>A dropped connection by itself changes nothing</b>; <c>GemHost</c> reconnects.
/// Only <see cref="EapLinkSettings.SilenceTimeoutSeconds"/> with no successful S1F3 and no
/// S6F11 tells the engine the window is unknown — once per silence, and only after having heard
/// something. A successful S1F3 rather than an HSMS linktest is the measure on purpose: a
/// linktest says the socket is up, not that the EAP's application is alive.</para>
/// </summary>
public sealed class EapSecsLink : IDisposable
{
    private readonly SystemLogger? _log;
    private readonly string _logDirectory;
    private readonly Func<uint, DateTime, ToolEventAck> _eventSink;
    private readonly Func<ToolContextReport, ToolEventAck> _contextSink;

    private readonly object _gate = new();
    private EapLinkSettings? _settings;
    private int _retentionDays = -1;
    private SecsLogFile? _trace;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private GemHost? _host;

    private readonly object _statusGate = new();
    private volatile EapLinkStatus _status = EapLinkStatus.Disabled;

    // Last successful exchange (ticks of DateTime.Now) and whether a silence would withdraw
    // anything — see the class note.
    private long _lastGoodTicks;
    private volatile bool _silenceArmed;
    private volatile bool _pollNow;

    // Sequence bookkeeping, shared by the receive thread (events) and the loop (polls). Two
    // numbers because they answer different questions: _lastEventSeq is the last event actually
    // handled, which is what a resend is a duplicate of; _lastKnownSeq is the highest number heard
    // from either, which is what a gap is measured from. A poll's S7 must not feed the first —
    // after an OES restart it names the event the EAP is about to resend, and treating that as
    // handled would drop it.
    private readonly object _seqGate = new();
    private uint? _lastEventSeq;
    private uint? _lastKnownSeq;

    // Log throttling.
    private int _failedAttempts;
    private DateTime? _outageSince;
    private DateTime _lastFailureLogged;
    private readonly Dictionary<string, DateTime> _throttled = new();
    private readonly HashSet<uint> _ignoredLogged = new();

    public EapSecsLink(SystemLogger? systemLogger, string logDirectory,
        Func<uint, DateTime, ToolEventAck> eventSink, Func<ToolContextReport, ToolEventAck> contextSink)
    {
        _log = systemLogger;
        _logDirectory = logDirectory;
        _eventSink = eventSink;
        _contextSink = contextSink;
    }

    /// <summary>How often S1–S7 are read once communicating. Thirty seconds in production.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Replaces <see cref="EapLinkSettings.SilenceTimeoutSeconds"/>, whose saved range
    /// starts at 35 s — so a test can watch a silence without waiting for one.</summary>
    public TimeSpan? SilenceTimeoutOverride { get; init; }

    /// <summary>T3 for the link, in ms. 45 s in production (fixed, like T5–T8); a test shortens it.</summary>
    public int T3Milliseconds { get; init; } = 45_000;

    /// <summary>T5 (connect separation) for the link, in ms. 10 s in production; a test shortens it
    /// to watch a reconnect.</summary>
    public int T5Milliseconds { get; init; } = 10_000;

    /// <summary>How often a continuing connect failure is summarised after the first one.</summary>
    public TimeSpan FailureSummaryInterval { get; init; } = TimeSpan.FromHours(1);

    public EapLinkStatus Status => _status;

    /// <summary>Raised on every status change, on a worker thread.</summary>
    public event Action<EapLinkStatus>? StatusChanged;

    /// <summary>The traffic file being written, or "" before the first line.</summary>
    public string LogFilePath => _trace?.CurrentPath ?? "";

    /// <summary>
    /// Applies the settings. A link already running with the same settings is left alone: the
    /// EAP tab saves everything at once, and dropping the connection because someone changed the
    /// window limit would put a needless gap in the trace.
    /// </summary>
    public void Configure(EapLinkSettings? settings, int retentionDays)
    {
        var next = (settings ?? new EapLinkSettings()).Clone();
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
            if (_settings is not null && Same(_settings, next))
                return;
            StopLocked();
            _settings = next;
            lock (_seqGate)
            {
                _lastEventSeq = null;
                _lastKnownSeq = null;
            }
            if (!next.Enabled)
            {
                Update(_ => EapLinkStatus.Disabled);
                return;
            }
            if (next.IdProblem() is { } problem)
            {
                Update(_ => EapLinkStatus.Disabled with { State = EapLinkState.IdsMissing, Detail = problem });
                return;
            }
            if (string.IsNullOrWhiteSpace(next.RemoteHost) || next.RemotePort is < 1 or > 65535)
            {
                Update(_ => EapLinkStatus.Disabled with { State = EapLinkState.IdsMissing, Detail = "the EAP address is not set" });
                return;
            }
            Start(next);
        }
    }

    private static bool Same(EapLinkSettings a, EapLinkSettings b) =>
        a.Enabled == b.Enabled &&
        string.Equals(a.RemoteHost?.Trim(), b.RemoteHost?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        a.RemotePort == b.RemotePort && a.DeviceId == b.DeviceId &&
        a.SilenceTimeoutSeconds.Equals(b.SilenceTimeoutSeconds) &&
        a.StatusSvids.AsSpan().SequenceEqual(b.StatusSvids) &&
        a.ForwardCeid == b.ForwardCeid && a.LinkCeid == b.LinkCeid;

    /// <summary>Caller holds <c>_gate</c>.</summary>
    private void Start(EapLinkSettings s)
    {
        string endpoint = $"{s.RemoteHost.Trim()}:{s.RemotePort}";
        var host = new GemHost(
            new SecsGemOptions
            {
                IsActive = true,
                IpAddress = s.RemoteHost.Trim(),
                Port = s.RemotePort,
                DeviceId = (ushort)s.DeviceId,
                // Fixed: the EAP spec states them, and nothing on site has asked for others.
                T3 = T3Milliseconds,
                T5 = T5Milliseconds,
                T6 = 10_000,
                T7 = 10_000,
                T8 = 10_000,
            },
            new GemOptions
            {
                ModelName = "OESLM",
                SoftwareRevision = "1.0",
                EstablishRetryIntervalMs = 10_000,
                // Never push the OES's clock into the EAP: it is not ours to set.
                TimeSyncIntervalMs = 0,
            });
        host.Log += line => OnHostLog(endpoint, line);
        host.EventReportReceived += m => OnEventReport(s, m);
        _host = host;

        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;
        Interlocked.Exchange(ref _lastGoodTicks, DateTime.Now.Ticks);
        _silenceArmed = false;
        Update(_ => EapLinkStatus.Disabled with { State = EapLinkState.Connecting, Endpoint = endpoint });
        Trace($"[EAP] connecting to {endpoint} as host (device {s.DeviceId}); ids: S1-S7 = " +
              $"{string.Join(",", s.StatusSvids)}, forward CEID {s.ForwardCeid}, link CEID {s.LinkCeid}");
        host.Start();
        host.Enable();
        _runTask = Task.Run(() => RunAsync(s, host, endpoint, token));
    }

    /// <summary>Caller holds <c>_gate</c>.</summary>
    private void StopLocked()
    {
        if (_runCts is null) return;
        _runCts.Cancel();
        try { _runTask?.Wait(TimeSpan.FromSeconds(3)); } catch { /* cancellation surfaces here */ }
        try { _host?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { /* best effort */ }
        _runCts.Dispose();
        _runCts = null;
        _runTask = null;
        _host = null;
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

    // ---- the loop -----------------------------------------------------------------

    private async Task RunAsync(EapLinkSettings s, GemHost host, string endpoint, CancellationToken token)
    {
        var timeout = Timeout(s);
        var step = TimeSpan.FromMilliseconds(Math.Clamp(timeout.TotalMilliseconds / 5, 20, 1000));
        bool setUp = false;
        DateTime nextPoll = DateTime.MinValue;
        while (await DelayAsync(step, token))
        {
            bool communicating = host.CommunicationState == CommunicationState.Communicating;
            if (!communicating && setUp)
            {
                setUp = false;
                OnLost(endpoint, "communication with the EAP ended");
            }
            if (communicating && !setUp)
            {
                setUp = await SetUpAsync(s, host, endpoint);
                nextPoll = DateTime.Now + PollInterval;
            }
            else if (setUp && (DateTime.Now >= nextPoll || _pollNow))
            {
                _pollNow = false;
                await PollAsync(s, host);
                nextPoll = DateTime.Now + PollInterval;
            }
            CheckSilence(timeout);
        }
    }

    /// <summary>Online, enable the two CEIDs, first read. False when the EAP refused a step, so
    /// the loop tries again on its next pass rather than polling a half-set-up link.</summary>
    private async Task<bool> SetUpAsync(EapLinkSettings s, GemHost host, string endpoint)
    {
        await host.GoOnlineAsync();
        await host.AreYouThereAsync();   // S1F2 MDLN/SOFTREV: logged to the trace, not compared
        if (!await host.EnableEventReportsAsync(true, new[] { s.ForwardCeid, s.LinkCeid }))
        {
            Update(st => st with { Detail = $"the EAP refused S2F37 for CEID {s.ForwardCeid}, {s.LinkCeid}" });
            LogThrottled("enable", LogSeverity.Warning, "EapLinkSetupFailed",
                $"The EAP at {endpoint} refused to enable CEID {s.ForwardCeid} and {s.LinkCeid} (S2F37); " +
                "check the ids on the EAP tab against the EAP's", related: $"Endpoint={endpoint}");
            return false;
        }
        OnConnected(endpoint);
        await PollAsync(s, host);
        return true;
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken token)
    {
        try { await Task.Delay(delay, token).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) { return false; }
    }

    // ---- connection bookkeeping ----------------------------------------------------

    // The transport's last error line, so a failed attempt can say why.
    private volatile string _lastHostError = "";

    /// <summary>
    /// Every line from <c>GemHost</c> and its transport goes to the trace; two of them also mean
    /// something here. Secs4Net reports a failed connect only as an <c>[ERR]</c> line followed by
    /// arming T5 — no connection state says it — so that pair is what counts an attempt.
    /// </summary>
    private void OnHostLog(string endpoint, string line)
    {
        Trace(line);
        if (line.Contains("[ERR]", StringComparison.Ordinal))
            _lastHostError = line[(line.IndexOf("[ERR]", StringComparison.Ordinal) + 5)..].Trim();
        else if (line.Contains("Start T5 Timer", StringComparison.Ordinal) && _status.State != EapLinkState.Connected)
            OnConnectFailed(endpoint, _lastHostError);
    }

    private void OnConnectFailed(string endpoint, string reason)
    {
        var now = DateTime.Now;
        _failedAttempts++;
        _outageSince ??= now;
        string why = reason.Length > 0 ? reason : "no answer";
        Update(st => st with { State = EapLinkState.Connecting, ConnectedSince = null, Detail = $"cannot reach the EAP ({why}) — retrying" });
        if (_failedAttempts == 1)
        {
            _lastFailureLogged = now;
            _log?.LogSystemEvent(LogSeverity.Warning, "EapLinkConnectFailed",
                $"Could not connect to the EAP at {endpoint} ({why}); retrying, summarised here every " +
                $"{FailureSummaryInterval.TotalMinutes:0} min until it succeeds",
                related: $"Endpoint={endpoint}");
        }
        else if (now - _lastFailureLogged >= FailureSummaryInterval)
        {
            _lastFailureLogged = now;
            _log?.LogSystemEvent(LogSeverity.Warning, "EapLinkConnectFailed",
                $"Still cannot connect to the EAP at {endpoint}: {_failedAttempts} attempts since {_outageSince:HH:mm:ss} (last: {why})",
                related: $"Endpoint={endpoint},Attempts={_failedAttempts}");
        }
    }

    private void OnConnected(string endpoint)
    {
        var now = DateTime.Now;
        string after = _outageSince is { } since
            ? $" after {(now - since).TotalMinutes:0.#} min, {_failedAttempts} failed attempt(s)"
            : "";
        _log?.LogSystemEvent(LogSeverity.Information, "EapLinkConnected",
            $"Connected to the EAP at {endpoint}{after}; events enabled", related: $"Endpoint={endpoint}");
        _failedAttempts = 0;
        _outageSince = null;
        Update(st => st with { State = EapLinkState.Connected, Endpoint = endpoint, ConnectedSince = now, Detail = "" });
    }

    private void OnLost(string endpoint, string reason)
    {
        Trace($"[EAP] connection lost: {reason}");
        _log?.LogSystemEvent(LogSeverity.Warning, "EapLinkLost",
            $"The connection to the EAP at {endpoint} was lost ({reason}); reconnecting. The process " +
            "window is kept unless nothing is heard for the silence timeout",
            related: $"Endpoint={endpoint}");
        _outageSince = DateTime.Now;
        _failedAttempts = 0;
        Update(st => st with { State = EapLinkState.Connecting, ConnectedSince = null, Detail = $"connection lost: {reason}" });
    }

    // ---- reading S1–S7 -------------------------------------------------------------

    private async Task PollAsync(EapLinkSettings s, GemHost host)
    {
        var result = await host.QueryStatusAsync(s.StatusSvids);
        if (!result.Succeeded)
        {
            LogThrottled("poll", LogSeverity.Warning, "EapStatusQueryFailed",
                $"Reading S1–S7 from the EAP failed: {result.Error}");
            return;
        }
        if (ParseReading(result.Values!) is not { } reading)
        {
            LogRejected($"S1F4 for S1–S7 is not U1, U4, A, U1, U4, A, U4 ({Describe(result.Values!)})");
            return;
        }
        Heard();
        Update(st => st with { LastReading = reading });
        CheckSequence(reading.Sequence, fromEvent: false);

        var report = reading.ToolLink == 0
            ? new ToolContextReport(ToolContextReportState.Unknown, 0, "the EAP reports no SECS link to the tool")
            : reading.WindowState switch
            {
                2 => new ToolContextReport(ToolContextReportState.Unknown, 0, "the EAP reports PM3's state unknown"),
                1 => new ToolContextReport(ToolContextReportState.InWindow, reading.WindowCeid,
                    $"EAP S1F3: PM3 in the window opened by CEID {reading.WindowCeid}" +
                    (reading.WindowStart.Length > 0 ? $" at {reading.WindowStart}" : "")),
                _ => new ToolContextReport(ToolContextReportState.Outside, 0, "EAP S1F3: PM3 outside any process window"),
            };
        if (_contextSink(report) == ToolEventAck.Invalid)
            LogRejected($"S2 = {reading.WindowCeid} is an End event in the CEID table");
    }

    /// <summary>S1–S7 in their fixed formats, or null. Strings are accepted empty (no window,
    /// nothing received yet); numbers are read the tolerant way <c>TOOL_EVENT</c> reads a CEID.</summary>
    public static EapStatusReading? ParseReading(IReadOnlyList<Item> v)
    {
        if (v.Count != 7) return null;
        uint? state = SecsBridge.ParseCeid(v[0]), winCeid = SecsBridge.ParseCeid(v[1]),
              link = SecsBridge.ParseCeid(v[3]), lastCeid = SecsBridge.ParseCeid(v[4]), seq = SecsBridge.ParseCeid(v[6]);
        if (state is not (0 or 1 or 2) || winCeid is null || link is not (0 or 1) || lastCeid is null || seq is null)
            return null;
        if (Text(v[2]) is not { } winStart || Text(v[5]) is not { } lastTime) return null;
        return new EapStatusReading((byte)state.Value, winCeid.Value, winStart, (byte)link.Value,
            lastCeid.Value, lastTime, seq.Value, DateTime.Now);
    }

    private static string? Text(Item i)
    {
        if (i.Format is not (SecsFormat.ASCII or SecsFormat.JIS8)) return null;
        var t = i.GetString().Trim();
        return t.Length == 0 || SecsBridge.ParseToolEventTime(i) is not null ? t : null;
    }

    // ---- events ---------------------------------------------------------------------

    /// <summary>One S6F11 (already acknowledged by the library). Receive thread.</summary>
    private void OnEventReport(EapLinkSettings s, SecsMessage message)
    {
        // L,3 { DATAID, CEID, L { L,2 { RPTID, L values } } }
        var body = message.SecsItem;
        if (body is not { Format: SecsFormat.List, Count: 3 } || SecsBridge.ParseCeid(body[1]) is not { } ceid)
        {
            LogRejected("S6F11 is not L,3 { DATAID, CEID, reports }");
            return;
        }
        if (ceid != s.ForwardCeid && ceid != s.LinkCeid) return;   // not ours: the EAP may report more
        var reports = body[2];
        if (reports.Format != SecsFormat.List || reports.Count != 1 ||
            reports[0] is not { Format: SecsFormat.List, Count: 2 } report || report[1].Format != SecsFormat.List)
        {
            LogRejected($"CEID {ceid} should carry exactly one report (is it enabled, and its report linked?)");
            return;
        }
        var values = report[1];
        if (ceid == s.LinkCeid)
        {
            uint? link = values.Count == 1 ? SecsBridge.ParseCeid(values[0]) : null;
            if (link is not (0 or 1))
            {
                LogRejected($"tool-link CEID {ceid} should carry one U1 of 0 or 1");
                return;
            }
            Heard();
            Update(st => st with { LastEvent = $"tool link {(link == 1 ? "up" : "DOWN")} at {DateTime.Now:HH:mm:ss}" });
            if (link == 0)
                _contextSink(new ToolContextReport(ToolContextReportState.Unknown, 0, "the EAP reports no SECS link to the tool"));
            else
                _pollNow = true;   // what the window is now, the next S1F3 says
            return;
        }

        if (values.Count != 3 || SecsBridge.ParseCeid(values[0]) is not { } toolCeid ||
            SecsBridge.ParseToolEventTime(values[1]) is not { } time || SecsBridge.ParseCeid(values[2]) is not { } seq)
        {
            LogRejected($"forwarded event should carry U4 tool CEID, A[16] time, U4 sequence; got {Describe(values)}");
            return;
        }
        Heard();
        bool duplicate;
        lock (_seqGate)
        {
            duplicate = _lastEventSeq == seq;
            if (!duplicate)
            {
                CheckSequenceLocked(seq, fromEvent: true);
                _lastEventSeq = seq;
            }
        }
        if (duplicate)
        {
            Trace($"[EAP] SEQ {seq} already handled — a resend; not applied again");
            return;
        }
        var ack = _eventSink(toolCeid, time);
        if (ack == ToolEventAck.Ignored) LogIgnoredOnce(toolCeid);
        Update(st => st with { LastEvent = $"tool CEID {toolCeid} at {time:HH:mm:ss}, SEQ {seq}" });
    }

    private static string Describe(Item list)
    {
        var parts = new List<string>();
        for (int i = 0; i < list.Count; i++) parts.Add(list[i].Format.ToString());
        return string.Join("/", parts);
    }

    private static string Describe(IReadOnlyList<Item> list)
    {
        var parts = new List<string>();
        foreach (var i in list) parts.Add(i.Format.ToString());
        return string.Join("/", parts);
    }

    // ---- sequence ---------------------------------------------------------------------

    private void CheckSequence(uint seq, bool fromEvent)
    {
        lock (_seqGate) CheckSequenceLocked(seq, fromEvent);
    }

    /// <summary>
    /// Gap and restart detection against the highest number heard. An event is one past the
    /// last; S7 repeats the last event sent, so anything above what was heard is that many lost.
    /// Smaller means the EAP started counting again. Caller holds <c>_seqGate</c>.
    /// </summary>
    private void CheckSequenceLocked(uint seq, bool fromEvent)
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
                $"{missing} event(s) from the EAP never arrived (sequence {last} then {seq}); the next " +
                "S1F3 corrects the process window, the events themselves are not recovered",
                value: missing.ToString(CultureInfo.InvariantCulture),
                related: $"From={last},To={seq}");
        }
        if (seq != last) _lastKnownSeq = seq;
    }

    // ---- silence ----------------------------------------------------------------------

    private TimeSpan Timeout(EapLinkSettings s) =>
        SilenceTimeoutOverride ??
        TimeSpan.FromSeconds(s.SilenceTimeoutSeconds > 0 ? s.SilenceTimeoutSeconds : EapLinkSettings.DefaultSilenceTimeoutSeconds);

    private void Heard()
    {
        Interlocked.Exchange(ref _lastGoodTicks, DateTime.Now.Ticks);
        _silenceArmed = true;
        if (_status.Silent) Update(st => st with { Silent = false });
    }

    private void CheckSilence(TimeSpan timeout)
    {
        if (!_silenceArmed) return;
        var since = new DateTime(Interlocked.Read(ref _lastGoodTicks));
        if (DateTime.Now - since < timeout) return;
        _silenceArmed = false;
        string reason = $"nothing from the EAP for {timeout.TotalSeconds:0} s";
        Trace($"[EAP] {reason} — process window set to unknown");
        _contextSink(new ToolContextReport(ToolContextReportState.Unknown, 0, reason));
        Update(st => st with { Silent = true });
    }

    // ---- logging ----------------------------------------------------------------------

    private void LogRejected(string why)
    {
        Trace($"[EAP] rejected: {why}");
        LogThrottled("reject", LogSeverity.Warning, "EapReportRejected",
            $"Data from the EAP was not used ({why}); further rejections are logged at most once an " +
            "hour, the EAP traffic log has every one");
    }

    private void LogThrottled(string kind, LogSeverity severity, string name, string description, string related = "")
    {
        var now = DateTime.Now;
        lock (_throttled)
        {
            if (_throttled.TryGetValue(kind, out var last) && now - last < TimeSpan.FromHours(1)) return;
            _throttled[kind] = now;
        }
        _log?.LogSystemEvent(severity, name, description, related: related);
    }

    private void LogIgnoredOnce(uint ceid)
    {
        lock (_ignoredLogged)
            if (!_ignoredLogged.Add(ceid)) return;
        _log?.LogSystemEvent(LogSeverity.Information, "ToolEventIgnored",
            $"Main tool event CEID {ceid} is not in the tool event table — acknowledged, nothing done " +
            "(logged once per CEID; the EAP traffic log has every occurrence)",
            related: $"CEID={ceid}");
    }

    private void Trace(string line) => _trace?.Write(line);

    /// <summary>Read-modify-write of the status: the receive thread and the loop both change it.</summary>
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
}
