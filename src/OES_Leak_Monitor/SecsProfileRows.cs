using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace OES_Leak_Monitor;

/// <summary>One row of a profile section, identified the way the section is matched: a status
/// variable by its <c>bind</c>, an event by CEID, an alarm by ALID, a remote command by name.
/// <see cref="Number"/> is the SVID / CEID / ALID (0 for a remote command).</summary>
public sealed record SecsProfileRow(string Section, string Key, long Number, string Text)
{
    public override string ToString() => Section switch
    {
        "statusVariables" => $"SVID {Number} ({Key})",
        "events" => $"CEID {Number}",
        "alarms" => $"ALID {Number}",
        _ => $"remote command {Key}",
    };
}

/// <summary>A row that was not added, and why.</summary>
public sealed record SecsProfileRowRejection(SecsProfileRow Row, string Reason);

/// <summary>What <see cref="SecsProfileRows.AddMissing"/> produced.</summary>
public sealed record SecsProfileRowsResult(
    string Json,
    IReadOnlyList<SecsProfileRow> Added,
    IReadOnlyList<SecsProfileRowRejection> Rejected);

/// <summary>
/// Brings a site's profile up to date with the shipped template by <b>appending</b> what it
/// lacks — status variables, events, alarms and remote commands this build serves and the file
/// never asked for. A site profile is never overwritten (<see cref="SecsProfileTemplate.EnsureExists"/>),
/// so without this a new status variable reaches no machine that has ever started the interface.
///
/// <para>It edits text, not a parsed document. Re-serialising would lose the comments and the
/// layout someone on site chose, and the promise is that nothing already in the file changes:
/// not a renumbered SVID, not a translated name, not a comment. New rows are inserted after the
/// last element of their section (or a missing section after the last one present), and
/// everything else is copied through byte for byte.</para>
///
/// <para>It also restores rows the site deleted — from the file alone, "deleted on purpose" and
/// "written by a build that did not have it" look the same. That is why this is a button and
/// not something done at start-up.</para>
/// </summary>
public static class SecsProfileRows
{
    // Template order, which is also the order a missing section is appended in.
    private static readonly string[] Sections = { "statusVariables", "alarms", "events", "remoteCommands" };

    private static readonly JsonDocumentOptions DocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Rows of <paramref name="templateJson"/> that <paramref name="siteJson"/> does not
    /// have, in template order.</summary>
    public static IReadOnlyList<SecsProfileRow> Missing(string siteJson, string templateJson)
    {
        var have = Present(siteJson);
        return Rows(templateJson).Where(r => !have.Keys.Contains((r.Section, Norm(r.Section, r.Key)))).ToList();
    }

