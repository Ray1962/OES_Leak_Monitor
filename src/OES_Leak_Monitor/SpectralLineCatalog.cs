using System.Collections.Generic;
using System.Linq;

namespace OES_Leak_Monitor;

/// <summary>One reference emission line: a species label and its wavelength in nm.</summary>
public readonly record struct SpectralLine(string Species, double WavelengthNm);

/// <summary>
/// Static catalog of OES emission lines (atoms and molecular band heads) usable for
/// leak-monitoring actinometry. Populates line-picker UI; the v1 ratios pick their
/// lines from here. Wavelengths for molecular species are approximate band heads.
/// </summary>
public static class SpectralLineCatalog
{
    public static IReadOnlyList<SpectralLine> All { get; } = Build();

    /// <summary>Distinct species names, in catalog order.</summary>
    public static IReadOnlyList<string> Species { get; } =
        All.Select(l => l.Species).Distinct().ToList();

    /// <summary>Lines of one species, ascending by wavelength.</summary>
    public static IEnumerable<SpectralLine> ForSpecies(string species) =>
        All.Where(l => l.Species == species).OrderBy(l => l.WavelengthNm);

    private static IReadOnlyList<SpectralLine> Build()
    {
        var list = new List<SpectralLine>();
        void Add(string sp, params double[] wls)
        {
            foreach (var w in wls) list.Add(new SpectralLine(sp, w));
        }

        Add("Al", 308.2, 309.3, 394.4, 396.1, 396.2);
        Add("AlCl", 261.4, 264.8, 268.6);
        Add("Ar", 415.9, 451.1, 484.8, 549.6, 603.2, 696.5, 706.6, 750.4, 811.5);
        Add("As", 235);
        Add("Au", 267.6, 274.9, 312.3, 327.4);
        Add("B", 249.8);
        Add("BCl", 265.5, 272.2, 278.4);
        Add("Br", 470.5, 478.7, 481.7);
        Add("C", 604.6);
        Add("C2", 516.5);
        Add("CCl", 258, 277.9, 278.8, 307, 460);
        Add("CF", 240.4, 247.5, 255.8, 292.1);
        Add("CF2", 248.8, 251.9, 255.1, 259.5, 263, 271.1, 275, 321.4);
        Add("CH", 431.4);
        Add("Cl", 256.1, 725.7, 741.4, 754.7, 771.8, 774.5);
        Add("Cl2", 256, 308.9);
        Add("CN", 289.8, 304.2, 359, 386.2, 387, 387.1, 388.3, 418.1, 419.7, 421.6, 585, 646.7, 692.6, 787.3);
        Add("CO", 209, 219.7, 223.8, 231.2, 233.8, 239.3, 259.8, 283.3, 292.5, 302.8,
                  313.4, 313.8, 325.3, 451.1, 482.5, 483.5, 519, 519.8, 561, 608);
        Add("Co", 271.2, 331.2, 349.2, 369.9, 662);
        Add("CO2+", 288.4, 289.8);
        Add("Cr", 359.3, 360.5, 425.4, 520.8);
        Add("Cu", 324.8, 327.4);
        Add("F", 624, 634.9, 641.4, 677.4, 683.4, 685.6, 687, 690.2, 691, 696.6, 703.7,
                 703.8, 712.8, 720.2, 733.2, 739.9, 742.6, 751.5, 755.2, 757.3, 760.7, 775.5, 780);
        Add("Fe", 242.8, 372, 373.5);
        Add("Ga", 294.4, 403.3, 417.2);
        Add("Ge", 265.1, 271, 275.5, 303.9);
        Add("H", 434, 486.1, 656.3, 656.5);
        Add("He", 294.5, 318.8, 344.8, 361.4, 363.4, 370.5, 382, 386.8, 388.9, 396.5,
                  402.6, 414.4, 438.8, 443.8, 447.2, 471.3, 492.2, 501.6, 504.8, 587.6, 667.8, 706.8, 728.1);
        Add("Hg", 253.7, 312.6, 313.2, 365, 365.5, 366.3, 404.7, 435.8, 546.1, 577, 579.1);
        Add("In", 325.6, 410, 451);
        Add("Mo", 313.3, 320.9, 379.8, 386.4, 390.3);
        Add("N", 674);
        Add("N2", 281.4, 282, 295.3, 296.2, 297.7, 310.4, 311.7, 313.6, 315.9, 326.8,
                  330.9, 337.1, 350.1, 353.7, 357.5, 357.7, 364.2, 367.2, 371.1, 375.5,
                  380.5, 385.8, 389.5, 391.4, 394.3, 399.8, 404.1, 405.9, 409.5, 414.2,
                  419, 420.1, 427, 427.8, 434.4, 559.3, 563.3, 575.5, 580.4, 585.4, 590.6,
                  595.9, 601.4, 607, 612.7, 618.5, 632.3, 639.5, 646.9, 654.5, 662.4, 670.5,
                  678.9, 687.5, 700, 716.5, 727.3, 738.7, 750.4, 760, 762.6, 775.3, 789.6, 820);
        Add("NH", 336);
        Add("Ni", 232, 301.2, 341.5, 346.2);
        Add("NO", 237, 247.9, 255.9, 259.6, 268, 272.2, 286, 288.5, 289.3, 303.5, 304.3,
                  319.8, 320.7, 337.7, 338.6);
        Add("O", 436.8, 497, 502, 533, 543.7, 615.8, 645.6, 725.4, 777.2, 844.7);
        Add("OH", 281.1, 306.4, 308.9);
        Add("Pt", 265.9, 270.2, 293, 306.5);
        Add("S", 469.5);
        Add("Si", 251.6, 252.4, 288.2);
        Add("SiCl", 281, 282.3, 286.6, 287.1);
        Add("SiF", 440.1, 777, 777.9);
        Add("SiF2", 390.2, 400.9);
        Add("SiN", 405.1, 408.7, 411.6, 412.7, 420.4, 440.7);
        Add("SiO", 230, 234.4, 238.8, 241.4, 248.7);
        Add("Ta", 265.3, 271.5);
        Add("Ti", 334.9, 365.3);
        Add("W", 400.9, 407.4, 429.5);
        Add("Zn", 213.9, 328.2, 330.3, 334.5, 468, 472.2, 481.1, 636.2);
        Add("Zr", 352, 354.8, 360.1, 468.8, 471, 474, 477.2, 481.6);

        return list;
    }
}
