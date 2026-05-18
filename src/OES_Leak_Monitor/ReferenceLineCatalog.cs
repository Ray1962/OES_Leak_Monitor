using System;
using System.Collections.Generic;
using System.Linq;

namespace OES_Leak_Monitor;

/// <summary>A named, ready-to-use reference (denominator) line for actinometric ratios.</summary>
public sealed class ReferenceLinePreset
{
    private readonly Func<LineRegion> _factory;

    public ReferenceLinePreset(string name, string description, Func<LineRegion> factory)
    {
        Name = name;
        Description = description;
        _factory = factory;
    }

    /// <summary>Display name; also the match key stored on captured baselines.</summary>
    public string Name { get; }

    public string Description { get; }

    /// <summary>A fresh <see cref="LineRegion"/> for this reference. Its
    /// <see cref="LineRegion.Label"/> equals <see cref="Name"/>.</summary>
    public LineRegion CreateRegion() => _factory();
}

/// <summary>
/// Preset reference lines a ratio's denominator can be switched to. The default is
/// N₂ 337.1; the others let the operator pick a reference better matched (in excitation
/// threshold or wavelength proximity) to a given signal line.
/// </summary>
public static class ReferenceLineCatalog
{
    public static IReadOnlyList<ReferenceLinePreset> All { get; } = new[]
    {
        new ReferenceLinePreset("N₂ 337.1",
            "N₂ second-positive band — stable carrier-gas reference (default).",
            () => new LineRegion
            {
                Label = "N₂ 337.1", CenterNm = 337.1, HalfWidthNm = 1.0,
                BaselineGapNm = 1.0, BaselineWidthNm = 1.0, Mode = LineExtractMode.Integral,
            }),
        new ReferenceLinePreset("N₂ 662.4",
            "N₂ first-positive band (NIR) — pair with NIR signal lines for window-fogging immunity.",
            () => new LineRegion
            {
                Label = "N₂ 662.4", CenterNm = 662.4, HalfWidthNm = 1.0,
                BaselineGapNm = 1.2, BaselineWidthNm = 0.8, Mode = LineExtractMode.Integral,
            }),
        new ReferenceLinePreset("Hα 656.3",
            "Hydrogen Balmer-α — hydrogen-supply reference; note it drifts with SiH₄ flow.",
            () => new LineRegion
            {
                Label = "Hα 656.3", CenterNm = 656.3, HalfWidthNm = 0.5,
                BaselineGapNm = 0.8, BaselineWidthNm = 0.6, Mode = LineExtractMode.PeakHeight,
            }),
        new ReferenceLinePreset("Ar 750.4",
            "Ar line — leak-borne actinometer; note it overlaps an N₂ band near 750.4 nm.",
            () => new LineRegion
            {
                Label = "Ar 750.4", CenterNm = 750.4, HalfWidthNm = 0.5,
                BaselineGapNm = 1.0, BaselineWidthNm = 1.0, Mode = LineExtractMode.PeakHeight,
            }),
        new ReferenceLinePreset("Ar 811.5",
            "Ar 811.5 nm — classic actinometry line; present only when Ar is in the gas or leak.",
            () => new LineRegion
            {
                Label = "Ar 811.5", CenterNm = 811.5, HalfWidthNm = 0.5,
                BaselineGapNm = 1.0, BaselineWidthNm = 1.0, Mode = LineExtractMode.PeakHeight,
            }),
    };

    public static IReadOnlyList<string> Names { get; } = All.Select(p => p.Name).ToList();

    public static ReferenceLinePreset? FindByName(string? name) =>
        name is null ? null : All.FirstOrDefault(p => p.Name == name);
}