    /// <summary>
    /// <paramref name="siteJson"/> with every <see cref="Missing"/> row appended to its section.
    /// A status variable whose SVID the site already uses for another row is refused — two rows
    /// with one SVID would answer the host with whichever the library met last — and listed in
    /// <see cref="SecsProfileRowsResult.Rejected"/>; the others go in regardless.
    /// </summary>
    public static SecsProfileRowsResult AddMissing(string siteJson, string templateJson)
    {
        var have = Present(siteJson);
        var added = new List<SecsProfileRow>();
        var rejected = new List<SecsProfileRowRejection>();
        foreach (var row in Missing(siteJson, templateJson))
        {
            if (row.Section == "statusVariables" && have.Svids.Contains(NormId(row.Number)))
            {
                rejected.Add(new SecsProfileRowRejection(row,
                    $"SVID {row.Number} is already used by another row in this profile"));
                continue;
            }
            added.Add(row);
        }
        var bytes = Encoding.UTF8.GetBytes(siteJson);
        var layout = Scan(bytes);

        // A section the file has but not as a list ("remoteCommands": null) cannot take a row,
        // and a second property of the same name would leave which one counts to the parser.
        foreach (var row in added.Where(r => !layout.Sections.ContainsKey(r.Section) &&
                                             layout.RootProperties.Contains(r.Section)).ToList())
        {
            added.Remove(row);
            rejected.Add(new SecsProfileRowRejection(row, $"\"{row.Section}\" in this profile is not a list"));
        }
        if (added.Count == 0) return new SecsProfileRowsResult(siteJson, added, rejected);

        // Written in the file's own line ending, so a CRLF file does not become a mixed one.
        string nl = siteJson.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var inserts = new List<(int At, string Text)>();
        var appendSections = new StringBuilder();
        foreach (var section in Sections)
        {
            var rows = added.Where(r => r.Section == section).Select(r => "    " + r.Text).ToList();
            if (rows.Count == 0) continue;
            if (layout.Sections.TryGetValue(section, out var at) && at.LastElementEnd >= 0)
            {
                // On new lines after the last element's line, so that line — and a comment
                // trailing it — is left exactly as it was: the separating comma leads the first
                // new line rather than being appended to the old one.
                var (pos, hasComma) = AfterLastElement(bytes, at.LastElementEnd);
                inserts.Add((pos, "\n" + (hasComma ? "    " : "  , ") + string.Join(",\n", rows).TrimStart()));
            }
            else if (at is not null)
            {
                // An empty [] or {} has to be opened up; there is no row in it to preserve.
                inserts.Add((at.ContentStart, "\n" + string.Join(",\n", rows) + "\n  "));
            }
            else
            {
                var (open, close) = section == "remoteCommands" ? ("{", "}") : ("[", "]");
                if (appendSections.Length > 0) appendSections.Append(',');
                appendSections.Append($"\n\n  \"{section}\": {open}\n{string.Join(",\n", rows)}\n  {close}");
            }
        }
        if (appendSections.Length > 0)
        {
            if (layout.RootLastValueEnd < 0)
                throw new InvalidOperationException("the profile has no properties to append a section after");
            // Same rule as rows: the root's last line, comment and all, stays as it was.
            var (pos, hasComma) = AfterLastElement(bytes, layout.RootLastValueEnd);
            var text = appendSections.ToString();
            inserts.Add((pos, hasComma ? text : "\n\n  , " + text.TrimStart()));
        }

        // Back to front, so each insertion leaves the offsets of the ones still to come alone.
        var output = new List<byte>(bytes);
        foreach (var (at, text) in inserts.OrderByDescending(i => i.At))
        {
            output.InsertRange(at, Encoding.UTF8.GetBytes(text.Replace("\n", nl)));
        }
        var json = Encoding.UTF8.GetString(output.ToArray());

        // Never hand back something the interface would then refuse to start on.
        try
        {
            using var check = JsonDocument.Parse(json, DocOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "adding the rows would leave the profile unreadable, so nothing was changed: " + ex.Message, ex);
        }
        return new SecsProfileRowsResult(json, added, rejected);
    }

    // ---- reading -----------------------------------------------------------

    private sealed record Have(HashSet<(string Section, string Key)> Keys, HashSet<long> Svids);

    private static Have Present(string json)
    {
        var keys = new HashSet<(string, string)>();
        var svids = new HashSet<long>();
        foreach (var row in Rows(json))
        {
            keys.Add((row.Section, Norm(row.Section, row.Key)));
            if (row.Section == "statusVariables") svids.Add(NormId(row.Number));
        }
        // A constant or builtin SV has no bind, so it is not a row Rows() reports, but its number
        // is still taken.
        using var doc = JsonDocument.Parse(json, DocOptions);
        if (doc.RootElement.TryGetProperty("statusVariables", out var svs) && svs.ValueKind == JsonValueKind.Array)
        {
            foreach (var sv in svs.EnumerateArray())
            {
                if (sv.ValueKind == JsonValueKind.Object && sv.TryGetProperty("svid", out var id) &&
                    id.TryGetInt64(out var n))
                    svids.Add(NormId(n));
            }
        }
        return new Have(keys, svids);
    }

