using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    // CEID nnn, specification §6.1. Only the three this app raises.
    private const int CeidAlarmAcknowledged = 502;
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
    private SecsLogFile? _logFile;

    // The only shared measurement state. Immutable object, single reference — see the class note.
    private volatile LeakMonitorSnapshot? _snapshot;

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
                BuildBindings());

            equipment.Log += Emit;
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
            // Disposal talks to the socket, so it is not something to block the UI thread on.
            _ = Task.Run(async () =>
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
    /// </summary>
    private SvBindings BuildBindings()
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

        return b;
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
