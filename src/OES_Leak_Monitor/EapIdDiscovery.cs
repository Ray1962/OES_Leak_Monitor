using System;
using System.Collections.Generic;
using System.Linq;
using Aqusen.Secs;
using Secs4Net;

namespace OES_Leak_Monitor;

/// <summary>What became of one agreed name when the EAP's namelists were matched against it.</summary>
public enum EapIdMatchState
{
    /// <summary>Exactly one id carries the name.</summary>
    Found,
    /// <summary>Exactly one id carries the name, but its S1F4 value is not the agreed format.
    /// Still offered: the engineer decides, the OES does not.</summary>
    WrongType,
    /// <summary>No id carries the name, and the id is required.</summary>
    NotFound,
    /// <summary>No id carries the name, and the id is optional (S4, the tool-link CEID): the
    /// EAP does not provide it, which #64 allows.</summary>
    NotProvided,
    /// <summary>Two or more ids carry the name; none is offered.</summary>
    Ambiguous,
}

/// <param name="Label">How the EAP tab names the id: S1–S7, forward CEID, tool-link CEID.</param>
/// <param name="Name">The SVNAME / CENAME the spec (§4) asks the platform to give it.</param>
/// <param name="Id">The id carrying the name, or 0 when there is not exactly one.</param>
/// <param name="Note">Why it is not <see cref="EapIdMatchState.Found"/>, in words; "" when it is.</param>
public sealed record EapIdMatch(string Label, string Name, uint Id, EapIdMatchState State, string Note)
{
    /// <summary>Whether <see cref="EapIdDiscovery.ApplyTo"/> takes this id over.</summary>
    public bool Offered => State is EapIdMatchState.Found or EapIdMatchState.WrongType or EapIdMatchState.NotProvided;
}

