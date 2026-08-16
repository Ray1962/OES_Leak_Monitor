namespace OES_Leak_Monitor;

/// <summary>
/// SECS/GEM equipment-side configuration: whether the interface runs at all, what it
/// reports, and the HSMS connection parameters.
/// <para/>
/// The full design is in <c>docs/secs-integration.md</c>; the field semantics on the wire
/// come from <c>docs/Satellite_SECS_Specification_v2.md</c> (<c>ss=27</c>).
/// <para/>
/// A <c>settings.json</c> written before this section existed gets these defaults, i.e.
/// <b>the interface stays off</b> until someone turns it on.
/// </summary>
public sealed class SecsSettings
{
    /// <summary>
    /// Master switch. False means nothing is started and no port is opened — the SECS tab
    /// still renders, showing <c>Disabled</c>.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Whether composite leak state and equipment faults go out as S5F1 alarms.
    /// <para/>
    /// Off does not hide anything: the state still reaches the UI, the audit log and
    /// S1F3 (VID 007). It only stops the equipment volunteering it — which is what you
    /// want while commissioning, so a test run doesn't file alarms in the fab's MES.
    /// </summary>
    public bool ReportAlarms { get; set; } = true;

    /// <summary>Whether S6F11 event reports are sent. Same reasoning as <see cref="ReportAlarms"/>.</summary>
    public bool ReportEvents { get; set; } = true;

    /// <summary>
    /// Whether alarms and events are sent while the frames are synthetic (no hardware) or
    /// replayed from a recording.
    /// <para/>
    /// <b>Off by default.</b> The app already refuses to let replayed data be mistaken for
    /// measurement — replayed CSVs are written under a <c>SIM</c> prefix — and an alarm
    /// arriving at the MES carries no such marking. Turn it on deliberately, to validate
    /// the whole alarm path against a recording of a real leak.
    /// <para/>
    /// Whatever this says, VID 016 (Test / replay mode) reports the truth, so a host can
    /// tell for itself that a reading is not a measurement.
    /// </summary>
    public bool ReportInTestMode { get; set; } = false;

    /// <summary>
    /// The chamber this OES watches — <c>cc</c> in the SVID/ALID/CEID encoding
    /// <c>1ccssaavvv</c>. Every id the equipment serves is derived from it, so it is the
    /// one number that must be right (see <see cref="SecsChamberCoding"/> and
    /// specification §1.1 for the code table).
    /// </summary>
    public int ChamberCode { get; set; } = 0;

    /// <summary>
    /// Whether this end opens the connection. Equipment is normally passive: it listens
    /// and the host connects to it.
    /// </summary>
    public bool IsActive { get; set; } = false;

    /// <summary>Address to listen on. <c>0.0.0.0</c> is every adapter; use <c>127.0.0.1</c> to bench-test.</summary>
    public string IpAddress { get; set; } = "0.0.0.0";

    /// <summary>TCP port.</summary>
    public int Port { get; set; } = 5000;

    /// <summary>
    /// HSMS device id. Must match the host's setting: the handshake does not check it, so
    /// a mismatch shows up later as data messages being rejected with S9F1.
    /// </summary>
    public int DeviceId { get; set; } = 0;

    /// <summary>MDLN, reported in S1F2 / S1F14.</summary>
    public string ModelName { get; set; } = "OESLM";

    /// <summary>SOFTREV, reported in S1F2 / S1F14.</summary>
    public string SoftwareRevision { get; set; } = "1.0.0";

    /// <summary>Reply timeout, seconds (specification §2 default 45).</summary>
    public int T3 { get; set; } = 45;

    /// <summary>Connect separation timeout, seconds.</summary>
    public int T5 { get; set; } = 10;

    /// <summary>Control transaction timeout, seconds.</summary>
    public int T6 { get; set; } = 10;

    /// <summary>Not-selected timeout, seconds.</summary>
    public int T7 { get; set; } = 10;

    /// <summary>Network intercharacter timeout, seconds.</summary>
    public int T8 { get; set; } = 10;

    /// <summary>
    /// Device profile file name, inside <c>{ConfigDirectory}\profiles\</c>. The file is the
    /// site-editable half of the interface: SVID numbers, names and alarm texts live there,
    /// the app only supplies the values behind them.
    /// </summary>
    public string ProfileFileName { get; set; } = "oes-leak-monitor.json";

    /// <summary>How many days of SECS traffic logs to keep. 0 keeps them for ever.</summary>
    public int LogRetentionDays { get; set; } = 30;
}
