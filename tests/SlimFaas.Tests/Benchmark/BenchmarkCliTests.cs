using BenchmarkProgram = SlimFaas.Benchmarks.Program;

namespace SlimFaas.Tests.Benchmark;

public sealed class BenchmarkCliTests
{
    [Fact]
    public async Task Missing_command_preserves_usage_exit_code()
    {
        Assert.Equal(2, await BenchmarkProgram.Main([]));
    }

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task Help_returns_success(string command)
    {
        Assert.Equal(0, await BenchmarkProgram.Main([command]));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("target")]
    [InlineData("run")]
    [InlineData("compare")]
    [InlineData("report")]
    public async Task Invalid_arguments_return_failure_instead_of_escaping(string command)
    {
        Assert.Equal(1, await BenchmarkProgram.Main([command, "--invalid"]));
    }
}
