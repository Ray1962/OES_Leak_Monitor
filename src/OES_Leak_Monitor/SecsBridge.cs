using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Aqusen.Secs;
using Secs4Net;

namespace OES_Leak_Monitor;

/// <summary>Run state of the SECS interface, as the tab shows it.</summary>
public enum SecsRunState
{
    /// <summary>Switched off in settings — no port is opened.</summary>
    Disabled,
    /// <summary>Listening (or connecting), nothing attached yet.</summary>
    Listening,
    /// <summary>HSMS link established and selected, GEM handshake not done.</summary>
    Selected,
    /// <summary>S1F13/S1F14 complete — the host and this equipment are talking.</summary>
    Communicating,
    /// <summary>Could not start. <see cref="SecsBridge.LastError"/> says why.</summary>
    Failed,
}

/// <summary>Equipment faults this app reports as alarms. The numbers are the <c>nnn</c>
/// part of the ALID (specification §5.1, reserved range).</summary>
public enum SecsFault
{
    /// <summary>The spectrometer connection dropped after having been up.</summary>
    ConnectionLost = 12,
    /// <summary>The device reported an acquisition error.</summary>
    AcquisitionError = 13,
    /// <summary>A data file could not be written.</summary>
    DataWriteFailure = 14,
}

/// <summary>
/// The SECS/GEM equipment side of this app: it owns the <see cref="EquipmentSimulator"/>,
/// answers host queries out of the latest <see cref="LeakMonitorSnapshot"/>, and pushes the
/// alarms and events the host subscribes to.
///
/// <para><b>It reads, it does not compute.</b> Every status variable is a binding the library
/// invokes on the requesting thread — once per S1F3, once per trace sample, once per value in
/// an event report, possibly several at a time. So the last snapshot is kept as a single
/// volatile reference to an immutable object and the bindings only read fields off it. Nothing
/// here takes a lock, and nothing here re-derives a measurement: if a number is not already in
/// the snapshot it does not belong in this class.</para>
///
/// <para>Design and field semantics: <c>docs/secs-integration.md</c>, and
/// <c>docs/Satellite_SECS_Specification_v2.md</c> §1.4 (ss=27), §5, §6.</para>
/// </summary>
public sealed class SecsBridge : IDisposable
{
    // CEID nnn, specification §6.1. Only the four this app raises.
    private const int CeidAlarmAcknowledged = 502;
    private const int CeidRatioConfigReloaded = 507;
    private const int CeidAcquisitionStarted = 508;
    private const int CeidAcquisitionStopped = 509;

    // ALID nnn, specification §5.1.
    private const int AlidLeakWarning = 1;
    private const int AlidLeakAlarm = 2;

    private readonly SystemLogger? _log;
    private readonly string _configDirectory;
    private readonly string _logDirectory;
    private readonly Func<AcquisitionInfo> _acquisition;

    private readonly object _sendGate = new();
    private Task _sendTail = Task.CompletedTask;      // serialises sends, preserving set/clear order

    private SecsSettings _settings = new();
    private EquipmentSimulator? _equipment;
    private Task _stopping = Task.CompletedTask;      // disposal of the instance Stop() let go of
    private SecsLogFile? _logFile;

    // The only shared measurement state. Immutable object, single reference — see the class note.
    private volatile LeakMonitorSnapshot? _snapshot;

    // What each ratio slot measures (slot offsets 16-25), as the engine last published it. Its own
    // reference rather than a snapshot field for the reason _toolStatus is: it is set when the
    // ratio set is built, before any frame, and must answer while none are flowing.
    private volatile RatioSlotTable _slots = RatioSlotTable.Empty;

    // Frame rate, measured off the snapshot stream (VID 026). Written on the acquisition
    // thread, read by bindings; a double torn across threads would at worst report one
    // nonsense rate for one query, which is not worth a lock on the hot path.
    private volatile float _frameRate;
    private DateTime _lastFrameTs = DateTime.MinValue;

    private readonly HashSet<uint> _activeFaults = new();
    private bool _leakWarningSet, _leakAlarmSet;

    public SecsBridge(
        SystemLogger? systemLogger,
        string configDirectory,
        string logDirectory,
        Func<AcquisitionInfo> acquisition)
    {
        _log = systemLogger;
        _configDirectory = configDirectory;
        _logDirectory = logDirectory;
        _acquisition = acquisition;
    }

    /// <summary>Live acquisition parameters, for VID 024 / 025.</summary>
    public readonly record struct AcquisitionInfo(float IntegrationTimeMs, uint AverageCount);

    // ---- observable state --------------------------------------------------

    /// <summary>Raised whenever <see cref="State"/>, <see cref="StatusText"/> or
    /// <see cref="LastError"/> changes. Not on the UI thread.</summary>
    public event EventHandler? StateChanged;

    /// <summary>One line of SECS traffic. Already written to the log file; this is for the tab.</summary>
    public event EventHandler<string>? Traffic;

    public SecsRunState State { get; private set; } = SecsRunState.Disabled;

    /// <summary>One line describing where the interface is, for the tab header.</summary>
    public string StatusText { get; private set; } = "SECS disabled";

    /// <summary>Why the interface could not start, or "".</summary>
    public string LastError { get; private set; } = "";

    /// <summary>The site-editable profile. Printed to the log so field staff can find it.</summary>
    public string ProfileTemplatePath { get; private set; } = "";

    /// <summary>The chamber-stamped profile actually loaded. Derived; rewritten on every start.</summary>
    public string EffectiveProfilePath { get; private set; } = "";

    /// <summary>Where the traffic log is being written, or "".</summary>
    public string LogFilePath => _logFile?.CurrentPath ?? "";

    /// <summary>True while an equipment instance exists (i.e. a port is held).</summary>
    public bool IsRunning => _equipment is not null;

    // ---- lifecycle ---------------------------------------------------------

