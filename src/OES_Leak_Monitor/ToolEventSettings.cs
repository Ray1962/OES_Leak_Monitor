using System;
using System.Collections.Generic;
using System.Linq;

namespace OES_Leak_Monitor;

/// <summary>
/// Process events from the main tool, forwarded by the fab's EAP as S2F41 <c>TOOL_EVENT</c>
/// (parameter <c>CEID</c> = the tool's own collection event id). They tell the leak monitor
/// which process the chamber is running, so a ratio scoped to a class judges only inside that
/// process's window — the spectrum alone can only infer it, the tool knows.
///
/// <para>Off by default, and an empty mapping does nothing, so an installation that never
/// configures it behaves exactly as before. Serialized under <see cref="SecsSettings"/> because
/// the events arrive on the SECS link and mean nothing without it.</para>
///
/// <para><b>Staged</b>, like a Ratio Setup edit: saved now, adopted by the engine when
/// acquisition next restarts. A mapping names the classes a ratio's <c>ProcessClass</c> refers
/// to, so applying one half live would leave the two disagreeing for the rest of the run.</para>
/// </summary>
public sealed class ToolEventSettings
{
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// What each forwarded CEID means. A CEID not listed here is acknowledged and ignored, so
    /// the EAP can forward the tool's events unfiltered.
    /// </summary>
    public List<ToolEventMapping> Mappings { get; set; } = new();

    /// <summary>
    /// How long a window may stay open before the engine stops believing it. An End the EAP never
    /// forwarded would otherwise keep every later process judged as this one; past the limit the
    /// window closes to Unknown (the behaviour without tool events) and a warning says why.
    /// Measured on the frame clock. <b>Hot-applied</b>, unlike the rest of this section: it names
    /// no class, so applying it mid-run leaves nothing disagreeing.
    /// </summary>
    public double MaxWindowSeconds { get; set; } = DefaultMaxWindowSeconds;

    /// <summary>Ten minutes — the measured process A step is 38–63 s, so a window this long has
    /// lost its End rather than run long.</summary>
    public const double DefaultMaxWindowSeconds = 600;

    /// <summary>
    /// Whether the tool's window is applied to the test-mode generator's synthetic spectra.
    /// Off by default: a synthetic spectrum is not the chamber, and judging it as process A
    /// because the tool happens to be running A says nothing. On, it is the only way to exercise
    /// the windows on a machine with no spectrometer. Never applies during a replay, which plays
    /// the past against a window that is the present. <b>Hot-applied</b>, like
    /// <see cref="MaxWindowSeconds"/>.
    /// </summary>
    public bool ApplyInTestMode { get; set; }

    /// <summary>
    /// The SECS link to the EAP (scheme 2b, <c>docs/eap-tool-event-secs-spec-zh-TW.md</c>): the
    /// OES as host, the EAP as equipment — a second way for the same events to arrive, because
    /// the site's SECS host is the FDC and HSMS-SS leaves the EAP no session to send
    /// <c>TOOL_EVENT</c> on. <b>Hot-applied</b>: it is a transport and names no class. Whether
    /// what it delivers is used is still <see cref="Enabled"/>'s call.
    /// </summary>
    public EapLinkSettings Eap { get; set; } = new();

    /// <summary>
    /// Whether two settings agree on the <b>staged</b> half — the switch and the table — which is
    /// what the engine adopts at an acquisition start. The hot-applied fields are left out: they
    /// take effect on save, so they never leave a saved table waiting. Switched off is one state
    /// whatever rows it carries.
    /// </summary>
    public static bool SameStaged(ToolEventSettings? a, ToolEventSettings? b)
    {
        bool aOn = a?.Enabled == true, bOn = b?.Enabled == true;
        if (aOn != bOn) return false;
        if (!aOn) return true;
        var x = a!.Mappings ?? new();
        var y = b!.Mappings ?? new();
        return x.Count == y.Count && x.Zip(y).All(p =>
            p.First.Ceid == p.Second.Ceid &&
            string.Equals(p.First.ClassName?.Trim() ?? "", p.Second.ClassName?.Trim() ?? "", StringComparison.Ordinal) &&
            p.First.Edge == p.Second.Edge &&
            p.First.Name == p.Second.Name &&
            p.First.PlasmaThreshold.Equals(p.Second.PlasmaThreshold));
    }

