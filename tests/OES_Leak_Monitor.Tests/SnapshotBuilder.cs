using OES_Leak_Monitor;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// Hand-built <see cref="LeakMonitorSnapshot"/>s for the tests that are about what SECS does
/// with a snapshot rather than about how the engine arrives at one. The engine's own behaviour
/// is exercised against real spectra in <c>RecordedRunTests</c>.
/// </summary>
internal static class SnapshotBuilder
{
    /// <summary>
    /// A tool mid-leak: three enabled ratios, one in alarm and one in warning, a calibrated
    /// leak-rate estimate, plasma on. The shape the wire tests assert their text against.
    /// </summary>
    public static LeakMonitorSnapshot Leaking() => new()
    {
        Timestamp = new DateTime(2026, 8, 14, 22, 11, 21, DateTimeKind.Local),
        Overall = LeakAlarmLevel.Alarm,
        TestMode = true,
        ActiveGoldenRun = "Recipe Ar",
        ActiveCalibration = "Air 2026-08",
        CalibrationStatus = CalibrationStatus.Active,
        PlasmaPresent = true,
        PlasmaGateAvailable = true,
        DropoutCount = 3,
        LeakRate = new LeakRateEstimate
        {
            HasEstimate = true,
            LeakRate = 1.8e-4,
            Sigma = 3.0e-5,
            Confidence = 0.72,
        },
        Ratios = new[]
        {
            Ratio("R_ec3adb8f", "N2 337.1 / Ar 750.4", RatioState.Alarm, percentOfBaseline: 158),
            Ratio("R_353b7e10", "N2 353.7 / Ar 750.4", RatioState.Warning, percentOfBaseline: 121),
            Ratio("R_7a6c5d0f", "uN2 335.5 / Ar 750.4", RatioState.Normal, percentOfBaseline: 101),
        },
    };

    /// <summary>Nothing to report: plasma on, every ratio at its baseline.</summary>
    public static LeakMonitorSnapshot Quiet() => new()
    {
        Timestamp = new DateTime(2026, 8, 14, 22, 4, 0, DateTimeKind.Local),
        Overall = LeakAlarmLevel.Normal,
        TestMode = true,
        ActiveGoldenRun = "Recipe Ar",
        CalibrationStatus = CalibrationStatus.NotCalibrated,
        PlasmaPresent = true,
        PlasmaGateAvailable = true,
        Ratios = new[]
        {
            Ratio("R_ec3adb8f", "N2 337.1 / Ar 750.4", RatioState.Normal, percentOfBaseline: 100),
        },
    };

    private static RatioSnapshot Ratio(string key, string name, RatioState state, double percentOfBaseline) =>
        new(key, name, state,
            RawRatio: 0.05, SmoothedRatio: 0.05,
            HasBaseline: true, BaselineMean: 0.05, BaselineSigma: 0.001,
            PercentOfBaseline: percentOfBaseline,
            WarnThreshold: 0.0525, AlarmThreshold: 0.056,
            SlopePerMinute: 0.4,
            PlasmaPresent: true,
            NumeratorIntensity: 900, DenominatorIntensity: 18000,
            RatioNoiseSigma: 0.0008, NumeratorSnr: 40, DenominatorSnr: 120,
            Mode: MonitorMode.Ratio, Role: RatioRole.Alarm, HasPedestal: false);
}
