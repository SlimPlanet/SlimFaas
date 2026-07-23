using System.Text;
using SlimFaas.Options;
using SlimFaas.Workers;

namespace SlimFaas.Tests.Workers;

public class PrometheusStreamParserTests
{
    [Fact]
    public async Task ParsesRequestedSamplesAcrossFragmentedReads()
    {
        const string body = """
                            # HELP metric_one test
                              metric_one +1.25 1731200000
                            metric_two{label="a\"}b",zone="é"} -2.5e1
                            duplicate_metric 1
                            duplicate_metric 2
                            metric_nan NaN
                            metric_inf +Inf
                            malformed_metric 5 invalid-timestamp
                            unrequested_metric{unterminated="label" 9
                            """;
        var requested = new[]
        {
            "metric_one",
            "metric_two",
            "duplicate_metric",
            "metric_nan",
            "metric_inf",
            "malformed_metric"
        };
        await using var stream = Stream(body.Replace("\n", "\r\n", StringComparison.Ordinal), maxChunkSize: 3);

        var result = await PrometheusStreamParser.ParseAsync(
            stream,
            requested,
            Options(),
            CancellationToken.None);

        Assert.Equal(PrometheusStreamParseStatus.Success, result.Status);
        Assert.Equal(3, result.Metrics.Count);
        Assert.Equal(1.25, result.Metrics["metric_one"]);
        Assert.Equal(-25, result.Metrics["""metric_two{label="a\"}b",zone="é"}"""]);
        Assert.Equal(2, result.Metrics["duplicate_metric"]);
    }

    [Fact]
    public async Task FiltersUnrequestedSeriesBeforeParsingLabels()
    {
        var body = new StringBuilder();
        body.AppendLine("unrequested_metric{unterminated=\"label\" 1");
        for (var index = 0; index < 5_000; index++)
            body.Append("ignored_metric_").Append(index).Append("{label=\"value\"} ").AppendLine(index.ToString());
        body.AppendLine("requested_metric{label=\"kept\"} 42");

        await using var stream = Stream(body.ToString(), maxChunkSize: 17);
        var result = await PrometheusStreamParser.ParseAsync(
            stream,
            ["requested_metric"],
            Options(maxSelectedSeries: 1),
            CancellationToken.None);

        Assert.Equal(PrometheusStreamParseStatus.Success, result.Status);
        var metric = Assert.Single(result.Metrics);
        Assert.Equal("requested_metric{label=\"kept\"}", metric.Key);
        Assert.Equal(42, metric.Value);
    }

    [Fact]
    public async Task RejectsResponseWhenActualStreamExceedsLimit()
    {
        await using var stream = Stream("metric_one 123\n", maxChunkSize: 2);

        var result = await PrometheusStreamParser.ParseAsync(
            stream,
            ["metric_one"],
            Options(maxResponseBytes: 8),
            CancellationToken.None);

        Assert.Equal(PrometheusStreamParseStatus.ResponseTooLarge, result.Status);
        Assert.Empty(result.Metrics);
    }

    [Fact]
    public async Task RejectsResponseAtomicallyWhenLineExceedsLimit()
    {
        await using var stream = Stream("metric_one 1\nmetric_two{label=\"too-long\"} 2\n", maxChunkSize: 4);

        var result = await PrometheusStreamParser.ParseAsync(
            stream,
            ["metric_one", "metric_two"],
            Options(maxLineBytes: 16),
            CancellationToken.None);

        Assert.Equal(PrometheusStreamParseStatus.LineTooLong, result.Status);
        Assert.Empty(result.Metrics);
    }

    [Fact]
    public async Task RejectsResponseAtomicallyWhenSelectedSeriesLimitIsExceeded()
    {
        const string body = """
                            metric_one{label="a"} 1
                            metric_one{label="b"} 2
                            """;
        await using var stream = Stream(body, maxChunkSize: 5);

        var result = await PrometheusStreamParser.ParseAsync(
            stream,
            ["metric_one"],
            Options(maxSelectedSeries: 1),
            CancellationToken.None);

        Assert.Equal(PrometheusStreamParseStatus.TooManySeries, result.Status);
        Assert.Empty(result.Metrics);
    }

    [Fact]
    public async Task HonorsCancellation()
    {
        await using var stream = Stream("metric_one 1\n", maxChunkSize: 1);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await PrometheusStreamParser.ParseAsync(
                stream,
                ["metric_one"],
                Options(),
                cancellation.Token));
    }

    private static MetricsScrapingOptions Options(
        long maxResponseBytes = 8L * 1024L * 1024L,
        int maxLineBytes = 64 * 1024,
        int maxSelectedSeries = 10_000)
        => new()
        {
            MaxResponseBytes = maxResponseBytes,
            MaxLineBytes = maxLineBytes,
            MaxSelectedSeriesPerTarget = maxSelectedSeries,
            RequestTimeoutSeconds = 10
        };

    private static FragmentedReadStream Stream(string content, int maxChunkSize)
        => new(Encoding.UTF8.GetBytes(content), maxChunkSize);

    private sealed class FragmentedReadStream(byte[] content, int maxChunkSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => content.LongLength;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(Math.Min(count, maxChunkSize), content.Length - _position);
            if (read <= 0)
                return 0;
            content.AsSpan(_position, read).CopyTo(buffer.AsSpan(offset, read));
            _position += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Math.Min(Math.Min(buffer.Length, maxChunkSize), content.Length - _position);
            if (read <= 0)
                return ValueTask.FromResult(0);
            content.AsMemory(_position, read).CopyTo(buffer);
            _position += read;
            return ValueTask.FromResult(read);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