    public ToolEventSettings Clone() => new()
    {
        Enabled = Enabled,
        Mappings = (Mappings ?? new()).Select(m => m.Clone()).ToList(),
        MaxWindowSeconds = MaxWindowSeconds,
        ApplyInTestMode = ApplyInTestMode,
        Eap = (Eap ?? new()).Clone(),
    };
}

/// <summary>
/// Where and how the OES connects to the EAP as a SECS host, and the nine ids the EAP assigns.
/// The OES is active; the EAP listens on the address it gave (<c>10.2.3.120:20347</c>).
/// <para>Off by default, like the SECS interface, so an upgrade opens no connection until asked.
/// The ids default to 0, which means "not set": the link does not start until all nine are.</para>
/// </summary>
public sealed class EapLinkSettings
{
    public bool Enabled { get; set; } = false;

    public string RemoteHost { get; set; } = DefaultRemoteHost;

    public int RemotePort { get; set; } = DefaultRemotePort;

    public int DeviceId { get; set; }

    /// <summary>
    /// How long without a successful S1F3 reply and without an S6F11 before the window stops
    /// being trusted. A dropped connection by itself changes nothing — the reconnect and the
    /// S1F3 that follows restore the window sooner than this.
    /// </summary>
    public double SilenceTimeoutSeconds { get; set; } = DefaultSilenceTimeoutSeconds;

    /// <summary>
    /// Events only (#74, <c>secs-integration.md</c> §15.10): receive the forwarded events and
    /// never read S1–S7 — for an EAP that has not assigned them yet, or cannot. The forwarding
    /// CEID is then the only required id; S1–S7 keep whatever was typed, unjudged, so turning this
    /// off goes straight back to the full link. With nothing to correct a lost event, the link
    /// reports the window unknown whenever one may have been lost.
    /// </summary>
    public bool EventsOnly { get; set; }

    /// <summary>S1 PM3 window state (U1: 0 outside / 1 in a window / 2 unknown).</summary>
    public uint WindowStateSvid { get; set; }
    /// <summary>S2 Start CEID of the open window (U4).</summary>
    public uint WindowCeidSvid { get; set; }
    /// <summary>S3 when the open window started (A).</summary>
    public uint WindowStartSvid { get; set; }
    /// <summary>S4 the EAP's SECS link to the tool (U1: 0 down / 1 up). <b>Optional</b>: 0 means
    /// the EAP does not provide it (spec Q5), and the S1F3 reads S1–S3, S5–S7 only.</summary>
    public uint ToolLinkSvid { get; set; }
    /// <summary>S5 last forwarded tool CEID (U4).</summary>
    public uint LastCeidSvid { get; set; }
    /// <summary>S6 when the EAP received it (A).</summary>
    public uint LastTimeSvid { get; set; }
    /// <summary>S7 sequence number of the last forwarded event (U4).</summary>
    public uint SequenceSvid { get; set; }
    /// <summary>The one CEID every tool event is forwarded under, with the fixed report
    /// <c>L{ U4 tool CEID, A[16] receive time, U4 sequence }</c>.</summary>
    public uint ForwardCeid { get; set; }
    /// <summary>The CEID sent when the EAP's link to the tool changes, with <c>L{ U1 }</c>.
    /// <b>Optional</b>, like <see cref="ToolLinkSvid"/>: 0 means the EAP does not send it, and
    /// S2F37 enables the forwarding CEID alone.</summary>
    public uint LinkCeid { get; set; }

    /// <summary>The address the EAP gave, 2026-10-26.</summary>
    public const string DefaultRemoteHost = "10.2.3.120";
    public const int DefaultRemotePort = 20347;

