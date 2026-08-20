using System;

namespace OES_Leak_Monitor;

/// <summary>
/// One frame's verdict for one ratio: what the two lines read, what the monitored value is, and
/// whether the frame is fit to feed a Golden Run baseline.
/// </summary>
public readonly record struct RatioFrameSample(
    LineMeasurement Numerator,
    LineMeasurement Denominator,
    double Value,
    bool GateOpen,
    bool PlasmaPresent,
    bool LowSnr)
{
    /// <summary>The monitored line did not extract — outside the axis, or an empty window.</summary>
    public bool NumeratorMissing => double.IsNaN(Numerator.Value);

    /// <summary>Ratio mode only: the reference line is absent or non-positive, so nothing can be
    /// divided by it. Always false in absolute mode, where the reference takes no part.</summary>
    public bool ReferenceMissing { get; init; }

    /// <summary>A value came out of this frame at all — before the SNR floor is applied.</summary>
    public bool Evaluable => PlasmaPresent && !double.IsNaN(Value);

    /// <summary>This frame may feed a baseline: evaluable and clear of the SNR floor.</summary>
    public bool Accepted => Evaluable && !LowSnr;
}

/// <summary>
/// The single definition of "what does this frame say about this ratio, and may it feed a
/// baseline". Both the live Golden Run capture (<see cref="LeakMonitorEngine"/>) and the offline
/// builder (<see cref="BaselineBuilder"/>) go through here.
///
/// <para>It exists because the alternative is two copies. A baseline built offline is stored in
/// the same field, judged by the same thresholds and compared against runs captured live — so a
/// second implementation of "does this frame count" would not be a small divergence, it would
/// make two Golden Runs incomparable while both looked fine. The same argument already put the
/// plasma gate and the recorder's save trigger on one measurement (<see cref="PlasmaGate"/>).</para>
/// </summary>
public static class RatioFrameSampling
{
    /// <summary>
    /// Reads one ratio out of one frame.
    /// </summary>
    /// <param name="gateOpen">Plasma gate: null when it could not be evaluated at all, which is
    /// treated as open — "we can't tell" is not "plasma off", and a silently dead ratio is the
    /// failure that gate exists to remove. The caller is the one that says so in the log.</param>
    /// <param name="referenceFloor">Leak-free level the reference line must clear, ratio mode
    /// only. Pass 0 while building a baseline: the floor comes out of the capture itself, so an
    /// inherited one would block re-baselining after a peak shift or a lower-power recipe.</param>
    public static RatioFrameSample Evaluate(RatioDefinition def, float[] wavelengths,
        float[] intensities, bool? gateOpen, double referenceFloor)
    {
        if (def is null) throw new ArgumentNullException(nameof(def));

        var num = LineIntensityExtractor.Extract(wavelengths, intensities, def.Numerator);
        var den = LineIntensityExtractor.Extract(wavelengths, intensities, def.Denominator);
        bool absolute = def.MonitorMode == MonitorMode.AbsoluteIntensity;
        bool open = gateOpen ?? true;

        // Ratio mode additionally needs its own reference line, because it divides by it. The
        // reference check alone is not a plasma test: a frame the spectrometer returns blank
        // still carries ~75 counts at the reference wavelength, which clears "> 0".
        bool referenceMissing = !absolute && (double.IsNaN(den.Value) || den.Value <= 0);
        bool plasma = absolute
            ? open
            : open && !referenceMissing && den.Value > referenceFloor;

        double value = absolute
            ? num.Value
            : (den.Value != 0 ? num.Value / den.Value : double.NaN);

        // A raw reading has no noise estimate, so its SNR is unknown rather than low and the
        // floor stands down for it. In absolute mode only the monitored line is judged — the
        // reference is not part of the reading, so letting its SNR veto would leave a strong
        // line reading "low signal" for ever.
        bool lowSnr = def.MinSnr > 0 &&
            ((!double.IsNaN(num.Snr) && num.Snr < def.MinSnr) ||
             (!absolute && !double.IsNaN(den.Snr) && den.Snr < def.MinSnr));

        return new RatioFrameSample(num, den, value, open, plasma, lowSnr)
        {
            ReferenceMissing = referenceMissing,
        };
    }
}

/// <summary>
/// Streaming mean / population σ. The σ a Golden Run stores is the scatter of the frames it
/// actually saw, not an estimate of a wider population, so the divisor is N — and it has to stay
/// that way for an offline-built baseline to be comparable with one captured live.
/// </summary>
public sealed class RunningStats
{
    private double _sum, _sumSq;

    public int Count { get; private set; }

    public void Add(double v) { _sum += v; _sumSq += v * v; Count++; }

    public void Reset() { _sum = _sumSq = 0; Count = 0; }

    public double Mean => Count > 0 ? _sum / Count : 0.0;

    public double StdDev
    {
        get
        {
            if (Count < 2) return 0.0;
            double m = Mean;
            double var = _sumSq / Count - m * m;
            return var > 0 ? Math.Sqrt(var) : 0.0;
        }
    }
}
