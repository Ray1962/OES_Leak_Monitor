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