    /// <summary>Two missed 30 s polls plus a margin.</summary>
    public const double DefaultSilenceTimeoutSeconds = 75;

    /// <summary>Range Save accepts. Under 35 s a healthy 30 s poll would time out.</summary>
    public const double MinSilenceTimeoutSeconds = 35, MaxSilenceTimeoutSeconds = 600;

    /// <summary>S1–S7 in order, 0 where unset.</summary>
    public uint[] StatusSvids => new[]
    {
        WindowStateSvid, WindowCeidSvid, WindowStartSvid, ToolLinkSvid, LastCeidSvid, LastTimeSvid, SequenceSvid,
    };

    /// <summary>Whether the EAP provides S4, the state of its own link to the tool.</summary>
    public bool HasToolLinkSvid => ToolLinkSvid != 0;

    /// <summary>Whether the EAP sends the tool-link CEID when that link changes.</summary>
    public bool HasLinkCeid => LinkCeid != 0;

    /// <summary>What the S1F3 asks for: S1–S7 in order, without S4 when the EAP does not
    /// provide it. The S1F4 comes back in this order.</summary>
    public uint[] QueriedSvids => HasToolLinkSvid
        ? StatusSvids
        : new[] { WindowStateSvid, WindowCeidSvid, WindowStartSvid, LastCeidSvid, LastTimeSvid, SequenceSvid };

    /// <summary>What S2F37 enables: the forwarding CEID, and the tool-link CEID when set.</summary>
    public uint[] EnabledCeids => HasLinkCeid ? new[] { ForwardCeid, LinkCeid } : new[] { ForwardCeid };

    /// <summary>The ids the link cannot run without, named as the EAP tab names them: seven, or
    /// the forwarding CEID alone when <see cref="EventsOnly"/>.</summary>
    private (string Name, uint Id)[] Required => EventsOnly
        ? new (string Name, uint Id)[] { ("forwarding CEID", ForwardCeid) }
        : new (string Name, uint Id)[]
        {
            ("S1", WindowStateSvid), ("S2", WindowCeidSvid), ("S3", WindowStartSvid), ("S5", LastCeidSvid),
            ("S6", LastTimeSvid), ("S7", SequenceSvid), ("forwarding CEID", ForwardCeid),
        };

    /// <summary>
    /// None of the seven required ids is set: the link only proves it can reach the EAP (#65) —
    /// online, S1F1 — and reads nothing, enables nothing, reports nothing to the engine. It is
    /// how a site checks the address, port, device id and firewall before the EAP has assigned
    /// its ids. Judged on the required set alone, so an optional S4 or tool-link CEID typed in
    /// early does not turn it into a half-filled configuration.
    /// </summary>
    public bool IsLinkTestOnly => Required.All(r => r.Id == 0);

    /// <summary>Null when the ids are usable — including none set at all, which is
    /// <see cref="IsLinkTestOnly"/>; otherwise what is wrong with them. Shared by the tab's Save
    /// and the link, so the two cannot disagree about what "set" means. S4 and the tool-link
    /// CEID may be 0 (not provided); once any of the other seven is set, all of them must be.</summary>
    public string? IdProblem()
    {
        if (IsLinkTestOnly) return null;
        var unset = Required.Where(r => r.Id == 0).Select(r => r.Name).ToArray();
        if (unset.Length > 0) return $"the EAP ids are not all set ({string.Join(", ", unset)})";
        var svids = StatusSvids.Where(i => i != 0).ToArray();
        if (!EventsOnly && svids.Distinct().Count() != svids.Length) return "two of S1–S7 share an SVID";
        if (HasLinkCeid && ForwardCeid == LinkCeid) return "the forwarding and tool-link CEIDs are the same";
        return null;
    }

