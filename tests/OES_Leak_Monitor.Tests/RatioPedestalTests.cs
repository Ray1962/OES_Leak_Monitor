using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// What <c>RatioDefinition.ValueHasPedestal</c> decides, and the one consequence that is
/// invisible from the screen: which of the two Warn/Alarm forms actually applies.
///
/// <para>The predicate read <c>AbsoluteIntensity &amp;&amp; RawMean</c> for as long as the pedestal
/// idea existed, on the assumption that dividing by a reference line removes the pedestal. It
/// does not — <c>(pedestal + line) / reference</c> is the same affine offset scaled by
/// <c>1/reference</c>. The effect is silent because <c>Math.Max</c> simply picks the other
/// branch: with the factory 1.05/1.12 factors it takes over once <c>mean/σ</c> passes 60, so a
/// ratio configured for 3 σ was warning at 5.9 σ and alarming at 14.2 σ with nothing on any
/// screen saying so. The numbers here are that machine's measured baseline.</para>
/// </summary>
public class RatioPedestalTests
{
    // Measured Golden Run baseline for N₂ 337.1 RawMean / Ar 750.4 PeakHeight — mean/σ ≈ 119,
    // comfortably past the point where the multiplicative branch wins.
    private const double BaseMean = 0.028948417572626506;
    private const double BaseSigma = 0.00024382125779962566;

    private static RatioDefinition Def(MonitorMode mode, LineExtractMode numerator) => new()
    {
        Key = "R_test",
        DisplayName = "test",
        MonitorMode = mode,
        WarnFactor = 1.05, AlarmFactor = 1.12,
        SigmaWarn = 3.0, SigmaAlarm = 6.0,
        Numerator = new LineRegion
        {
            Label = "N₂ 337.1", CenterNm = 337.1, HalfWidthNm = 1.0,
            BaselineGapNm = 1.0, BaselineWidthNm = 1.0, Mode = numerator,
        },
        Denominator = new LineRegion
        {
            Label = "Ar 750.4", CenterNm = 750.4, HalfWidthNm = 0.5,
            BaselineGapNm = 1.0, BaselineWidthNm = 1.0, Mode = LineExtractMode.PeakHeight,
        },
    };

    private static RatioMonitor Monitored(MonitorMode mode, LineExtractMode numerator)
    {
        var mon = new RatioMonitor(Def(mode, numerator));
        mon.SetBaseline(BaseMean, BaseSigma);
        return mon;
    }

    [Theory]
    [InlineData(MonitorMode.AbsoluteIntensity)]
    [InlineData(MonitorMode.Ratio)]
    public void RawMeanNumerator_CarriesThePedestal_InEitherMode(MonitorMode mode) =>
        Assert.True(Def(mode, LineExtractMode.RawMean).ValueHasPedestal);

    [Theory]
    [InlineData(MonitorMode.AbsoluteIntensity, LineExtractMode.PeakHeight)]
    [InlineData(MonitorMode.AbsoluteIntensity, LineExtractMode.Integral)]
    [InlineData(MonitorMode.Ratio, LineExtractMode.PeakHeight)]
    [InlineData(MonitorMode.Ratio, LineExtractMode.Integral)]
    public void SubtractedNumerator_HasNoPedestal(MonitorMode mode, LineExtractMode numerator) =>
        Assert.False(Def(mode, numerator).ValueHasPedestal);

    /// <summary>
    /// The whole point of the predicate: a pedestal drops the multiplicative branch, so the
    /// thresholds are the ones the operator configured rather than whatever the factors happen
    /// to work out to against a large mean.
    /// </summary>
    [Theory]
    [InlineData(MonitorMode.AbsoluteIntensity)]
    [InlineData(MonitorMode.Ratio)]
    public void PedestalValue_UsesTheSigmaThresholds_NotTheFactors(MonitorMode mode)
    {
        var mon = Monitored(mode, LineExtractMode.RawMean);

        Assert.Equal(BaseMean + 3.0 * BaseSigma, mon.WarnThreshold, 12);
        Assert.Equal(BaseMean + 6.0 * BaseSigma, mon.AlarmThreshold, 12);
        // And is genuinely looser than the factor form it replaces — the regression this guards
        // against is silent precisely because both numbers look reasonable in isolation.
        Assert.True(mon.WarnThreshold < 1.05 * BaseMean);
        Assert.True(mon.AlarmThreshold < 1.12 * BaseMean);
    }

    [Theory]
    [InlineData(MonitorMode.AbsoluteIntensity)]
    [InlineData(MonitorMode.Ratio)]
    public void SubtractedValue_KeepsTheHigherOfTheTwoForms(MonitorMode mode)
    {
        var mon = Monitored(mode, LineExtractMode.PeakHeight);

        // mean/σ ≈ 119, so at these factors the multiplicative branch is the higher one.
        Assert.Equal(1.05 * BaseMean, mon.WarnThreshold, 12);
        Assert.Equal(1.12 * BaseMean, mon.AlarmThreshold, 12);
    }
}
