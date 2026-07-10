using System;
using System.Collections.Generic;

namespace OES_Leak_Monitor;

/// <summary>
/// One catalog-level wavelength-drift correction: an additive offset (nm) for a specific
/// <c>(species, wavelength)</c> emission line. Stored as a sparse overlay in
/// <c>settings.json</c> — only corrected lines appear; every other line uses offset 0.
/// Because the key is the catalog <c>(species, wavelength)</c>, a single entry re-aligns
/// <em>every</em> ratio (signal or reference) that uses that line, without re-picking the
/// catalog entry. See <see cref="WavelengthCalibration"/> for how it is applied.
/// </summary>
public sealed class WavelengthCorrection
{
    /// <summary>Catalog species (ASCII, e.g. "O", "N2", "OH") the correction applies to.</summary>
    public string Species { get; set; } = "";

    /// <summary>Catalog wavelength of the line, nm — matched against a region's <c>CenterNm</c>.</summary>
    public double WavelengthNm { get; set; }

    /// <summary>Additive drift correction, nm. Effective center = catalog wavelength + this.</summary>
    public double OffsetNm { get; set; }

    public WavelengthCorrection Clone() => (WavelengthCorrection)MemberwiseClone();
}

/// <summary>
/// Applies the catalog-level <see cref="WavelengthCorrection"/> overlay to line regions when the
/// engine builds its monitors. Pure — no engine or UI dependencies. The correction shifts a
/// region's <see cref="LineRegion.CenterNm"/> by the matched offset; the persisted region keeps
/// the raw catalog wavelength, so corrections never leak back into <c>settings.json</c>.
/// </summary>
public static class WavelengthCalibration
{
    // Reference-line presets (ReferenceLineCatalog) label their regions with pretty, subscripted
    // species ("N₂ 337.1", "Hα 656.3") whereas SpectralLineCatalog / correction entries use plain
    // ASCII ("N2", "H"). Fold the pretty forms back so a reference line still matches a correction
    // keyed on the catalog species.
    private static readonly Dictionary<string, string> SpeciesAlias = new()
    {
        ["N₂"] = "N2",
        ["Hα"] = "H",
    };

    /// <summary>Builds a lookup keyed on <c>(normalized species, rounded wavelength)</c> → offset.
    /// Zero-offset and blank-species entries are skipped; later duplicate keys win.</summary>
    public static Dictionary<(string Species, double Wl), double> Build(
        IEnumerable<WavelengthCorrection>? corrections)
    {
        var map = new Dictionary<(string, double), double>();
        if (corrections is null) return map;
        foreach (var c in corrections)
        {
            if (c is null || c.OffsetNm == 0.0 || string.IsNullOrWhiteSpace(c.Species)) continue;
            map[(NormalizeSpecies(c.Species.Trim()), Math.Round(c.WavelengthNm, 3))] = c.OffsetNm;
        }
        return map;
    }

    /// <summary>Returns a corrected clone of <paramref name="region"/>: its <c>CenterNm</c> shifted
    /// by the offset matching <c>(region species, region CenterNm)</c>, or an unshifted clone when
    /// no correction applies.</summary>
    public static LineRegion Correct(LineRegion region,
        IReadOnlyDictionary<(string Species, double Wl), double> lookup)
    {
        var clone = region.Clone();
        if (lookup.Count == 0) return clone;
        string species = SpeciesOf(region.Label);
        if (species.Length > 0 &&
            lookup.TryGetValue((species, Math.Round(region.CenterNm, 3)), out double offset))
        {
            clone.CenterNm += offset;
        }
        return clone;
    }

    /// <summary>Returns a corrected clone of <paramref name="def"/> with both its numerator and
    /// denominator regions corrected. The original definition is left untouched.</summary>
    public static RatioDefinition Correct(RatioDefinition def,
        IReadOnlyDictionary<(string Species, double Wl), double> lookup)
    {
        var clone = def.Clone();
        clone.Numerator = Correct(def.Numerator, lookup);
        clone.Denominator = Correct(def.Denominator, lookup);
        return clone;
    }

    /// <summary>Leading whitespace-delimited token of a region label ("O 777.2" → "O",
    /// "N₂ 337.1" → "N2"), normalized to a catalog species. Empty when the label has none.</summary>
    public static string SpeciesOf(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "";
        int sp = label.IndexOf(' ');
        string token = sp < 0 ? label : label.Substring(0, sp);
        return NormalizeSpecies(token);
    }

    private static string NormalizeSpecies(string species) =>
        SpeciesAlias.TryGetValue(species, out var ascii) ? ascii : species;
}
