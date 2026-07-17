using Microsoft.Extensions.Logging.Abstractions;
using SlimData;
using SlimFaas;
using Xunit;

namespace SlimFaas.Tests;

public sealed class RetryTests
{
    [Fact]
    public async Task DoAsync_retries_set_style_unavailability_and_preserves_the_final_exception()
    {
        var attempts = 0;

        var exception = await Assert.ThrowsAsync<SlimDataUnavailableException>(() =>
            Retry.DoAsync<int>(
                () =>
                {
                    attempts++;
                    return Task.FromException<int>(new SlimDataUnavailableException("No active quorum."));
                },
                NullLogger.Instance,
                [0]));

        Assert.Equal(2, attempts);
        Assert.Equal("No active quorum.", exception.Message);
    }
}