    private static IEnumerable<SecsProfileRow> Rows(string json)
    {
        using var doc = JsonDocument.Parse(json, DocOptions);
        var root = doc.RootElement;
        var rows = new List<SecsProfileRow>();

        foreach (var e in Elements(root, "statusVariables"))
        {
            if (e.TryGetProperty("bind", out var bind) && bind.ValueKind == JsonValueKind.String)
                rows.Add(new SecsProfileRow("statusVariables", bind.GetString()!, Number(e, "svid"), e.GetRawText()));
        }
        foreach (var e in Elements(root, "alarms"))
        {
            long alid = Number(e, "alid");
            if (alid > 0) rows.Add(new SecsProfileRow("alarms", Id(alid), alid, e.GetRawText()));
        }
        foreach (var e in Elements(root, "events"))
        {
            long ceid = Number(e, "ceid");
            if (ceid > 0) rows.Add(new SecsProfileRow("events", Id(ceid), ceid, e.GetRawText()));
        }
        if (root.TryGetProperty("remoteCommands", out var commands) && commands.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in commands.EnumerateObject())
                rows.Add(new SecsProfileRow("remoteCommands", p.Name, 0,
                    JsonSerializer.Serialize(p.Name) + ": " + p.Value.GetRawText()));
        }
        return rows;
    }

    private static IEnumerable<JsonElement> Elements(JsonElement root, string section) =>
        root.TryGetProperty(section, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList()
            : Enumerable.Empty<JsonElement>();

    private static long Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;

    private static string Id(long n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>Keys compared as the program compares them: a bind name and a remote command
    /// ignoring case, an id with its chamber digits set to 00 (a site may have stamped its own
    /// template).</summary>
    private static string Norm(string section, string key) => section switch
    {
        "events" or "alarms" => Id(NormId(long.Parse(key, CultureInfo.InvariantCulture))),
        _ => key.ToUpperInvariant(),
    };

    private static long NormId(long id) => SecsChamberCoding.Unstamp(id);

    /// <summary>"3 statusVariables, 1 remoteCommands" — the per-section count the tab and the
    /// log both show.</summary>
    public static string Describe(IEnumerable<SecsProfileRow> rows) =>
        string.Join(", ", rows.GroupBy(r => r.Section).Select(g => $"{g.Count()} {g.Key}"));

    // ---- where things are in the text --------------------------------------

    /// <summary>
    /// From just after a section's last element: the end of that line (past a trailing comment),
    /// or the closing bracket if it shares the line, and whether a comma — allowed as trailing —
    /// already follows the element, in which case adding another would make two.
    /// </summary>
    private static (int Position, bool HasComma) AfterLastElement(byte[] json, int from)
    {
        int i = from;
        bool comma = false;
        while (i < json.Length)
        {
            byte c = json[i];
            if (c == (byte)' ' || c == (byte)'\t') { i++; continue; }
            if (c == (byte)',') { comma = true; i++; continue; }
            if (c == (byte)'/' && i + 1 < json.Length && json[i + 1] == (byte)'/')
            {
                while (i < json.Length && json[i] != (byte)'\n' && json[i] != (byte)'\r') i++;
                break;
            }
            if (c == (byte)'/' && i + 1 < json.Length && json[i + 1] == (byte)'*')
            {
                int close = IndexOf(json, "*/"u8, i + 2);
                i = close < 0 ? json.Length : close + 2;
                continue;
            }
            break;   // end of line, the closing bracket, or anything else: insert here
        }
        return (i, comma);
    }

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle, int from)
    {
        int at = haystack.AsSpan(from).IndexOf(needle);
        return at < 0 ? -1 : from + at;
    }

    private sealed record SectionLayout(int ContentStart, int LastElementEnd);

    private sealed record Layout(
        Dictionary<string, SectionLayout> Sections, HashSet<string> RootProperties, int RootLastValueEnd);

    /// <summary>
    /// Byte offsets the insertions need: just inside each section's opening bracket, just after
    /// its last element (-1 when empty), and just after the root's last property value. Taken
    /// with the reader rather than by searching the text, so a bracket inside a comment or a
    /// string cannot be mistaken for structure.
    /// </summary>
    private static Layout Scan(byte[] json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        var sections = new Dictionary<string, SectionLayout>();
        var rootProperties = new HashSet<string>(StringComparer.Ordinal);
        string? property = null, open = null;
        int contentStart = -1, lastEnd = -1, rootLastEnd = -1;

        while (reader.Read())
        {
            int depth = reader.CurrentDepth;
            var token = reader.TokenType;
            int end = (int)reader.BytesConsumed;

            if (depth == 1 && token == JsonTokenType.PropertyName)
            {
                property = reader.GetString();
                if (property is not null) rootProperties.Add(property);
                continue;
            }
            if (depth == 1 && token is JsonTokenType.StartArray or JsonTokenType.StartObject &&
                property is not null && Sections.Contains(property))
            {
                open = property;
                contentStart = (int)reader.TokenStartIndex + 1;
                lastEnd = -1;
                continue;
            }
            if (open is not null && depth == 2 && token is not (JsonTokenType.PropertyName
                    or JsonTokenType.StartArray or JsonTokenType.StartObject))
            {
                lastEnd = end;   // the end of an element (or of a remote command's value)
            }
            if (depth == 1 && token is not (JsonTokenType.PropertyName
                    or JsonTokenType.StartArray or JsonTokenType.StartObject))
            {
                rootLastEnd = end;   // a root property's value ended here
                if (open is not null)
                {
                    sections[open] = new SectionLayout(contentStart, lastEnd);
                    open = null;
                }
            }
        }
        return new Layout(sections, rootProperties, rootLastEnd);
    }
}
