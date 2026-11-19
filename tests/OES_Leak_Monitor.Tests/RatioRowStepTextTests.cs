using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// What a Leak Monitor row says about step judging (#94): the Step column, the source of a
/// Warning / Alarm, and the caption that marks a Golden Run percentage as not judged.
/// </summary>
public class RatioRowStepTextTests
{
    private static RatioSnapshot Snap(RatioState state, JudgingPaths enabled, JudgingPaths raisedBy = JudgingPaths.None,
                                      double rise = double.NaN, double warmup = 0, bool frozen = false) =>
        new("R", "R", state, 0.5, 0.5, true, 0.5, 0.01, 100, 0.55, 0.6, 0, true, 1, 2, 0.01, 20, 20,
            MonitorMode.Ratio, RatioRole.Alarm, false)
        {
            EnabledPaths = enabled, RaisedBy = raisedBy, StepRise = rise,
            StepWarmupSecondsRemaining = enabled.HasFlag(JudgingPaths.Step) ? warmup : double.NaN,
            StepReferenceFrozen = frozen, StepReference = 0.5,
        };

    private static RatioViewModel Row(RatioSnapshot s)
    {
        var vm = new RatioViewModel("R", "R");
        vm.Apply(s);
        return vm;
    }

    [Fact]
    public void A_Golden_Run_only_row_reads_exactly_as_before()
    {
        var vm = Row(Snap(RatioState.Alarm, JudgingPaths.GoldenRun, JudgingPaths.GoldenRun));
        Assert.Equal("ALARM", vm.StateText);
        Assert.Equal("—", vm.StepText);
        Assert.Equal("% of Golden Run", vm.PercentCaption);
    }

    [Theory]
    [InlineData(RatioState.Alarm, JudgingPaths.Step, "ALARM · Step")]
    [InlineData(RatioState.Warning, JudgingPaths.GoldenRun, "WARNING · GR")]
    [InlineData(RatioState.Alarm, JudgingPaths.GoldenRun | JudgingPaths.Step, "ALARM · GR+Step")]
    public void A_step_judged_row_names_the_path_behind_its_state(RatioState state, JudgingPaths by, string text)
    {
        var vm = Row(Snap(state, JudgingPaths.GoldenRun | JudgingPaths.Step, by, rise: 0.3));
        Assert.Equal(text, vm.StateText);
    }

    [Fact]
    public void The_step_column_shows_warm_up_rise_and_frozen()
    {
        Assert.Equal("warming up 23 s", Row(Snap(RatioState.NoBaseline, JudgingPaths.Step, warmup: 22.4)).StepText);
        Assert.Equal("Warming up", Row(Snap(RatioState.NoBaseline, JudgingPaths.Step, warmup: 22.4)).StateText);
        Assert.Equal("+31.1 %", Row(Snap(RatioState.Normal, JudgingPaths.Step, rise: 0.311)).StepText);
        Assert.Equal("+31.1 % (frozen)", Row(Snap(RatioState.Alarm, JudgingPaths.Step, JudgingPaths.Step, rise: 0.311, frozen: true)).StepText);
        Assert.Equal("-2.0 %", Row(Snap(RatioState.Normal, JudgingPaths.Step, rise: -0.02)).StepText);
    }

    [Fact]
    public void Golden_Run_percent_is_captioned_not_judged_when_that_path_is_off()
    {
        var vm = Row(Snap(RatioState.Normal, JudgingPaths.Step, rise: 0.01));
        Assert.Equal("% of GR (not judged)", vm.PercentCaption);
        Assert.Equal("100 %", vm.PercentText);   // still shown: it is the drift
    }
}