    /// <summary>
    /// Applies settings: stops any running interface and starts a new one if enabled. Called at
    /// start-up and whenever the SECS tab saves — the interface is small enough that rebuilding
    /// it is simpler, and more obviously correct, than patching a live one.
    /// </summary>
    public void Configure(SecsSettings settings)
    {
        _settings = settings ?? new SecsSettings();
        Stop();
        if (_settings.Enabled)
        {
            // The new instance binds the same port, so the old listener must be closed first.
            // Starting straight after Stop() raced its disposal, and with a host connected the
            // race was usually lost: WSAEADDRINUSE, a red tab, and a second Save that "fixed" it.
            WaitForPreviousStop();
            Start();
        }
        else
        {
            SetState(SecsRunState.Disabled, "SECS disabled", "");
        }
    }

    private void Start()
    {
        try
        {
            var profileFolder = Path.Combine(_configDirectory, SecsProfileTemplate.FolderName);
            ProfileTemplatePath = Path.Combine(profileFolder, _settings.ProfileFileName);
            EffectiveProfilePath = Path.Combine(
                profileFolder, SecsProfileTemplate.EffectiveFolderName, _settings.ProfileFileName);

            _logFile = new SecsLogFile(_logDirectory, _settings.LogRetentionDays);
            _logFile.WriteFailed += m => _log?.LogSystemEvent(LogSeverity.Warning, "SecsLogWriteFailed",
                "The SECS traffic log could not be written; the interface keeps running.", value: m);

            bool created = SecsProfileTemplate.EnsureExists(ProfileTemplatePath);
            Emit(created
                ? $"[CFG] wrote the default profile to {ProfileTemplatePath}"
                : $"[CFG] profile template {ProfileTemplatePath}");

            var stamped = SecsChamberCoding.ApplyChamber(
                File.ReadAllText(ProfileTemplatePath), _settings.ChamberCode);
            Directory.CreateDirectory(Path.GetDirectoryName(EffectiveProfilePath)!);
            File.WriteAllText(EffectiveProfilePath, stamped);
            Emit($"[CFG] chamber {_settings.ChamberCode:00} " +
                 $"({SecsChamberCoding.ChamberName(_settings.ChamberCode)}) -> {EffectiveProfilePath}");

            // A status variable this build serves but the profile never asks for reaches no host
            // at all, and after an upgrade that is the normal case: the site's profile was
            // written by an older build and is deliberately never overwritten. Said once, here,
            // rather than left to be discovered when a host asks for a VID and gets nothing.
            var unlisted = BindsNotInProfile(stamped);
            if (unlisted.Count > 0)
            {
                Emit($"[CFG] {unlisted.Count} status variable(s) this build can serve are not in " +
                     $"the profile: {string.Join(", ", unlisted)}");
                _log?.LogSystemEvent(LogSeverity.Information, "SecsProfileMissingBinds",
                    "The SECS profile does not list every status variable this build can serve. " +
                    "Add the rows (or delete the profile to regenerate it) if the host needs them.",
                    value: string.Join(", ", unlisted), related: ProfileTemplatePath);
            }

            var profile = JsonDeviceProfile.Load(EffectiveProfilePath);

            var equipment = new EquipmentSimulator(
                new SecsGemOptions
                {
                    IsActive = _settings.IsActive,
                    IpAddress = _settings.IpAddress,
                    Port = _settings.Port,
                    DeviceId = (ushort)_settings.DeviceId,
                    // Secs4Net counts these in milliseconds; the specification quotes seconds.
                    T3 = _settings.T3 * 1000,
                    T5 = _settings.T5 * 1000,
                    T6 = _settings.T6 * 1000,
                    T7 = _settings.T7 * 1000,
                    T8 = _settings.T8 * 1000,
                },
                new GemOptions
                {
                    ModelName = _settings.ModelName,
                    SoftwareRevision = _settings.SoftwareRevision,
                    // Satellite writes ALID as ASCII digits throughout (specification §4, §5.3).
                    AlarmIdFormat = AlarmIdFormat.Ascii,
                },
                profile,
                CreateBindings());

            equipment.Log += Emit;
            // The library has already checked the command and its parameter names against the
            // profile; this decides the rest.
            equipment.RemoteCommandHandler = HandleRemoteCommand;
            equipment.CommunicationStateChanged += _ => RefreshState();
            equipment.ControlStateChanged += _ => RefreshState();
            equipment.ConnectionChanged += _ => RefreshState();

            _equipment = equipment;
            equipment.Start();

            SetState(SecsRunState.Listening,
                $"{(_settings.IsActive ? "connecting to" : "listening on")} " +
                $"{_settings.IpAddress}:{_settings.Port}, device id {_settings.DeviceId}", "");
            _log?.LogSystemEvent(LogSeverity.Information, "SecsStarted",
                "SECS/GEM equipment interface started.",
                related: $"Chamber={_settings.ChamberCode:00},Endpoint={_settings.IpAddress}:{_settings.Port}," +
                         $"DeviceId={_settings.DeviceId}",
                value: EffectiveProfilePath);
        }
        catch (Exception ex)
        {
            Stop();
            SetState(SecsRunState.Failed, "SECS failed to start", ex.Message);
            _log?.LogError("Secs_Start_Failed", ex, ProfileTemplatePath);
        }
    }

    /// <summary>Tears the interface down. Safe to call when nothing is running.</summary>
    public void Stop()
    {
        var equipment = _equipment;
        _equipment = null;
        if (equipment is not null)
        {
            // Disposal talks to the socket, so it runs off the calling thread; only a restart
            // (Configure) waits for it, because only a restart needs the port back.
            _stopping = Task.Run(async () =>
            {
                try { await equipment.DisposeAsync(); }
                catch (Exception ex) { _log?.LogError("Secs_Stop_Failed", ex, ""); }
            });
            _log?.LogSystemEvent(LogSeverity.Information, "SecsStopped",
                "SECS/GEM equipment interface stopped.");
        }
        _logFile?.Dispose();
        _logFile = null;
        lock (_activeFaults) { _activeFaults.Clear(); }
        _leakWarningSet = _leakAlarmSet = false;
    }

