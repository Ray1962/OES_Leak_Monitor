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

    public ToolEventSettings Clone() => new()
    {
        Enabled = Enabled,
        Mappings = (Mappings ?? new()).Select(m => m.Clone()).ToList(),
    };
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

/// <summary>What became of one forwarded tool event, for the HCACK the host is answered with.</summary>
public enum ToolEventAck
{
    /// <summary>The CEID is in the table and was taken.</summary>
    Accepted,
    /// <summary>The CEID is not in the table — acknowledged, nothing done.</summary>
    Ignored,
    /// <summary>Tool events are not in use as currently applied.</summary>
    Disabled,
}

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