    /// <summary>
    /// Null when the EAP provides both S4 and the tool-link CEID; otherwise what the OES then
    /// cannot see, for the log and the EAP tab. A lost EAP–tool link is still reported through
    /// S1 = 2 — the EAP is asked to set it (spec E4/E5) — but no longer by a value of its own.
    /// </summary>
    public string? ToolLinkGap() => (HasToolLinkSvid, HasLinkCeid) switch
    {
        (true, true) => null,
        (false, false) => "the EAP provides neither S4 nor the tool-link CEID: a lost link between the EAP " +
                          "and the tool is seen only as S1 = 2, or when a window outlasts the tool-event max window",
        (false, true) => "the EAP provides no S4: a change of its link to the tool is seen only through the " +
                         "tool-link CEID, and one that event missed is not corrected by the 30 s S1F3",
        (true, false) => "the EAP sends no tool-link CEID: a change of its link to the tool is seen at the " +
                         "next 30 s S1F3 instead of when it happens",
    };

    /// <summary>
    /// Null in full mode; with <see cref="EventsOnly"/>, what the OES then cannot see, for the log
    /// and the EAP tab — the counterpart of <see cref="ToolLinkGap"/>, which is about S4 and has
    /// nothing to say once S1–S7 are not read at all.
    /// </summary>
    public string? EventsOnlyGap() => !EventsOnly ? null :
        "S1–S7 are not read, so the process window is set to unknown on every connect, sequence gap " +
        "and EAP restart and stays unknown until the next process event; an EAP that still answers " +
        "S1F1 but has stopped forwarding is not detected" +
        (HasLinkCeid ? "" : "; with no tool-link CEID, a lost link between the EAP and the tool is " +
                            "seen only when a window outlasts the tool-event max window");

    public EapLinkSettings Clone() => (EapLinkSettings)MemberwiseClone();
}

/// <summary>Which end of a process window a tool event marks.</summary>
public enum ToolEventEdge
{
    Start,
    End,
}

/// <summary>One row of the CEID table — in practice a line of <c>docs/EAP ceid-events.md</c>.</summary>
public sealed class ToolEventMapping
{
    /// <summary>The main tool's CEID, exactly as the EAP forwards it.</summary>
    public uint Ceid { get; set; }

    /// <summary>
    /// The process class this event's window is, spelled as ratios spell it in
    /// <c>ProcessClass</c>. Empty = recorded only: the event is logged and changes no window
    /// (Auto Leak check, slit door).
    /// </summary>
    public string ClassName { get; set; } = "";

    public ToolEventEdge Edge { get; set; } = ToolEventEdge.Start;

    /// <summary>The tool's own name for the event, for the log. Not interpreted.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Trigger-metric level that counts as plasma-on inside this class's window. ≤ 0 = inherit:
    /// the process classifier's threshold for the same class name if one is configured (whether
    /// or not the classifier itself is switched on), else the logger's save threshold.
    /// </summary>
    public double PlasmaThreshold { get; set; }

    public ToolEventMapping Clone() => new()
    {
        Ceid = Ceid, ClassName = ClassName, Edge = Edge, Name = Name, PlasmaThreshold = PlasmaThreshold,
    };
}

/// <summary>
/// What the engine currently knows about the tool's process window. <b>The declaration order is
/// the wire encoding</b> of VID 030 (cast straight to U4) — append, never insert.
/// </summary>
public enum ToolEventContext
{
    /// <summary>Tool events are not in use; class scoping is whatever it was without them.</summary>
    Disabled = 0,
    /// <summary>In use, but no window edge has arrived since they were applied — the engine falls
    /// back to the behaviour it had without them rather than judge nothing.</summary>
    Unknown = 1,
    /// <summary>Inside a window whose class is known.</summary>
    InWindow = 2,
    /// <summary>A window has closed and no other is open: no class is running.</summary>
    OutsideWindow = 3,
}

