using System.Buffers;
using System.Buffers.Text;
using System.Text;
using SlimFaas.Options;

namespace SlimFaas.Workers;

internal enum PrometheusStreamParseStatus
{
    Success,
    ResponseTooLarge,
    LineTooLong,
    TooManySeries
}

internal readonly record struct PrometheusStreamParseResult(
    PrometheusStreamParseStatus Status,
    IReadOnlyDictionary<string, double> Metrics,
    long BytesRead,
    long LinesRead);

internal static class PrometheusStreamParser
{
    private const int ReadBufferSize = 16 * 1024;

    internal static async ValueTask<PrometheusStreamParseResult> ParseAsync(
        Stream stream,
        IReadOnlyCollection<string> requestedMetricNames,
        MetricsScrapingOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(requestedMetricNames);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxResponseBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxLineBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxSelectedSeriesPerTarget);

        var metrics = new Dictionary<string, double>(StringComparer.Ordinal);
        if (requestedMetricNames.Count == 0)
            return new(PrometheusStreamParseStatus.Success, metrics, 0L, 0L);

        var readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        var lineBuffer = ArrayPool<byte>.Shared.Rent(options.MaxLineBytes);
        long bytesRead = 0L;
        long linesRead = 0L;
        var lineLength = 0;

        try
        {
            while (true)
            {
                var read = await stream
                    .ReadAsync(readBuffer.AsMemory(0, ReadBufferSize), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;

                bytesRead += read;
                if (bytesRead > options.MaxResponseBytes)
                    return Rejected(PrometheusStreamParseStatus.ResponseTooLarge, metrics, bytesRead, linesRead);

                var offset = 0;
                while (offset < read)
                {
                    var remaining = readBuffer.AsSpan(offset, read - offset);
                    var newlineOffset = remaining.IndexOf((byte)'\n');
                    var segmentLength = newlineOffset < 0 ? remaining.Length : newlineOffset;

                    if (lineLength + segmentLength > options.MaxLineBytes)
                        return Rejected(PrometheusStreamParseStatus.LineTooLong, metrics, bytesRead, linesRead);

                    remaining[..segmentLength].CopyTo(lineBuffer.AsSpan(lineLength));
                    lineLength += segmentLength;
                    offset += segmentLength;

                    if (newlineOffset < 0)
                        continue;

                    offset++;
                    linesRead++;
                    var lengthWithoutCarriageReturn =
                        lineLength > 0 && lineBuffer[lineLength - 1] == (byte)'\r'
                            ? lineLength - 1
                            : lineLength;
                    if (!TryProcessLine(
                            lineBuffer.AsSpan(0, lengthWithoutCarriageReturn),
                            requestedMetricNames,
                            metrics,
                            options.MaxSelectedSeriesPerTarget))
                    {
                        return Rejected(PrometheusStreamParseStatus.TooManySeries, metrics, bytesRead, linesRead);
                    }

                    lineLength = 0;
                }
            }

            if (lineLength > 0)
            {
                linesRead++;
                var lengthWithoutCarriageReturn =
                    lineBuffer[lineLength - 1] == (byte)'\r' ? lineLength - 1 : lineLength;
                if (!TryProcessLine(
                        lineBuffer.AsSpan(0, lengthWithoutCarriageReturn),
                        requestedMetricNames,
                        metrics,
                        options.MaxSelectedSeriesPerTarget))
                {
                    return Rejected(PrometheusStreamParseStatus.TooManySeries, metrics, bytesRead, linesRead);
                }
            }

            return new(PrometheusStreamParseStatus.Success, metrics, bytesRead, linesRead);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
            ArrayPool<byte>.Shared.Return(lineBuffer);
        }
    }

    private static PrometheusStreamParseResult Rejected(
        PrometheusStreamParseStatus status,
        Dictionary<string, double> metrics,
        long bytesRead,
        long linesRead)
    {
        metrics.Clear();
        return new(status, metrics, bytesRead, linesRead);
    }

