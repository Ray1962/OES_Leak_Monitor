using System;
using System.Collections.Generic;
using System.Linq;

namespace OES_Leak_Monitor;

/// <summary>
/// What one ratio slot measures — the identity half of the slot SVIDs (offsets 16–25,
/// Satellite spec §1.4(b)). Built from the engine's <em>wavelength-corrected</em> definition, so
/// the wavelengths are the ones actually extracted, not the catalog values in
/// <c>settings.json</c>. Thresholds and smoothing are deliberately absent: retuning them does not
/// change what the slot measures, and a host that keyed its history on this identity must not be
/// told to re-read it for a threshold edit.
/// <para>Not <see cref="RatioDefinition.MeasuresSameAs"/>, and deliberately so: that is the
/// engine's fingerprint for carrying a latch or a baseline across a rebuild, and it compares
/// <c>ProcessClass</c> and <c>Role</c>, which no slot offset carries. This is what the host can
/// read, so it also holds the name and the enable switch, which a host labels its history by.
/// Equality is the record's own, over exactly the fields below.</para>
/// </summary>
public sealed record RatioSlot(
    string Key,
    string DisplayName,
    bool Enabled,
    MonitorMode MonitorMode,
    bool ValueHasPedestal,
    LineExtractMode ExtractMode,
    double SignalWavelengthNm,
    double ReferenceWavelengthNm,
    string SignalSpecies,
    string ReferenceSpecies)
{
    /// <summary>The slot for <paramref name="corrected"/>. In absolute-intensity mode the
    /// reference line takes no part in the reading, so the slot names none (0 nm, "").</summary>
    public static RatioSlot From(RatioDefinition corrected)
    {
        bool absolute = corrected.MonitorMode == MonitorMode.AbsoluteIntensity;
        return new RatioSlot(
            corrected.Key ?? "",
            corrected.DisplayName ?? "",
            corrected.Enabled,
            corrected.MonitorMode,
            corrected.ValueHasPedestal,
            corrected.Numerator.Mode,
            corrected.Numerator.CenterNm,
            absolute ? 0.0 : corrected.Denominator.CenterNm,
            WavelengthCalibration.SpeciesOf(corrected.Numerator.Label),
            absolute ? "" : WavelengthCalibration.SpeciesOf(corrected.Denominator.Label));
    }
}

/// <summary>
/// The engine's ratio list as the host sees it: slot N is row N of Ratio Setup. Immutable, so the
/// SECS thread can hold one reference and read it without the frame lock — the same arrangement
/// as <see cref="ToolEventStatus"/>. Only <see cref="SlotCount"/> slots exist on the wire; a slot
/// past the end of the list is empty (<see cref="Slot"/> returns null).
/// </summary>
public sealed class RatioSlotTable
{
    /// <summary>Slots the SVID range 101–400 carries — the Ratio Setup maximum.</summary>
    public const int SlotCount = RatioSetupViewModel.MaxRatios;

    public static readonly RatioSlotTable Empty = new(Array.Empty<RatioSlot>());

    private readonly RatioSlot[] _slots;

    public RatioSlotTable(IEnumerable<RatioSlot> slots) => _slots = slots.ToArray();

    /// <summary>Every slot in list order, including any past <see cref="SlotCount"/>.</summary>
    public IReadOnlyList<RatioSlot> Slots => _slots;

    /// <summary>Slot <paramref name="n"/> (1-based), or null for an empty slot.</summary>
    public RatioSlot? Slot(int n) => n >= 1 && n <= _slots.Length ? _slots[n - 1] : null;

    /// <summary>The 1-based slot holding <paramref name="key"/>, or 0 if no slot does.</summary>
    public int SlotOf(string key) => Array.FindIndex(_slots, s => s.Key == key) + 1;

    /// <summary>
    /// True when all <see cref="SlotCount"/> slots read the same identity in both tables, empty
    /// slots included — i.e. a host need not re-read offsets 16–25. False means CEID 507.
    /// </summary>
    public bool SameIdentityAs(RatioSlotTable? other)
    {
        if (other is null) return false;
        for (int n = 1; n <= SlotCount; n++)
        {
            if (!Equals(Slot(n), other.Slot(n))) return false;
        }
        return true;
    }
}
