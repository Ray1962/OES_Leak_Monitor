using System;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// Constructing the Baseline Builder tab.
///
/// <para>It threw on first use: <c>StartDate</c> and <c>EndDate</c> rescan the data folder when
/// set, the constructor set them before it had built the commands, and the rescan walked into
/// <c>RaiseCanExecuteChanged</c> on a command that was still null. Nothing about the arithmetic
/// under the tab was wrong — the tab simply could not be opened, which no amount of testing the
/// arithmetic would have caught.</para>
/// </summary>
public class BaselineBuilderViewModelTests
{
    private const string Missing = @"C:\NoSuchFolder-OES-Leak-Monitor-Tests";

    [Fact]
    public void CanBeConstructed_EvenWhenTheDataFolderIsNotThere()
    {
        using var engine = new LeakMonitorEngine(LeakMonitorSettings.CreateDefault());
        using var intensity = new DualIntensityLogger(new[] { "OES1" }, Missing);
        var logger = new LoggerViewModel(intensity, Missing);

        var vm = new BaselineBuilderViewModel(engine, logger, Missing);

        Assert.NotNull(vm.RefreshCommand);
        Assert.NotNull(vm.BuildCommand);
        Assert.Empty(vm.Files);
        Assert.Contains(Missing, vm.EffectiveBaseDirectory);
    }

    /// <summary>The date range is what rescans, so it has to survive being moved after start-up
    /// as well as during it.</summary>
    [Fact]
    public void ChangingTheDateRangeRescans()
    {
        using var engine = new LeakMonitorEngine(LeakMonitorSettings.CreateDefault());
        using var intensity = new DualIntensityLogger(new[] { "OES1" }, Missing);
        var logger = new LoggerViewModel(intensity, Missing);
        var vm = new BaselineBuilderViewModel(engine, logger, Missing);

        vm.StartDate = DateTime.Today.AddDays(-90);

        Assert.Empty(vm.Files);          // still nothing there, but it did not throw getting there
    }
}

/// <summary>
/// What an acquisition fingerprint may claim to know.
///
/// <para>A Golden Run built from a recording with no sidecar beside it carries the axis the CSV
/// proves and nothing else. Reported as differences, those unknowns became
/// "integration 0 → 150, average 0 → 6, boxcar 0 → 1, acquire mode → HardwareAverage…" on every
/// frame — a warning about facts nobody had, which is how a warning stops being read. Measured on
/// 2026-08-21, from a baseline built out of recordings made before the sidecar existed.</para>
/// </summary>
public class AcquisitionFingerprintTests
{
    private static AcquisitionFingerprint Live() => new()
    {
        IntegrationTimeMs = 150, AverageCount = 6, BoxcarWidth = 1,
        AcquireMode = "HardwareAverage", AverageMode = "Hardware",
        BackgroundRemove = true, LinearityCorrection = true,
        AxisLength = 1904, AxisStartNm = 179.84, AxisEndNm = 850.19,
    };

    /// <summary>Axis known, settings not: the axis is still worth comparing, the rest is not.</summary>
    private static AcquisitionFingerprint AxisOnly(int points = 1904) => new()
    {
        AxisLength = points, AxisStartNm = 179.84, AxisEndNm = 850.19,
    };

    [Fact]
    public void UnrecordedSettingsAreNotReportedAsChanges()
    {
        Assert.Equal("", Live().Differences(AxisOnly()));
    }

    [Fact]
    public void TheAxisIsStillComparedWhenTheSettingsAreUnknown()
    {
        var differences = Live().Differences(AxisOnly(1000));

        Assert.Contains("axis points", differences);
        Assert.DoesNotContain("integration", differences);
    }

    [Fact]
    public void SettingsThatWereRecordedAreStillCompared()
    {
        var slower = Live();
        slower.IntegrationTimeMs = 40;

        Assert.Contains("integration 150 → 40", slower.Differences(Live()));
    }
}