    /// <summary>
    /// Returns false only when adding this line would exceed the configured series limit.
    /// Invalid, non-finite, or unrequested samples are ignored.
    /// </summary>
    private static bool TryProcessLine(
        ReadOnlySpan<byte> line,
        IReadOnlyCollection<string> requestedMetricNames,
        Dictionary<string, double> metrics,
        int maxSelectedSeries)
    {
        var index = SkipWhitespace(line, 0);
        if (index >= line.Length || line[index] == (byte)'#' || !IsMetricNameStart(line[index]))
            return true;

        var nameStart = index++;
        while (index < line.Length && IsMetricNamePart(line[index]))
            index++;
        var nameEnd = index;

        // Filtering happens before labels are scanned or decoded. This is the hot path for
        // endpoints exposing thousands of series while SlimFaas needs only a few names.
        if (!IsRequested(line[nameStart..nameEnd], requestedMetricNames))
            return true;

        if (index < line.Length && line[index] == (byte)'{')
        {
            index = FindLabelSetEnd(line, index);
            if (index < 0)
                return true;
        }

        var keyEnd = index;
        if (index >= line.Length || !IsWhitespace(line[index]))
            return true;

        index = SkipWhitespace(line, index);
        var valueStart = index;
        while (index < line.Length && !IsWhitespace(line[index]))
            index++;
        if (valueStart == index)
            return true;

        var valueBytes = line[valueStart..index];
        if (!Utf8Parser.TryParse(valueBytes, out double value, out var consumed) ||
            consumed != valueBytes.Length ||
            !double.IsFinite(value))
        {
            return true;
        }

        index = SkipWhitespace(line, index);
        if (index < line.Length)
        {
            var timestampStart = index;
            while (index < line.Length && !IsWhitespace(line[index]))
            {
                if (line[index] is < (byte)'0' or > (byte)'9')
                    return true;
                index++;
            }

            if (timestampStart == index || SkipWhitespace(line, index) != line.Length)
                return true;
        }

        var key = Encoding.UTF8.GetString(line[nameStart..keyEnd]);
        if (!metrics.ContainsKey(key) && metrics.Count >= maxSelectedSeries)
            return false;

        metrics[key] = value;
        return true;
    }

    private static int FindLabelSetEnd(ReadOnlySpan<byte> line, int openingBrace)
    {
        var inString = false;
        var escaped = false;
        for (var index = openingBrace + 1; index < line.Length; index++)
        {
            var current = line[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == (byte)'\\')
                {
                    escaped = true;
                }
                else if (current == (byte)'"')
                {
                    inString = false;
                }
            }
            else if (current == (byte)'"')
            {
                inString = true;
            }
            else if (current == (byte)'}')
            {
                return index + 1;
            }
        }

        return -1;
    }

    private static bool IsRequested(
        ReadOnlySpan<byte> metricName,
        IReadOnlyCollection<string> requestedMetricNames)
    {
        foreach (var requestedName in requestedMetricNames)
        {
            if (requestedName.Length != metricName.Length)
                continue;

            var matches = true;
            for (var index = 0; index < metricName.Length; index++)
            {
                if (requestedName[index] > 0x7F || (byte)requestedName[index] != metricName[index])
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
                return true;
        }

        return false;
    }

    private static int SkipWhitespace(ReadOnlySpan<byte> line, int index)
    {
        while (index < line.Length && IsWhitespace(line[index]))
            index++;
        return index;
    }

    private static bool IsWhitespace(byte value) => value is (byte)' ' or (byte)'\t';

    private static bool IsMetricNameStart(byte value)
        => value is (>= (byte)'a' and <= (byte)'z') or
            (>= (byte)'A' and <= (byte)'Z') or
            (byte)'_' or
            (byte)':';

    private static bool IsMetricNamePart(byte value)
        => IsMetricNameStart(value) || value is >= (byte)'0' and <= (byte)'9';
}
