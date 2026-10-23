using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace OES_Leak_Monitor;

/// <summary>
/// The ratio slot SVIDs (Satellite spec §1.4(b)): <see cref="RatioSlotTable.SlotCount"/> slots
/// of <see cref="Fields"/>, at <c>VID = 100 + (N−1)×30 + offset</c>. One table, read by both the
/// shipped profile (<see cref="SecsProfileTemplate"/>) and the bindings (<see cref="SecsBridge"/>),
/// so the row a host is offered and the value behind it cannot be numbered two different ways.
/// Offsets 26–30 are reserved.
/// </summary>
public static class SecsRatioSlots
{
    /// <summary>One slot offset: its number, the field part of its bind name, and how the
    /// profile describes it.</summary>
    public sealed record Field(int Offset, string Bind, string Name, string Units, string Format);

    // 01–15 are per-frame values, 16–25 the slot's identity. Formats per the specification:
    // enumerations and flags U4, names A, everything else F4.
    public static readonly IReadOnlyList<Field> Fields = new Field[]
    {
        new(1,  "monitoredValue",      "monitored value",      "",         "F4"),
        new(2,  "smoothedValue",       "smoothed value",       "",         "F4"),
        new(3,  "percentOfBaseline",   "percent of baseline",  "% or sigma", "F4"),
        new(4,  "slope",               "slope",                "%/min or sigma/min", "F4"),
        new(5,  "state",               "state",                "",         "U4"),
        new(6,  "signalSnr",           "signal SNR",           "",         "F4"),
        new(7,  "referenceSnr",        "reference SNR",        "",         "F4"),
        new(8,  "signalIntensity",     "signal intensity",     "counts",   "F4"),
        new(9,  "referenceIntensity",  "reference intensity",  "counts",   "F4"),
        new(10, "baselineMean",        "baseline mean",        "",         "F4"),
        new(11, "baselineSigma",       "baseline sigma",       "",         "F4"),
        new(12, "warnThreshold",       "warn threshold",       "",         "F4"),
        new(13, "alarmThreshold",      "alarm threshold",      "",         "F4"),
        new(14, "leakRate",            "leak rate",            "mbar-L/s", "F4"),
        new(15, "usedInFusion",        "used in fusion",       "",         "U4"),
        new(16, "enabled",             "enabled",              "",         "U4"),
        new(17, "monitorMode",         "monitor mode",         "",         "U4"),
        new(18, "valueSemantics",      "value semantics",      "",         "U4"),
        new(19, "extractionMode",      "extraction mode",      "",         "U4"),
        new(20, "signalWavelength",    "signal wavelength",    "nm",       "F4"),
        new(21, "referenceWavelength", "reference wavelength", "nm",       "F4"),
        new(22, "displayName",         "display name",         "",         "A"),
        new(23, "signalSpecies",       "signal species",       "",         "A"),
        new(24, "referenceSpecies",    "reference species",    "",         "A"),
        new(25, "key",                 "key",                  "",         "A"),
    };

    /// <summary>The <c>vvv</c> of slot <paramref name="slot"/> (1-based) at <paramref name="offset"/>.</summary>
    public static int Vid(int slot, int offset) => 100 + (slot - 1) * 30 + offset;

    /// <summary>The bind name the profile and the program join on, e.g. <c>oes.slot3.state</c>.</summary>
    public static string BindName(int slot, Field field) => $"oes.slot{slot}.{field.Bind}";

    /// <summary>Every slot bind name, slot by slot, in offset order.</summary>
    public static IEnumerable<string> BindNames() =>
        from n in Enumerable.Range(1, RatioSlotTable.SlotCount)
        from f in Fields
        select BindName(n, f);

    /// <summary>The template's slot rows (cc = 00), each line a JSON object followed by a comma
    /// except the last — written out by program rather than by hand, 250 rows being 250 chances
    /// to mistype a digit.</summary>
    internal static string ProfileRows()
    {
        var sb = new StringBuilder();
        for (int n = 1; n <= RatioSlotTable.SlotCount; n++)
        {
            sb.Append($"\n    // Ratio {n}: row {n} of Ratio Setup.\n");
            foreach (var f in Fields)
            {
                bool last = n == RatioSlotTable.SlotCount && f == Fields[^1];
                uint svid = SecsChamberCoding.Svid(0, Vid(n, f.Offset));
                sb.Append(string.Create(CultureInfo.InvariantCulture,
                    $"    {{ \"svid\": {svid}, \"name\": \"Ratio{n} {f.Name}\", \"units\": \"{f.Units}\", " +
                    $"\"format\": \"{f.Format}\", \"bind\": \"{BindName(n, f)}\" }}{(last ? "" : ",")}\n"));
            }
        }
        return sb.ToString();
    }
}
