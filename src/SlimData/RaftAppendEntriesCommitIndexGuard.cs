using System.Globalization;
using Microsoft.Extensions.Primitives;

namespace SlimData;

/// <summary>
/// Applies the Raft commit-index bound to incoming AppendEntries requests before DotNext
/// passes the value to the local write-ahead log.
/// </summary>
/// <remarks>
/// DotNext 6.4.1 can advertise a commit index newer than the final entry carried by an
/// older replication round. Raft requires followers to commit no further than the last
/// new entry in that AppendEntries request.
/// </remarks>
public sealed class RaftAppendEntriesCommitIndexGuard(
    ILogger<RaftAppendEntriesCommitIndexGuard> logger) : IMiddleware
{
    private const string MessageTypeHeader = "X-Raft-Message-Type";
    private const string AppendEntriesMessageType = "AppendEntries";
    private const string PrecedingRecordIndexHeader = "X-Raft-Preceding-Record-Index";
    private const string EntriesCountHeader = "X-Raft-Entries-Count";
    private const string CommitIndexHeader = "X-Raft-Commit-Index";
    private long _clampedRequests;

    /// <summary>
    /// Gets the number of incoming AppendEntries requests whose commit index was bounded.
    /// </summary>
    public long ClampedRequests => Interlocked.Read(ref _clampedRequests);

    public Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (TryClamp(
                context.Request.Headers,
                out var requestedCommitIndex,
                out var clampedCommitIndex,
                out var precedingRecordIndex,
                out var entriesCount))
        {
            var clampedRequests = Interlocked.Increment(ref _clampedRequests);
            if (clampedRequests is 1L || clampedRequests % 100L is 0L)
            {
                logger.LogWarning(
                    "Bounded Raft AppendEntries commit index to the last entry carried by the request. RequestedCommitIndex={RequestedCommitIndex}, ClampedCommitIndex={ClampedCommitIndex}, PrecedingRecordIndex={PrecedingRecordIndex}, EntriesCount={EntriesCount}, TotalClampedRequests={TotalClampedRequests}",
                    requestedCommitIndex,
                    clampedCommitIndex,
                    precedingRecordIndex,
                    entriesCount,
                    clampedRequests);
            }
            else
            {
                logger.LogDebug(
                    "Bounded Raft AppendEntries commit index. RequestedCommitIndex={RequestedCommitIndex}, ClampedCommitIndex={ClampedCommitIndex}, TotalClampedRequests={TotalClampedRequests}",
                    requestedCommitIndex,
                    clampedCommitIndex,
                    clampedRequests);
            }
        }

        return next(context);
    }

    internal static bool TryClamp(
        IHeaderDictionary headers,
        out long requestedCommitIndex,
        out long clampedCommitIndex,
        out long precedingRecordIndex,
        out long entriesCount)
    {
        requestedCommitIndex = 0L;
        clampedCommitIndex = 0L;
        precedingRecordIndex = 0L;
        entriesCount = 0L;

        if (!headers.TryGetValue(MessageTypeHeader, out var messageType) ||
            !StringValues.Equals(messageType, AppendEntriesMessageType) ||
            !TryParseHeader(headers, PrecedingRecordIndexHeader, out precedingRecordIndex) ||
            !TryParseHeader(headers, EntriesCountHeader, out entriesCount) ||
            !TryParseHeader(headers, CommitIndexHeader, out requestedCommitIndex) ||
            precedingRecordIndex < 0L ||
            entriesCount < 0L)
        {
            return false;
        }

        try
        {
            clampedCommitIndex = checked(precedingRecordIndex + entriesCount);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (requestedCommitIndex <= clampedCommitIndex)
            return false;

        headers[CommitIndexHeader] = clampedCommitIndex.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryParseHeader(IHeaderDictionary headers, string name, out long value)
    {
        value = 0L;
        return headers.TryGetValue(name, out var rawValue) &&
               long.TryParse(
                   rawValue.ToString(),
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out value);
    }
}
