using SlimFaas.Workers;

namespace SlimFaas.Tests.Workers;

public sealed class SlimDataRecoveryMetricCalculatorTests
{
    [Theory]
    [InlineData(10, 4, 6)]
    [InlineData(4, 10, 0)]
    public void CalculatesLocalApplyLag(long lastLogIndex, long appliedLogIndex, long expected)
        => Assert.Equal(expected,
            SlimDataDiagnosticsWorker.SlimDataRecoveryMetricCalculator.CalculateLocalApplyLag(
                lastLogIndex, appliedLogIndex));

    [Fact]
    public void CalculatesRatesAndRejectsRewoundIndexes()
    {
        Assert.Equal(4, SlimDataDiagnosticsWorker.SlimDataRecoveryMetricCalculator.CalculateRate(14, 6, 2));
        Assert.Equal(0, SlimDataDiagnosticsWorker.SlimDataRecoveryMetricCalculator.CalculateRate(6, 14, 2));
        Assert.Equal(0, SlimDataDiagnosticsWorker.SlimDataRecoveryMetricCalculator.CalculateRate(14, 6, 0));
    }

    [Theory]
    [InlineData(true, 4, false, "wal")]
    [InlineData(true, 4, true, "restoring")]
    [InlineData(true, 0, false, "unknown")]
    [InlineData(false, 4, false, "unknown")]
    public void SelectsOnlyReliableRecoveryModes(
        bool isFollower, long localApplyLag, bool isRestoring, string expected)
        => Assert.Equal(expected,
            SlimDataDiagnosticsWorker.SlimDataRecoveryMetricCalculator.GetMode(
                isFollower, localApplyLag, isRestoring));

    [Fact]
    public void DetectsWhenGenerationOutrunsCatchUp()
    {
        Assert.True(SlimDataDiagnosticsWorker.SlimDataRecoveryMetricCalculator.CannotConverge(10, 9));
        Assert.False(SlimDataDiagnosticsWorker.SlimDataRecoveryMetricCalculator.CannotConverge(10, 10));
    }

    [Fact]
    public void StartsAndClearsRecoveryAfterTwoHealthySamples()
    {
        long started = 0;
        var clearSamples = 0;

        SlimDataDiagnosticsWorker.SlimDataRecoveryMetricCalculator.UpdateRecoveryState(
            "wal", 10, ref started, ref clearSamples);
        Assert.Equal(10, started);
        Assert.Equal(0, clearSamples);

        SlimDataDiagnosticsWorker.SlimDataRecoveryMetricCalculator.UpdateRecoveryState(
            "unknown", 20, ref started, ref clearSamples);
        Assert.Equal(10, started);
        Assert.Equal(1, clearSamples);

        SlimDataDiagnosticsWorker.SlimDataRecoveryMetricCalculator.UpdateRecoveryState(
            "unknown", 30, ref started, ref clearSamples);
        Assert.Equal(0, started);
        Assert.Equal(0, clearSamples);
    }
}
