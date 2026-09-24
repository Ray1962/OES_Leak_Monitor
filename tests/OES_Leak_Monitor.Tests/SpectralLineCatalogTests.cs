using System.Collections.Generic;
using System.Linq;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// The built-in lines a site configuration names.
///
/// <para>What this guards: the Ratio Setup tab matches a saved region back to the catalog by the
/// species in its label and then the nearest wavelength, with no tolerance. The site ratio set
/// divides by <c>CO 329.6</c>, and while the catalog's nearest CO line was 325.3 the picker showed
/// that denominator as CO 325.3 — re-selecting it, or so much as opening the combo and picking
/// what it already appeared to hold, re-pointed the ratio 4 nm away and relabelled it, which drops
/// its Golden Run baseline (<c>ReferenceLabel</c> no longer matches).</para>
/// </summary>
public class SpectralLineCatalogTests
{
    /// <summary>The denominator of R_N2CO_A / _B / _C, exactly as leak-monitor-plan §4.3 writes it.</summary>
    private static LineRegion SiteCo329() => new()
    {
        Label = "CO 329.6", CenterNm = 329.60, HalfWidthNm = 1.06,
        BaselineGapNm = 2.10, BaselineWidthNm = 1.10,
        Mode = LineExtractMode.PeakHeight, PeakSearchHalfWidthNm = 0.0,
    };

    private static RatioDefinition SiteN2Co() => new()
    {
        Key = "R_N2CO_C", DisplayName = "N2 337 / CO 330 (C)", Enabled = true,
        MonitorMode = MonitorMode.Ratio, ProcessClass = "C",
        Numerator = new LineRegion
        {
            Label = "N2 337.1", CenterNm = 337.40, HalfWidthNm = 0.70,
            BaselineGapNm = 1.30, BaselineWidthNm = 1.10,
            Mode = LineExtractMode.PeakHeight, PeakSearchHalfWidthNm = 0.0,
        },
        Denominator = SiteCo329(),
    };

    [Fact]
    public void Catalog_carries_CO_329_6()
    {
        Assert.Contains(new SpectralLine("CO", 329.6), SpectralLineCatalog.BuiltIn);
    }

    [Fact]
    public void Site_CO_329_6_denominator_resolves_to_itself_in_Ratio_Setup()
    {
        var lines = SpectralLineCatalog.All.Select(l => new SpectralLineOption(l)).ToList();

        var edit = new RatioEditViewModel(SiteN2Co(), lines);

        Assert.Equal(new SpectralLine("CO", 329.6), edit.ReferenceLine.Line);
    }

    [Fact]
    public void Saving_the_site_ratio_keeps_its_denominator()
    {
        var lines = SpectralLineCatalog.All.Select(l => new SpectralLineOption(l)).ToList();
        var edit = new RatioEditViewModel(SiteN2Co(), lines);

        // Re-selecting the line the combo shows is what an operator does without meaning to.
        edit.ReferenceLine = edit.ReferenceLine;
        var saved = edit.ToDefinition().Denominator;

        Assert.Equal("CO 329.6", saved.Label);
        Assert.Equal(329.60, saved.CenterNm, 3);
    }
}
