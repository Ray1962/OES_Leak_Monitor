using System;

namespace OES_Leak_Monitor;

/// <summary>
/// Plasma-present gate for absolute-intensity ratios, derived from the intensity logger's save
/// trigger — the same quantity against the same threshold, so the gate and the recorder never
/// disagree about how bright the frame is.
///
/// <para>They share that measurement, not the recorder's state machine. This is a bare
/// per-frame comparison; the recorder confirms for <c>StartConfirmSeconds</c> before it opens,
/// holds through <c>StopConfirmSeconds</c> after the metric has fallen, and back-fills
/// <c>max(StartConfirmSeconds, PreTriggerSeconds)</c> of buffered frames when it does open
/// (Core 0.1.8). A recording therefore carries gate-closed rows at both ends — the pre-trigger
/// head is below the threshold by definition — so "the gate is open" is not the same claim as
/// "this row is in a file", and an offline baseline built off a recording has to re-apply this
/// gate rather than trust that being in the file means anything.</para>
///
/// <para>Ratio-mode entries gate on their reference line (it has to be present anyway, since it
/// is divided in). An absolute-intensity entry doesn't divide by anything, so the reference line
/// is not required to exist at all — and a line that isn't there extracts to continuum noise
/// scattered about zero, which made <c>reference &gt; 0</c> a coin flip deciding whether the
/// frame was evaluated. Worse, a reference sitting on a curved continuum (a linear baseline drawn
/// between two side windows lies above a convex continuum) extracts systematically negative, and
/// the ratio never evaluated a single frame. Hence this gate.</para>
///
/// <para>Deliberately mirrors <c>DualIntensityLogger.SampleTriggerMetric</c>: nearest bin within
/// <see cref="LoggerSettings.WavelengthToleranceNm"/> for <see cref="TriggerMode.Wavelength"/>,
/// the frame percentile for <see cref="TriggerMode.SpectrumPercentile"/>, the brightest monitored
/// wavelength for <see cref="TriggerMode.AnyMonitoredWavelength"/>. Introducing a third way to
/// measure "how bright is it" would guarantee the gate and the recorder eventually disagree.</para>
///
/// <para>Immutable snapshot of the logger settings — rebuilt by
/// <see cref="LeakMonitorEngine.ConfigureTrigger"/> at start-up and on every Apply.</para>
/// </summary>
public sealed class PlasmaGate
{
    private readonly TriggerMode _mode;
    private readonly double _percentile;
    private readonly float _triggerWavelength;
    private readonly float _toleranceNm;
    private readonly float[] _monitored;

    public PlasmaGate(LoggerSettings settings)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        _mode = settings.TriggerMode;
        _percentile = settings.TriggerPercentile;
        _triggerWavelength = settings.TriggerWavelength;
        _toleranceNm = settings.WavelengthToleranceNm;
        _monitored = settings.MonitoredWavelengths ?? Array.Empty<float>();
        Threshold = settings.SaveStartThresholdIntensity;
    }

    /// <summary>Intensity the trigger metric must exceed — the logger's save-start threshold.</summary>
    public float Threshold { get; }

    /// <summary>
    /// False when the gate cannot decide anything: a non-positive threshold (every frame would
    /// pass) or a monitored-wavelength trigger with no wavelengths listed. The engine then leaves
    /// absolute-intensity ratios ungated and says so once in the system log, rather than silently
    /// treating "misconfigured" as "no plasma".
    /// </summary>
    public bool IsUsable =>
        Threshold > 0 && (_mode != TriggerMode.AnyMonitoredWavelength || _monitored.Length > 0);

    /// <summary>One-line description of the active gate, for the system log.</summary>
    public string Description => _mode switch
    {
        TriggerMode.SpectrumPercentile =>
            $"frame {_percentile:0.#}th percentile above {Threshold:0.#}",
        TriggerMode.AnyMonitoredWavelength =>
            $"brightest of {_monitored.Length} monitored wavelength(s) above {Threshold:0.#}",
        _ => $"{_triggerWavelength:0.#} nm above {Threshold:0.#}",
    };

    /// <summary>
    /// True / false when the trigger metric could be measured on this frame, null when it could
    /// not (wavelength outside the axis or outside tolerance, empty frame, unusable configuration)
    /// — the caller decides what an unanswerable gate means.
    /// </summary>
    public bool? IsPlasmaPresent(float[]? wavelengths, float[]? intensities)
    {
        if (!IsUsable) return null;
        var metric = TriggerMetric(wavelengths, intensities);
        return metric is { } v ? v > Threshold : null;
    }

    private float? TriggerMetric(float[]? wl, float[]? inten)
    {
        if (wl is null || inten is null) return null;
        int n = Math.Min(wl.Length, inten.Length);
        if (n == 0) return null;

        switch (_mode)
        {
            case TriggerMode.SpectrumPercentile:
                return Percentile(inten, n, _percentile);

            case TriggerMode.AnyMonitoredWavelength:
            {
                float? best = null;
                foreach (var target in _monitored)
                {
                    var v = BinNearest(wl, inten, n, target);
                    if (v is { } value && (best is null || value > best)) best = value;
                }
                return best;
            }

            default:
                return BinNearest(wl, inten, n, _triggerWavelength);
        }
    }

    /// <summary>Intensity of the bin nearest <paramref name="target"/>, or null if outside tolerance.</summary>
    private float? BinNearest(float[] wl, float[] inten, int n, float target)
    {
        int best = 0;
        float bestDelta = Math.Abs(wl[0] - target);
        for (int i = 1; i < n; i++)
        {
            var d = Math.Abs(wl[i] - target);
            if (d < bestDelta) { bestDelta = d; best = i; }
        }
        return bestDelta <= _toleranceNm ? inten[best] : (float?)null;
    }

    /// <summary>The p-th percentile of one frame by nearest rank (same as the logger's).</summary>
    private static float Percentile(float[] values, int n, double percentile)
    {
        var copy = new float[n];
        Array.Copy(values, copy, n);
        Array.Sort(copy);
        var p = Math.Clamp(percentile, 0, 100);
        int idx = (int)Math.Round((p / 100.0) * (n - 1), MidpointRounding.AwayFromZero);
        return copy[Math.Clamp(idx, 0, n - 1)];
    }
}