    /// <summary>
    /// Blocks until the instance the last <see cref="Stop"/> released has closed its sockets.
    /// Called on the UI thread from Save &amp; apply; safe there because nothing on the disposal
    /// path marshals back synchronously (state and traffic reach the tab by BeginInvoke).
    /// Bounded, so a disposal that hangs costs a red tab with the reason, not a frozen window.
    /// </summary>
    private void WaitForPreviousStop()
    {
        var stopping = _stopping;
        if (stopping.IsCompleted)
        {
            return;
        }
        if (!stopping.Wait(StopTimeout))
        {
            _log?.LogSystemEvent(LogSeverity.Warning, "SecsStopSlow",
                $"The previous SECS connection did not close within {StopTimeout.TotalSeconds:0} s; " +
                "starting anyway, which fails if the port is still held.");
        }
    }

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    // ---- bringing an old profile up to date ----------------------------------

    /// <summary>The site-editable profile the settings name — the cc = 00 one, not the stamped
    /// copy — whether or not the interface is running.</summary>
    public string SiteProfilePath =>
        Path.Combine(_configDirectory, SecsProfileTemplate.FolderName, _settings.ProfileFileName);

    /// <summary>
    /// What <see cref="AddMissingProfileRows"/> would do now, without writing anything: the rows
    /// it would add and the ones it would refuse. Null when there is no profile yet (the first
    /// start writes the whole template). Throws if the file cannot be read or parsed.
    /// </summary>
    public SecsProfileRowsResult? PreviewMissingProfileRows()
    {
        var path = SiteProfilePath;
        return File.Exists(path)
            ? SecsProfileRows.AddMissing(ReadProfile(path).Text, SecsProfileTemplate.Json)
            : null;
    }

    /// <summary>
    /// Appends the missing rows to the site profile (<see cref="SecsProfileRows.AddMissing"/>),
    /// having first copied it to <c>{profile}.bak-{yyyyMMddHHmmss}</c>. Takes effect the next
    /// time the interface starts — the running one keeps the profile it loaded. Nothing is
    /// written when nothing would be added. Logged as <c>SecsProfileRowsAdded</c>.
    /// </summary>
    public SecsProfileRowsResult AddMissingProfileRows()
    {
        var path = SiteProfilePath;
        var (before, encoding) = ReadProfile(path);
        var result = SecsProfileRows.AddMissing(before, SecsProfileTemplate.Json);
        if (result.Added.Count > 0)
        {
            File.Copy(path, BackupPath(path), overwrite: false);
            var temp = path + ".tmp";
            File.WriteAllText(temp, result.Json, encoding);
            File.Move(temp, path, overwrite: true);
        }

        var counts = SecsProfileRows.Describe(result.Added);
        var refused = string.Join("; ", result.Rejected.Select(r => $"{r.Row}: {r.Reason}"));
        Emit($"[CFG] added {result.Added.Count} missing row(s) to {path}" +
             (result.Rejected.Count > 0 ? $", refused {result.Rejected.Count}: {refused}" : ""));
        _log?.LogSystemEvent(result.Rejected.Count > 0 ? LogSeverity.Warning : LogSeverity.Information,
            "SecsProfileRowsAdded",
            $"Added {result.Added.Count} row(s) the SECS profile was missing ({(counts.Length > 0 ? counts : "none")}); " +
            $"refused {result.Rejected.Count}{(refused.Length > 0 ? " — " + refused : "")}. " +
            "They take effect the next time the SECS interface starts.",
            value: counts, related: path);
        return result;
    }

