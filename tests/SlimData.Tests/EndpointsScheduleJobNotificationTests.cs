using SlimData;

namespace SlimData.Tests;

public class EndpointsScheduleJobNotificationTests
{
    [Fact(DisplayName = "ScheduleJobPrefix est 'ScheduleJob:'")]
    public void ScheduleJobPrefix_Should_Be_Correct()
    {
        Assert.Equal("ScheduleJob:", Endpoints.ScheduleJobPrefix);
    }

    [Theory(DisplayName = "Clés commençant par ScheduleJob: doivent matcher le prefix")]
    [InlineData("ScheduleJob:fibonacci", true)]
    [InlineData("ScheduleJob:default", true)]
    [InlineData("ScheduleJob:", true)]
    [InlineData("OtherKey:fibonacci", false)]
    [InlineData("scheduleJob:fibonacci", false)]  // case-sensitive
    [InlineData("", false)]
    [InlineData("ScheduleJobfibonacci", false)]  // pas de ':'
    public void Key_StartsWith_ScheduleJobPrefix_Should_Match_Correctly(string key, bool expected)
    {
        var result = key.StartsWith(Endpoints.ScheduleJobPrefix, StringComparison.Ordinal);
        Assert.Equal(expected, result);
    }
}

