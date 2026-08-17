using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;

namespace OES_Leak_Monitor;

/// <summary>
/// Rewrites a device profile's ids for the chamber this OES actually watches.
/// <para/>
/// Satellite encodes what a number means into the number itself
/// (specification §1, §5, §6):
/// <code>
/// SVID = 1 + cc + ss + aa + vvv   (10 digits)   e.g. 1 02 27 00 001 → 1022700001
/// ALID = 1 + cc + ss + nnn        ( 8 digits)   e.g. 1 02 27 002    →   10227002
/// CEID = 1 + cc + ss + nnn        ( 8 digits)   nnn from 501
/// </code>
/// so every id in the profile carries the chamber code. The profile on disk is written
/// once with <c>cc = 00</c> and this class stamps the configured chamber into it at
/// start-up — the alternative, asking whoever installs the tool to hand-edit thirty
/// ten-digit numbers, gets one of them wrong and the wrong one is not obvious.
/// <para/>
/// Pure: JSON text in, JSON text out. No file system, no SECS types.
/// </summary>
public static class SecsChamberCoding
{
    /// <summary>Sensor type of the OES Leak Monitor (specification §1.2).</summary>
    public const int SensorCode = 27;

    /// <summary>
    /// Slit-valve field. The OES watches the plasma emission directly and conditions no
    /// signal through a slit valve, so it is fixed at 00 (specification §1.4, ss=27).
    /// </summary>
    public const int SlitValveCode = 0;

    /// <summary>
    /// Chamber codes the specification defines (§1.1). 00 exists in the table (it means
    /// "no slit-valve signal collected") but is not a chamber, so it is not offered here —
    /// it is what the profile template ships with, precisely because it is not a real one.
    /// </summary>
    public static readonly IReadOnlyList<int> ValidChamberCodes = new[]
    {
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        21, 22, 23, 24, 25,
        31, 32, 33, 34,
    };

    /// <summary>Human-readable chamber name for a code, or "" when the code is not in the table.</summary>
    public static string ChamberName(int code) => code switch
    {
        1 => "Ch_1", 2 => "Ch_2", 3 => "Ch_3", 4 => "Ch_4", 5 => "Ch_5",
        6 => "Ch_A / VIA_1", 7 => "Ch_E", 8 => "Ch_F", 9 => "Ch_B",
        10 => "X'fer / Buffer 2", 11 => "Buffer / Buffer 1", 12 => "Ch_C", 13 => "Ch_D",
        14 => "LLA", 15 => "LLB",
        21 => "X'fer Viewport 1", 22 => "X'fer Viewport 2", 23 => "X'fer Viewport 3",
        24 => "X'fer Viewport 4", 25 => "X'fer Viewport 5",
        31 => "Buffer Viewport 1", 32 => "Buffer Viewport 2", 33 => "Buffer Viewport 3",
        34 => "Buffer Viewport 4",
        _ => "",
    };

    /// <summary>Whether <paramref name="code"/> is a chamber the specification names.</summary>
    public static bool IsValidChamber(int code) => ChamberName(code).Length > 0;

    /// <summary>Builds an SVID for this chamber and measurement item (vvv).</summary>
    public static uint Svid(int chamber, int vid) =>
        (uint)(1_000_000_000 + chamber * 10_000_000 + SensorCode * 100_000 + SlitValveCode * 1_000 + vid);

    /// <summary>Builds an ALID or CEID for this chamber and sequence number (nnn).</summary>
    public static uint EventId(int chamber, int nnn) =>
        (uint)(10_000_000 + chamber * 100_000 + SensorCode * 1_000 + nnn);

    /// <summary>
    /// Returns <paramref name="json"/> with every <c>svid</c>, <c>alid</c> and <c>ceid</c>
    /// re-stamped for <paramref name="chamber"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The chamber is not one the specification
    /// names, or the file holds an id that is not an OES Leak Monitor id — a mistyped digit
    /// in a hand-edited profile, which would otherwise reach the host as a plausible-looking
    /// number belonging to some other sensor.</exception>
    public static string ApplyChamber(string json, int chamber)
    {
        if (!IsValidChamber(chamber))
        {
            throw new InvalidOperationException(
                $"chamber code {chamber:00} is not one the specification defines (§1.1). " +
                "Set a chamber on the SECS tab before enabling the interface.");
        }

        var root = JsonNode.Parse(json, documentOptions: new System.Text.Json.JsonDocumentOptions
        {
            CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) ?? throw new InvalidOperationException("the profile file is empty");

        Rewrite(root["statusVariables"], "svid", 10, chamber);
        Rewrite(root["alarms"], "alid", 8, chamber);
        Rewrite(root["alarms"], "ceid", 8, chamber);       // harmless if absent
        Rewrite(root["events"], "ceid", 8, chamber);

        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static void Rewrite(JsonNode? array, string field, int digits, int chamber)
    {
        if (array is not JsonArray entries)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (entry is not JsonObject obj || !obj.TryGetPropertyValue(field, out var node) || node is null)
            {
                continue;
            }
            // A constant SV (one with "value" and no "bind") may legitimately carry a
            // site-chosen id that is not in the Satellite scheme — a machine id, say.
            // Leave anything that is not shaped like one of ours alone rather than
            // corrupting it, but insist that anything that *is* shaped like one is valid.
            var original = node.GetValue<long>();
            obj[field] = Restamp(original, digits, chamber, field);
        }
    }

    private static long Restamp(long id, int digits, int chamber, string field)
    {
        var text = id.ToString(CultureInfo.InvariantCulture);
        if (text.Length != digits || text[0] != '1')
        {
            return id;                                   // not a Satellite id; leave it
        }

        // digits 2-3 are cc, 4-5 are ss. Everything after stays as written.
        var ss = int.Parse(text.Substring(3, 2), CultureInfo.InvariantCulture);
        if (ss != SensorCode)
        {
            throw new InvalidOperationException(
                $"profile {field} {id} has sensor code {ss:00}, not {SensorCode} (OES Leak Monitor). " +
                "Check the digits — an id belonging to another sensor type would be read by the host as that sensor.");
        }

        if (digits == 10)
        {
            var aa = int.Parse(text.Substring(5, 2), CultureInfo.InvariantCulture);
            if (aa != SlitValveCode)
            {
                throw new InvalidOperationException(
                    $"profile {field} {id} has slit-valve code {aa:00}, not 00. " +
                    "The OES observes the plasma directly; specification §1.4 fixes aa at 00 for ss=27.");
            }
        }

        return long.Parse(
            "1" + chamber.ToString("00", CultureInfo.InvariantCulture) + text.Substring(3),
            CultureInfo.InvariantCulture);
    }
}