    /// <summary>The profile's text, and the encoding to write it back in — with a byte-order
    /// mark if it had one, so the only bytes that change are the rows added.</summary>
    private static (string Text, System.Text.Encoding Encoding) ReadProfile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: bom);
        return (encoding.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), encoding);
    }

    /// <summary><c>{profile}.bak-{yyyyMMddHHmmss}</c>, with <c>_N</c> appended rather than
    /// replacing an earlier backup taken within the same second.</summary>
    private static string BackupPath(string path)
    {
        var stem = $"{path}.bak-{DateTime.Now:yyyyMMddHHmmss}";
        var candidate = stem;
        for (int n = 1; File.Exists(candidate); n++) candidate = $"{stem}_{n}";
        return candidate;
    }

    // ---- inbound: TOOL_EVENT ------------------------------------------------

    /// <summary>
    /// Where a forwarded main-tool event goes — the leak monitor's
    /// <c>ReceiveToolEvent</c>, wired by the host. Kept as a delegate rather than an engine
    /// reference so this class stays what the class note says it is: it reads the snapshot and
    /// hands things on, it holds no measurement state. Null answers every event "cannot perform
    /// now", which is the truth.
    /// </summary>
    public Func<uint, DateTime, ToolEventAck>? ToolEventSink { get; set; }

    /// <summary>
    /// Raised when the tool-event part of the snapshot changes (window, class, last CEID), so
    /// the tab can redraw its status line without polling. Not on the UI thread.
    /// </summary>
    public event EventHandler? ToolContextChanged;

    // Where the tool events have put the engine. Its own reference rather than a snapshot field:
    // the table is applied and the host's events arrive whether or not frames are flowing, and
    // 030-032 must answer for that moment, not for the last frame (which may not exist yet).
    private volatile ToolEventStatus _toolStatus = ToolEventStatus.Disabled;

    /// <summary>The engine's tool-event status as last published, for the tab to word.</summary>
    public ToolEventStatus ToolStatus => _toolStatus;

    /// <summary>The engine's <see cref="LeakMonitorEngine.ToolStatusChanged"/>. Any thread; it
    /// stores the value and raises <see cref="ToolContextChanged"/>, nothing else.</summary>
    public void OnToolStatus(ToolEventStatus status)
    {
        _toolStatus = status;
        ToolContextChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>One line for the SECS tab: which window the leak monitor is judging in, and the
    /// last event the EAP forwarded.</summary>
    public string ToolEventStatusText
    {
        get
        {
            var s = _toolStatus;
            if (s.State == ToolEventContext.Disabled)
                return "Tool events: not in use";
            string window = s.State switch
            {
                ToolEventContext.InWindow => $"in window {s.WindowClass}",
                ToolEventContext.OutsideWindow => "outside any window",
                _ => "window unknown (judging as without tool events)",
            };
            string last = s.LastTime is { } t
                ? $"last CEID {s.LastCeid} at {t:HH:mm:ss}"
                : "no event received yet";
            return $"Tool events: {window}; {last}";
        }
    }

    /// <summary>
    /// S2F41 after the library's own checks (ONLINE/REMOTE, command and parameter names listed
    /// in the profile). Runs on the receive thread while the host waits, so it parses, hands the
    /// event on and answers — nothing else.
    /// </summary>
    private RemoteCommandResult HandleRemoteCommand(RemoteCommandRequest request)
    {
        // The profile is site-editable; a command added there that this build does not implement
        // is not one it should pretend to have carried out.
        if (!string.Equals(request.Command, "TOOL_EVENT", StringComparison.OrdinalIgnoreCase))
            return RemoteCommandResult.Rejected(1);

        var ceidParam = request.Find("CEID");
        uint? ceid = ceidParam is null ? null : ParseCeid(ceidParam.Value);
        if (ceid is null)
        {
            Emit($"[EQP] TOOL_EVENT without a readable CEID -> HCACK=3");
            return ceidParam is null
                ? RemoteCommandResult.Rejected(3)
                : new RemoteCommandResult(3, new[] { new RemoteCommandParameterAck("CEID", 2) });  // CPACK 2: illegal value
        }

        // TIME, when sent, is the tool's own moment for the event, so a forwarding delay in the
        // EAP does not move the window. One that cannot be read refuses the event: stamping it
        // with the arrival time instead would put a window somewhere the tool never said.
        var time = DateTime.Now;
        var timeParam = request.Find("TIME");
        if (timeParam is not null)
        {
            if (ParseToolEventTime(timeParam.Value) is not { } sent)
            {
                Emit($"[EQP] TOOL_EVENT CEID {ceid} with an unreadable TIME -> HCACK=3");
                return new RemoteCommandResult(3, new[] { new RemoteCommandParameterAck("TIME", 2) });  // CPACK 2: illegal value
            }
            time = sent;
        }

        var sink = ToolEventSink;
        var ack = sink?.Invoke(ceid.Value, time) ?? ToolEventAck.Disabled;
        switch (ack)
        {
            case ToolEventAck.Accepted:
                Emit($"[EQP] TOOL_EVENT CEID {ceid} taken");
                return RemoteCommandResult.Accepted;
            case ToolEventAck.Ignored:
                Emit($"[EQP] TOOL_EVENT CEID {ceid} ignored (not in the tool event table)");
                LogIgnoredOnce(ceid.Value);
                return RemoteCommandResult.Accepted;
            default:
                Emit($"[EQP] TOOL_EVENT CEID {ceid} refused: tool events are not in use -> HCACK=2");
                return RemoteCommandResult.Rejected(2);
        }
    }

    // CEIDs already reported as ignored this run. The EAP is invited to forward the tool's events
    // unfiltered, so every slit-valve cycle and load-lock pump-down arrives here; one audit entry
    // per CEID says "this event reaches us and is not mapped" without burying the log it is in.
    // The traffic log still has every one.
    private readonly HashSet<uint> _ignoredLogged = new();

    /// <summary>
    /// The first time a CEID arrives that the table does not map, put it in the system log — the
    /// traffic log is not what anyone reads when asking why a window never opened. Runs on the
    /// receive thread: <see cref="SystemLogger"/> only queues the entry for its writer thread.
    /// </summary>
    private void LogIgnoredOnce(uint ceid)
    {
        lock (_ignoredLogged)
        {
            if (!_ignoredLogged.Add(ceid)) return;
        }
        _log?.LogSystemEvent(LogSeverity.Information, "ToolEventIgnored",
            $"Main tool event CEID {ceid} is not in the tool event table — acknowledged, nothing done " +
            "(logged once per CEID; the SECS traffic log has every occurrence)",
            related: $"CEID={ceid}");
    }

    /// <summary>
    /// A CEID as the EAP may send it: ASCII digits (what <c>GemHost</c> and most hosts put in a
    /// CPVAL), or an unsigned integer item. Null for anything else, or a value that cannot be a
    /// CEID — a guess here would open a window nobody asked for.
    /// </summary>
    public static uint? ParseCeid(Item value)
    {
        try
        {
            switch (value.Format)
            {
                case SecsFormat.ASCII or SecsFormat.JIS8:
                    return uint.TryParse(value.GetString().Trim(), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var n) ? n : null;
                case SecsFormat.U4 when value.Count == 1:
                    return value.FirstValue<uint>();
                case SecsFormat.U8 when value.Count == 1:
                    var u8 = value.FirstValue<ulong>();
                    return u8 <= uint.MaxValue ? (uint)u8 : null;
                case SecsFormat.U2 when value.Count == 1:
                    return value.FirstValue<ushort>();
                case SecsFormat.U1 when value.Count == 1:
                    return value.FirstValue<byte>();
                default:
                    return null;
            }
        }
        catch (Exception)
        {
            return null;   // a malformed item is a parameter error, not a reason to drop the link
        }
    }

    /// <summary>
    /// <c>TOOL_EVENT</c>'s <c>TIME</c>: the SEMI clock form <c>YYYYMMDDhhmmsscc</c> (hundredths
    /// last), or <c>YYYYMMDDhhmmss</c> without them, as ASCII. Read as local time, which is what
    /// the tool and the frames both run on. Null for anything else.
    /// </summary>
    public static DateTime? ParseToolEventTime(Item value)
    {
        try
        {
            if (value.Format is not (SecsFormat.ASCII or SecsFormat.JIS8)) return null;
            var text = value.GetString().Trim();
            string? format = text.Length switch { 16 => "yyyyMMddHHmmssff", 14 => "yyyyMMddHHmmss", _ => null };
            return format is not null &&
                   DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture,
                       DateTimeStyles.AssumeLocal, out var t)
                ? t
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- inputs ------------------------------------------------------------

    /// <summary>
    /// Latest processed frame. Called on the acquisition thread for every frame, so it does
    /// exactly two things: publish the reference, and update the measured frame rate.
    /// </summary>
    public void OnSample(LeakMonitorSnapshot snapshot)
    {
        _snapshot = snapshot;

        var ts = snapshot.Timestamp;
        if (_lastFrameTs != DateTime.MinValue)
        {
            var dt = (ts - _lastFrameTs).TotalSeconds;
            // Ignore a non-advancing or absurd interval (a clock step, or the first frame after
            // a pause) rather than letting it define the rate.
            if (dt > 0.0005 && dt < 60)
            {
                var instant = 1.0 / dt;
                var previous = _frameRate;
                // Light smoothing: the reported rate should describe the run, not the last gap.
                _frameRate = previous <= 0 ? (float)instant : (float)(0.9 * previous + 0.1 * instant);
            }
        }
        _lastFrameTs = ts;
    }

    /// <summary>
    /// Composite leak level changed. Maps onto ALID 001 / 002 (specification §5.1): Warning is
    /// set while the composite sits at Warning, Alarm while it sits at Alarm. Both clear on the
    /// way down, so the host's alarm list never keeps a leak that has been acknowledged.
    /// </summary>
    public void OnLeakLevelChanged(LeakAlarmLevel level)
    {
        if (!AlarmsAllowed)
        {
            return;
        }

        bool warning = level == LeakAlarmLevel.Warning;
        bool alarm = level == LeakAlarmLevel.Alarm;

        if (warning != _leakWarningSet)
        {
            _leakWarningSet = warning;
            SendAlarm(AlidLeakWarning, warning, DescribeLeak("LEAK WARNING", level));
        }
        if (alarm != _leakAlarmSet)
        {
            _leakAlarmSet = alarm;
            SendAlarm(AlidLeakAlarm, alarm, DescribeLeak("LEAK ALARM", level));
        }
    }

    /// <summary>
    /// Raises or clears an equipment fault (ALID 012–014).
    /// <para/>
    /// Unlike the leak alarms these are <b>not</b> suppressed in test mode: a spectrometer that
    /// has dropped off the bus, or a CSV that cannot be written, is a fact about the tool
    /// whatever the frames happen to contain.
    /// </summary>
    public void ReportFault(SecsFault fault, bool set, string detail = "")
    {
        if (!Ready || !_settings.ReportAlarms)
        {
            return;
        }

        uint alid = SecsChamberCoding.EventId(_settings.ChamberCode, (int)fault);
        lock (_activeFaults)
        {
            // De-bounce: a fault that is already reported does not need reporting again, and a
            // clear for something that was never set would show up in the host's log as an event
            // that never happened.
            if (set == _activeFaults.Contains(alid))
            {
                return;
            }
            if (set) { _activeFaults.Add(alid); } else { _activeFaults.Remove(alid); }
        }

        string name = fault switch
        {
            SecsFault.ConnectionLost => "CONNECTION LOST",
            SecsFault.AcquisitionError => "ACQUISITION ERROR",
            _ => "DATA WRITE FAILURE",
        };
        SendAlarm((int)fault, set, Prefix() + "OES " + name + (detail.Length > 0 ? " — " + detail : ""));
    }

    /// <summary>Acquisition started or stopped — CEID 508 / 509.</summary>
    public void OnAcquisitionChanged(bool acquiring) =>
        SendEvent(acquiring ? CeidAcquisitionStarted : CeidAcquisitionStopped,
            acquiring ? "acquisition started" : "acquisition stopped");

    /// <summary>The ratio slot table as the engine has it now — at start-up, before any frame.
    /// Stores it; sends nothing.</summary>
    public void OnSlotTable(RatioSlotTable table) => _slots = table;

    /// <summary>
    /// The engine's <see cref="LeakMonitorEngine.SlotIdentityChanged"/>: a slot now measures
    /// something else, so a host that labelled its history by slot must re-read offsets 16–25
    /// before trusting the next value — CEID 507. Stored before it is sent, so a host reacting
    /// to the event reads the new identity.
    /// </summary>
    public void OnSlotIdentityChanged(RatioSlotTable table)
    {
        _slots = table;
        SendEvent(CeidRatioConfigReloaded,
            $"ratio slots changed ({Math.Min(table.Slots.Count, RatioSlotTable.SlotCount)} of " +
            $"{RatioSlotTable.SlotCount} in use) — re-read slot identity");
    }

    /// <summary>An operator ended a confirmed leak alarm — CEID 502.</summary>
    public void OnAcknowledged(LeakAcknowledgedEventArgs e) =>
        SendEvent(CeidAlarmAcknowledged,
            $"alarm acknowledged by {(e.User.Length > 0 ? e.User : "(unknown)")}, " +
            $"{e.ClearedRatios.Count} ratio(s) cleared, {e.OldLevel} -> {e.NewLevel}");

    // ---- sending -----------------------------------------------------------

    /// <summary>Whether the interface is up far enough to send anything.</summary>
    private bool Ready => _equipment is not null;

    /// <summary>
    /// Whether a data-derived alarm may go out: reporting is on, and either the frames are real
    /// or the operator has deliberately allowed test-mode reporting. VID 016 tells the host the
    /// truth in either case.
    /// </summary>
    private bool AlarmsAllowed =>
        Ready && _settings.ReportAlarms && (_settings.ReportInTestMode || _snapshot?.TestMode != true);

    private bool EventsAllowed =>
        Ready && _settings.ReportEvents && (_settings.ReportInTestMode || _snapshot?.TestMode != true);

    private void SendAlarm(int nnn, bool set, string text)
    {
        var equipment = _equipment;
        if (equipment is null)
        {
            return;
        }
        uint alid = SecsChamberCoding.EventId(_settings.ChamberCode, nnn);
        Enqueue(() => equipment.SendAlarmAsync(alid, text, set), $"S5F1 ALID={alid}");
    }

    private void SendEvent(int nnn, string what)
    {
        var equipment = _equipment;
        if (equipment is null || !EventsAllowed)
        {
            return;
        }
        uint ceid = SecsChamberCoding.EventId(_settings.ChamberCode, nnn);
        Emit($"[EQP] {what} -> CEID {ceid}");
        Enqueue(() => equipment.SendEventAsync(ceid), $"S6F11 CEID={ceid}");
    }

    /// <summary>
    /// Queues one send behind the previous one. Ordering is the point: an alarm's set and its
    /// clear are two messages that mean the opposite thing, and a host that receives them out of
    /// order is left holding an alarm that ended. A failed send (host away, T3 expired) is logged
    /// and does not break the chain.
    /// </summary>
    private void Enqueue(Func<Task> send, string description)
    {
        lock (_sendGate)
        {
            _sendTail = _sendTail.ContinueWith(async _ =>
            {
                try { await send(); }
                catch (Exception ex) { Emit($"[EQP] {description} failed: {ex.Message}"); }
            }, TaskScheduler.Default).Unwrap();
        }
    }

    // ---- status variables --------------------------------------------------

    /// <summary>
    /// Every value the profile can bind to. Registered whether or not the profile asks for it:
    /// a site that removes an SV from its profile has removed it, and a site that adds one back
    /// should not have to restart the app. The library checks the other direction at construction
    /// — a profile naming a binding that does not exist here fails immediately, by name.
    /// Public so a test can read a value exactly as S1F3 would, without a host.
    /// </summary>
    public SvBindings CreateBindings()
    {
        var b = new SvBindings();

        // §1.4(a) 001-005: the quantitative estimate. Reported as 0 when there is none, with
        // 004 = 0 saying so — a host must read 004 before believing 001 (specification §1.4(d)).
        b.Bind("oes.leakRate", () => (float)(Estimate()?.LeakRate ?? 0));
        b.Bind("oes.leakRateSigma", () => (float)(Estimate()?.Sigma ?? 0));
        b.Bind("oes.leakRateConfidence", () => (float)(Estimate()?.Confidence ?? 0));
        b.Bind("oes.leakRateValid", () => Flag(Estimate()?.HasEstimate == true));
        b.Bind("oes.outOfCalibratedRange", () => Flag(Estimate()?.OutOfCalibratedRange == true));

        // 006-007: the two enumerations a host acts on. The declaration order of
        // CalibrationStatus and LeakAlarmLevel *is* the wire encoding — see
        // docs/secs-integration.md §5.1 before inserting a member into either.
        b.Bind("oes.calibrationStatus", () => (uint)(_snapshot?.CalibrationStatus ?? CalibrationStatus.NotCalibrated));
        b.Bind("oes.compositeLevel", () => (uint)(_snapshot?.Overall ?? LeakAlarmLevel.Idle));

        // 008-012: how the composite was arrived at. A host that only reads 007 cannot tell a
        // healthy Normal from one where three of four ratios stood down for low signal.
        b.Bind("oes.enabledRatios", () => CountRatios(s => s != RatioState.Disabled));
        b.Bind("oes.warningRatios", () => CountRatios(s => s == RatioState.Warning));
        b.Bind("oes.alarmRatios", () => CountRatios(s => s == RatioState.Alarm));
        b.Bind("oes.lowSignalRatios", () => CountRatios(s => s == RatioState.LowSignal));
        b.Bind("oes.baselineAvailable", () => Flag(_snapshot?.Ratios.Any(r => r.HasBaseline) == true));

        // 013-016: what the numbers are being judged against.
        b.Bind("oes.goldenRunName", () => _snapshot?.ActiveGoldenRun ?? "");
        b.Bind("oes.calibrationName", () => _snapshot?.ActiveCalibration ?? "");
        b.Bind("oes.acquisitionMismatch", () => Flag(!string.IsNullOrEmpty(_snapshot?.AcquisitionWarning)));
        b.Bind("oes.testMode", () => Flag(_snapshot?.TestMode == true));

        // 017-020: captures in progress. A host polling during one is looking at a baseline
        // that is about to be replaced.
        b.Bind("oes.captureActive", () => Flag(_snapshot?.CaptureActive == true));
        b.Bind("oes.captureProgress", () => (float)((_snapshot?.CaptureProgress01 ?? 0) * 100));
        b.Bind("oes.calCaptureActive", () => Flag(_snapshot?.CalibrationCaptureActive == true));
        b.Bind("oes.calCaptureProgress", () => (float)((_snapshot?.CalibrationCaptureProgress01 ?? 0) * 100));

        // 021-023: instrument health.
        b.Bind("oes.plasmaPresent", () => Flag(_snapshot?.PlasmaPresent == true));
        b.Bind("oes.plasmaGateAvailable", () => Flag(_snapshot?.PlasmaGateAvailable == true));
        b.Bind("oes.dropoutCount", () => (uint)Math.Max(0, _snapshot?.DropoutCount ?? 0));

        // 024-026: the acquisition the numbers were taken with. Read live rather than off the
        // snapshot, because a mid-run Apply changes them without a frame having to arrive.
        b.Bind("oes.integrationTime", () => _acquisition().IntegrationTimeMs);
        b.Bind("oes.averageCount", () => _acquisition().AverageCount);
        b.Bind("oes.frameRate", () => _frameRate);

        // 027-029: which process the plasma step now running is. A chamber that interleaves
        // recipes reports a leak state that means nothing without it — each ratio is scoped to
        // the process it measures, so "Normal" during a step no ratio covers is not the same
        // claim as "Normal" during one they do. 028 is what makes 027 readable: the name is
        // blank for three different reasons and only the state says which.
        b.Bind("oes.processClass", () => _snapshot?.ProcessClass ?? "");
        b.Bind("oes.processClassState",
               () => (uint)(_snapshot?.ProcessClassState ?? ProcessClassState.NotConfigured));
        b.Bind("oes.processStepIndex", () => (uint)Math.Max(0, _snapshot?.ProcessStepIndex ?? 0));

        // 030-032: the process window the main tool's forwarded events put the monitor in, and
        // the last event it received. 030 is what tells a host whether 027 came from the tool or
        // from the fallback — and 032 lets the EAP see its own forwarding arrive.
        b.Bind("oes.toolEventState", () => (uint)_toolStatus.State);
        b.Bind("oes.toolWindowClass", () => _toolStatus.WindowClass);
        b.Bind("oes.lastToolCeid", () => _toolStatus.LastCeid);

        // 101-400: the ratio slots (§1.4(b)). Slot N is row N of Ratio Setup.
        for (int n = 1; n <= RatioSlotTable.SlotCount; n++)
        {
            foreach (var field in SecsRatioSlots.Fields)
            {
                b.Bind(SecsRatioSlots.BindName(n, field), SlotReader(n, field.Offset));
            }
        }

        return b;
    }

    /// <summary>
    /// The value behind one slot offset. 16–25 read the slot table; 01–15 read the last snapshot's
    /// row <em>for the slot's key</em>, never by position — after a reorder the snapshot still in
    /// hand lists the ratios in the old order until the next frame. Nothing to read gives 0 (and
    /// "" for a name): F4 never carries NaN, and a host tells meaningful from not by 05, 18, 19.
    /// </summary>
    private Func<object> SlotReader(int n, int offset) => offset switch
    {
        1 => () => F4(LiveRow(n)?.RawRatio),
        2 => () => F4(LiveRow(n)?.SmoothedRatio),
        3 => () => F4(LiveRow(n)?.PercentOfBaseline),
        4 => () => F4(LiveRow(n)?.SlopePerMinute),
        5 => () => (uint)SlotState(n),
        6 => () => F4(LiveRow(n)?.NumeratorSnr),
        // Absolute intensity still reads its reference for the plasma gate, but the slot names
        // no reference (21 = 0, 24 = ""), so it reports none either. Decided by the slot table's
        // mode, as 17 is — not the row's, which until the next frame may predate a reload.
        7 => () => F4(IsRatioSlot(n) ? LiveRow(n)?.DenominatorSnr : null),
        8 => () => F4(LiveRow(n)?.NumeratorIntensity),
        9 => () => F4(IsRatioSlot(n) ? LiveRow(n)?.DenominatorIntensity : null),
        10 => () => F4(LiveRow(n)?.BaselineMean),
        11 => () => F4(LiveRow(n)?.BaselineSigma),
        12 => () => F4(LiveRow(n)?.WarnThreshold),
        13 => () => F4(LiveRow(n)?.AlarmThreshold),
        14 => () => F4(SlotLeakEstimate(n)?.LeakRate),
        15 => () => Flag(SlotLeakEstimate(n)?.Used == true),
        16 => () => Flag(_slots.Slot(n)?.Enabled == true),
        17 => () => (uint)(_slots.Slot(n)?.MonitorMode ?? MonitorMode.Ratio),
        // Read straight off ValueHasPedestal. Re-deriving it from 17 and 19 is the step
        // specification v2.1 got wrong (secs-ratio-svid-example-zh-TW.md §7).
        18 => () => Flag(_slots.Slot(n)?.ValueHasPedestal == true),
        19 => () => (uint)(_slots.Slot(n)?.ExtractMode ?? LineExtractMode.PeakHeight),
        20 => () => F4(_slots.Slot(n)?.SignalWavelengthNm),
        21 => () => F4(_slots.Slot(n)?.ReferenceWavelengthNm),
        22 => () => _slots.Slot(n)?.DisplayName ?? "",
        23 => () => _slots.Slot(n)?.SignalSpecies ?? "",
        24 => () => _slots.Slot(n)?.ReferenceSpecies ?? "",
        25 => () => _slots.Slot(n)?.Key ?? "",
        _ => throw new ArgumentOutOfRangeException(nameof(offset), offset, "not a slot offset"),
    };

    private bool IsRatioSlot(int n) => _slots.Slot(n)?.MonitorMode == MonitorMode.Ratio;

    /// <summary>The last snapshot's row for slot <paramref name="n"/>'s key, or null for an
    /// empty slot or a key that snapshot does not carry.</summary>
    private RatioSnapshot? LiveRow(int n)
    {
        var key = _slots.Slot(n)?.Key;
        var ratios = _snapshot?.Ratios;
        if (key is null || ratios is null) return null;
        for (int i = 0; i < ratios.Count; i++)
        {
            if (ratios[i].Key == key) return ratios[i];
        }
        return null;
    }

    /// <summary>Offset 05. No row to read — an empty slot, or the first frame after a reload has
    /// not arrived — is Disabled for a slot that is switched off or absent and NoPlasma for one
    /// that is on: "nothing measured yet", which is what a host must not judge.</summary>
    private RatioState SlotState(int n) =>
        LiveRow(n)?.State
        ?? (_slots.Slot(n)?.Enabled == true ? RatioState.NoPlasma : RatioState.Disabled);

    private RatioLeakEstimate? SlotLeakEstimate(int n)
    {
        var key = _slots.Slot(n)?.Key;
        var perRatio = Estimate()?.PerRatio;
        if (key is null || perRatio is null) return null;
        for (int i = 0; i < perRatio.Count; i++)
        {
            if (perRatio[i].Key == key) return perRatio[i];
        }
        return null;
    }

    /// <summary>An F4 value, with "no value" and any non-number sent as 0.</summary>
    private static float F4(double? value) =>
        value is { } v && float.IsFinite((float)v) ? (float)v : 0f;

    /// <summary>
    /// Every bind name <see cref="CreateBindings"/> registers — the global ones written out, the
    /// 250 slot ones generated from <see cref="SecsRatioSlots"/>.
    ///
    /// <para>It exists for one direction the library cannot check. A profile naming a binding
    /// this build does not supply fails at start-up, by name; a build supplying one the profile
    /// does not list is silent — and that is exactly what an upgrade produces, because
    /// <see cref="SecsProfileTemplate.EnsureExists"/> never overwrites a site's profile. The new
    /// status variable then reaches no machine that has ever started the interface, with nothing
    /// anywhere to say so. <c>SecsProfileTests</c> checks each name here is really registered, so
    /// the list cannot claim a binding that does not exist.</para>
    /// </summary>
    public static readonly string[] SuppliedBindNames = new[]
    {
        "oes.leakRate", "oes.leakRateSigma", "oes.leakRateConfidence", "oes.leakRateValid",
        "oes.outOfCalibratedRange", "oes.calibrationStatus", "oes.compositeLevel",
        "oes.enabledRatios", "oes.warningRatios", "oes.alarmRatios", "oes.lowSignalRatios",
        "oes.baselineAvailable", "oes.goldenRunName", "oes.calibrationName",
        "oes.acquisitionMismatch", "oes.testMode", "oes.captureActive", "oes.captureProgress",
        "oes.calCaptureActive", "oes.calCaptureProgress", "oes.plasmaPresent",
        "oes.plasmaGateAvailable", "oes.dropoutCount", "oes.integrationTime", "oes.averageCount",
        "oes.frameRate", "oes.processClass", "oes.processClassState", "oes.processStepIndex",
        "oes.toolEventState", "oes.toolWindowClass", "oes.lastToolCeid",
    }.Concat(SecsRatioSlots.BindNames()).ToArray();

    /// <summary>
    /// Bind names this build serves that <paramref name="profileJson"/> does not ask for. Not an
    /// error — a site that deleted a status variable deleted it — but after an upgrade it is the
    /// difference between a new VID working and doing nothing at all, so it is said out loud
    /// once at start-up instead of being discovered by a host asking.
    /// </summary>
    public static IReadOnlyList<string> BindsNotInProfile(string profileJson)
    {
        if (string.IsNullOrEmpty(profileJson)) return Array.Empty<string>();
        var asked = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(profileJson, "\"bind\"\\s*:\\s*\"([^\"]*)\""))
            asked.Add(m.Groups[1].Value);
        return SuppliedBindNames.Where(n => !asked.Contains(n)).ToArray();
    }

    private LeakRateEstimate? Estimate() => _snapshot?.LeakRate;

    private uint CountRatios(Func<RatioState, bool> predicate)
    {
        var ratios = _snapshot?.Ratios;
        if (ratios is null)
        {
            return 0;
        }
        uint n = 0;
        for (int i = 0; i < ratios.Count; i++)
        {
            if (predicate(ratios[i].State)) n++;
        }
        return n;
    }

    private static uint Flag(bool value) => value ? 1u : 0u;

    // ---- text --------------------------------------------------------------

    /// <summary>Chamber prefix for alarm text, e.g. "CH2 " (specification §5.3).</summary>
    private string Prefix() => $"CH{_settings.ChamberCode} ";

    private string DescribeLeak(string headline, LeakAlarmLevel level)
    {
        var snap = _snapshot;
        var text = Prefix() + "OES " + headline + $" composite={(int)level}";
        if (snap is null)
        {
            return text;
        }

        uint warning = CountRatios(s => s == RatioState.Warning);
        uint alarm = CountRatios(s => s == RatioState.Alarm);
        uint enabled = CountRatios(s => s != RatioState.Disabled);
        text += $", {alarm} alarm / {warning} warning of {enabled} ratios";

        if (snap.LeakRate is { HasEstimate: true } est)
        {
            text += $", leak rate {est.LeakRate.ToString("0.000e+000", CultureInfo.InvariantCulture)}" +
                    $" +/-{est.Sigma.ToString("0.0e+000", CultureInfo.InvariantCulture)} mbar-L/s" +
                    $" (conf {est.Confidence.ToString("0.00", CultureInfo.InvariantCulture)})";
        }
        if (snap.TestMode)
        {
            text += " [TEST/REPLAY DATA]";
        }
        return text;
    }

    // ---- plumbing ----------------------------------------------------------

    private void Emit(string line)
    {
        _logFile?.Write(line);
        Traffic?.Invoke(this, line);
    }

    /// <summary>What the host has been allowed to do, for the tab. "" when nothing is running.</summary>
    public string ControlStateText { get; private set; } = "";

    private void RefreshState()
    {
        var equipment = _equipment;
        if (equipment is null)
        {
            return;
        }
        // Three things a person debugging a connection needs separated: is there a link
        // (Selected), has GEM said hello (Communicating), or is neither true yet.
        var state = equipment.CommunicationState == CommunicationState.Communicating
            ? SecsRunState.Communicating
            : equipment.State == ConnectionState.Selected
                ? SecsRunState.Selected
                : SecsRunState.Listening;
        ControlStateText = equipment.ControlState.ToString();
        SetState(state, StatusText, LastError);
    }

    private void SetState(SecsRunState state, string status, string error)
    {
        State = state;
        StatusText = status;
        LastError = error;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Stop();
}