/// <summary>
/// Where the tool events have put the engine, published the moment it changes. It is not part
/// of a frame: the table is applied, and the host's events arrive, while no spectrum may be
/// flowing at all — reading it off the last frame told an operator "not in use" for a table that
/// had been applied, and told the host VID 030 = 0, until acquisition happened to start.
/// </summary>
/// <param name="ReceivedCount">Events received this run while tool events were in use — taken
/// or ignored by the table alike. The SECS tab shows it so "is the EAP forwarding anything?" has a
/// number for an answer.</param>
public sealed record ToolEventStatus(ToolEventContext State, string WindowClass, uint LastCeid, DateTime? LastTime,
    int ReceivedCount = 0)
{
    public static readonly ToolEventStatus Disabled = new(ToolEventContext.Disabled, "", 0, null);
}

/// <summary>What became of one forwarded tool event, for the HCACK the host is answered with.</summary>
public enum ToolEventAck
{
    /// <summary>The CEID is in the table and was taken.</summary>
    Accepted,
    /// <summary>The CEID is not in the table — acknowledged, nothing done.</summary>
    Ignored,
    /// <summary>Tool events are not in use as currently applied.</summary>
    Disabled,
    /// <summary>The report contradicts the table — a heartbeat naming an End CEID as the event
    /// that opened the current window. Nothing changes.</summary>
    Invalid,
}

/// <summary>What the EAP's heartbeat says about the tool's window: <c>STATE</c> on the wire.</summary>
public enum ToolContextReportState
{
    Outside = 0,
    InWindow = 1,
    Unknown = 2,
}

/// <summary>
/// A statement of where the tool's window <i>is</i>, as opposed to an edge that moves it: the
/// EAP's heartbeat, or the link deciding it can no longer vouch for one. The engine compares it
/// with the window it holds and corrects only a difference.
/// </summary>
/// <param name="StartCeid">For <see cref="ToolContextReportState.InWindow"/>: the tool CEID that
/// opened the window, read through the CEID table like an event.</param>
/// <param name="Reason">Why, in words, for the log line a correction writes.</param>
public readonly record struct ToolContextReport(ToolContextReportState State, uint StartCeid, string Reason);

/// <summary>
/// An applied <see cref="ToolEventSettings"/>, indexed for the two questions asked of it on
/// every event and every frame. Immutable, so the SECS thread and the frame thread can share
/// one reference without a lock.
/// </summary>
internal sealed class ToolEventTable
{
    private readonly Dictionary<uint, ToolEventMapping> _byCeid;
    private readonly Dictionary<string, double> _thresholds;

    private ToolEventTable(ToolEventSettings settings)
    {
        var rows = (settings.Mappings ?? new()).Select(m => m.Clone()).ToList();
        // A CEID listed twice is a typo in a hand-edited file; the first row wins, as the table
        // on screen reads top to bottom.
        _byCeid = new Dictionary<uint, ToolEventMapping>();
        foreach (var m in rows) _byCeid.TryAdd(m.Ceid, m);
        _thresholds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in rows)
            if (m.PlasmaThreshold > 0 && !string.IsNullOrWhiteSpace(m.ClassName))
                _thresholds.TryAdd(m.ClassName.Trim(), m.PlasmaThreshold);
        Signature = string.Join(";", rows.Select(m =>
            $"{m.Ceid}|{m.ClassName?.Trim()}|{m.Edge}|{m.PlasmaThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
    }

    /// <summary>Null when the settings switch tool events off.</summary>
    public static ToolEventTable? From(ToolEventSettings? settings) =>
        settings is { Enabled: true } ? new ToolEventTable(settings) : null;

    /// <summary>What decides the windows. Two tables with the same signature name the same
    /// windows, so re-applying one (as every acquisition restart does) need not forget the window
    /// the tool is in. The event's display name is deliberately not part of it.</summary>
    public string Signature { get; }

    public int Count => _byCeid.Count;

    public bool TryGet(uint ceid, out ToolEventMapping mapping) => _byCeid.TryGetValue(ceid, out mapping!);

    /// <summary>The plasma threshold this table sets for a class, or null to inherit.</summary>
    public double? ThresholdFor(string? className) =>
        className is not null && _thresholds.TryGetValue(className.Trim(), out var t) ? t : null;
}