/// <summary>
/// The EAP's ids found by name (#66): its S1F11 and S1F23 namelists matched against the names
/// the spec agrees with the platform, and the S1F3 values of what was found checked against the
/// agreed formats.
///
/// <para><b>Matching is exact, never fuzzy</b> — trimmed and case-insensitive, nothing more. An
/// id read by the wrong variable is a process window read wrong, so a name either is the agreed
/// one or the id is left for the engineer to type. Nothing here writes the settings: the tab
/// offers the result, and only Save makes it the configuration.</para>
/// </summary>
/// <param name="Problem">Why there is nothing to match — the EAP answered neither namelist, or
/// answered both empty — or "" when there is.</param>
public sealed record EapIdDiscovery(DateTime At, IReadOnlyList<EapIdMatch> Matches,
    IReadOnlyList<StatusVariableName> StatusVariables, IReadOnlyList<CollectionEventName> Events, string Problem)
{
    /// <summary>The spec's §4 name table, in the EAP tab's order.</summary>
    public static readonly IReadOnlyList<(string Label, string Name, bool IsEvent, bool Optional)> Agreed = new[]
    {
        ("S1", "PM3_WINDOW_STATE", false, false),
        ("S2", "PM3_WINDOW_CEID", false, false),
        ("S3", "PM3_WINDOW_START", false, false),
        ("S4", "PM3_TOOL_LINK", false, true),
        ("S5", "PM3_LAST_CEID", false, false),
        ("S6", "PM3_LAST_TIME", false, false),
        ("S7", "PM3_EVENT_SEQ", false, false),
        ("forward CEID", "PM3_EVENT_FORWARD", true, false),
        ("tool-link CEID", "PM3_TOOL_LINK_CHANGED", true, true),
    };

    /// <summary>S1–S7 carry U1, U4, A, U1, U4, A, U4; what is checked is the kind — a number
    /// or text — since the link reads any integer format the way <c>TOOL_EVENT</c> reads a CEID.</summary>
    private static readonly Dictionary<string, bool> IsText = new()
    {
        ["S1"] = false, ["S2"] = false, ["S3"] = true, ["S4"] = false, ["S5"] = false, ["S6"] = true, ["S7"] = false,
    };

    /// <summary>The OES's rule, the one the spec states and Test_SECS's host profile applies.</summary>
    public static bool SameName(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Matches the agreed names against what the EAP answered. A null list is a namelist the EAP
    /// did not answer: its names read <see cref="EapIdMatchState.NotFound"/> (or NotProvided when
    /// optional) with that said in the note.
    /// </summary>
    public static IReadOnlyList<EapIdMatch> Match(IReadOnlyList<StatusVariableName>? svs, IReadOnlyList<CollectionEventName>? events)
    {
        var result = new List<EapIdMatch>();
        foreach (var (label, name, isEvent, optional) in Agreed)
        {
            uint[] ids = isEvent
                ? events?.Where(e => SameName(e.Name, name)).Select(e => e.Ceid).Distinct().ToArray() ?? Array.Empty<uint>()
                : svs?.Where(v => SameName(v.Name, name)).Select(v => v.Svid).Distinct().ToArray() ?? Array.Empty<uint>();
            bool answered = isEvent ? events is not null : svs is not null;
            result.Add(ids.Length switch
            {
                1 => new EapIdMatch(label, name, ids[0], EapIdMatchState.Found, ""),
                0 => new EapIdMatch(label, name, 0, optional ? EapIdMatchState.NotProvided : EapIdMatchState.NotFound,
                    (answered ? $"{name} not found" : $"{(isEvent ? "S1F23" : "S1F11")} not answered") +
                    (optional ? " — treated as not provided" : "")),
                _ => new EapIdMatch(label, name, 0, EapIdMatchState.Ambiguous,
                    $"{name} matches {(isEvent ? "CEID" : "SVID")} {string.Join(" and ", ids)}"),
            });
        }
        return result;
    }

    /// <summary>The SVIDs to read for the format check, in the order <see cref="CheckFormats"/>
    /// expects their values: every found S1–S7, in label order.</summary>
    public static uint[] SvidsToCheck(IReadOnlyList<EapIdMatch> matches) =>
        matches.Where(m => IsText.ContainsKey(m.Label) && m.State == EapIdMatchState.Found).Select(m => m.Id).ToArray();

    /// <summary>
    /// Marks each found S1–S7 whose S1F4 value is the wrong kind. <paramref name="values"/> answer
    /// <see cref="SvidsToCheck"/> in order; null means the read failed, which checks nothing.
    /// </summary>
    public static IReadOnlyList<EapIdMatch> CheckFormats(IReadOnlyList<EapIdMatch> matches, IReadOnlyList<Item>? values)
    {
        if (values is null) return matches;
        var checkedIds = SvidsToCheck(matches);
        if (values.Count != checkedIds.Length) return matches;
        var byId = checkedIds.Select((id, i) => (id, value: values[i])).ToDictionary(p => p.id, p => p.value);
        return matches.Select(m =>
        {
            if (m.State != EapIdMatchState.Found || !IsText.TryGetValue(m.Label, out bool text) ||
                !byId.TryGetValue(m.Id, out var value))
                return m;
            bool isText = value.Format is SecsFormat.ASCII or SecsFormat.JIS8;
            bool isNumber = SecsBridge.ParseCeid(value) is not null && !isText;
            if (text ? isText : isNumber) return m;
            return m with
            {
                State = EapIdMatchState.WrongType,
                Note = $"SVID {m.Id} answers {value.Format}; {(text ? "text (A)" : "a number (U1/U4)")} expected",
            };
        }).ToList();
    }

    /// <summary>Whether there is anything for "Use these ids" to take.</summary>
    public bool HasOffer => Matches.Any(m => m.Offered && m.State != EapIdMatchState.NotProvided);

    /// <summary>
    /// The ids this result offers, written into <paramref name="settings"/>: found ones (format
    /// mismatches included — flagged, not withheld), and 0 for an optional id the EAP does not
    /// provide. A required id that was not found, or was ambiguous, is left as it was.
    /// </summary>
    public void ApplyTo(EapLinkSettings settings)
    {
        foreach (var m in Matches.Where(m => m.Offered))
        {
            switch (m.Label)
            {
                case "S1": settings.WindowStateSvid = m.Id; break;
                case "S2": settings.WindowCeidSvid = m.Id; break;
                case "S3": settings.WindowStartSvid = m.Id; break;
                case "S4": settings.ToolLinkSvid = m.Id; break;
                case "S5": settings.LastCeidSvid = m.Id; break;
                case "S6": settings.LastTimeSvid = m.Id; break;
                case "S7": settings.SequenceSvid = m.Id; break;
                case "forward CEID": settings.ForwardCeid = m.Id; break;
                case "tool-link CEID": settings.LinkCeid = m.Id; break;
            }
        }
    }

    /// <summary>The id <paramref name="settings"/> holds for <paramref name="label"/>.</summary>
    public static uint Current(EapLinkSettings settings, string label) => label switch
    {
        "S1" => settings.WindowStateSvid,
        "S2" => settings.WindowCeidSvid,
        "S3" => settings.WindowStartSvid,
        "S4" => settings.ToolLinkSvid,
        "S5" => settings.LastCeidSvid,
        "S6" => settings.LastTimeSvid,
        "S7" => settings.SequenceSvid,
        "forward CEID" => settings.ForwardCeid,
        "tool-link CEID" => settings.LinkCeid,
        _ => 0,
    };

    /// <summary>One line for the log and the tab: how many of the agreed names were found.</summary>
    public string Summary
    {
        get
        {
            if (Problem.Length > 0) return Problem;
            int found = Matches.Count(m => m.State == EapIdMatchState.Found);
            var issues = Matches.Where(m => m.State is not EapIdMatchState.Found).Select(m => $"{m.Label}: {m.Note}").ToArray();
            return $"{found} of {Matches.Count} agreed names found" + (issues.Length > 0 ? " — " + string.Join("; ", issues) : "");
        }
    }
}
