using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace SlimData.Tests;

public sealed class RaftAppendEntriesCommitIndexGuardTests
{
    [Fact]
    public async Task AppendEntries_commit_index_is_bounded_to_last_transmitted_entry()
    {
        var context = CreateAppendEntriesContext(
            precedingRecordIndex: 99L,
            entriesCount: 5L,
            commitIndex: 120L);
        var guard = new RaftAppendEntriesCommitIndexGuard(
            NullLogger<RaftAppendEntriesCommitIndexGuard>.Instance);
        var nextCalled = false;

        await guard.InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.True(nextCalled);
        Assert.Equal("104", context.Request.Headers["X-Raft-Commit-Index"]);
        Assert.Equal(1L, guard.ClampedRequests);
    }

    [Theory]
    [InlineData(104L)]
    [InlineData(100L)]
    public async Task Valid_AppendEntries_commit_index_is_not_changed(long commitIndex)
    {
        var context = CreateAppendEntriesContext(
            precedingRecordIndex: 99L,
            entriesCount: 5L,
            commitIndex);
        var guard = new RaftAppendEntriesCommitIndexGuard(
            NullLogger<RaftAppendEntriesCommitIndexGuard>.Instance);

        await guard.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(commitIndex.ToString(), context.Request.Headers["X-Raft-Commit-Index"]);
        Assert.Equal(0L, guard.ClampedRequests);
    }

    [Fact]
    public async Task AppendEntries_heartbeat_is_bounded_to_preceding_record()
    {
        var context = CreateAppendEntriesContext(
            precedingRecordIndex: 42L,
            entriesCount: 0L,
            commitIndex: 50L);
        var guard = new RaftAppendEntriesCommitIndexGuard(
            NullLogger<RaftAppendEntriesCommitIndexGuard>.Instance);

        await guard.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal("42", context.Request.Headers["X-Raft-Commit-Index"]);
        Assert.Equal(1L, guard.ClampedRequests);
    }

    [Fact]
    public async Task Non_AppendEntries_request_is_not_changed()
    {
        var context = CreateAppendEntriesContext(
            precedingRecordIndex: 99L,
            entriesCount: 5L,
            commitIndex: 120L);
        context.Request.Headers["X-Raft-Message-Type"] = "RequestVote";
        var guard = new RaftAppendEntriesCommitIndexGuard(
            NullLogger<RaftAppendEntriesCommitIndexGuard>.Instance);

        await guard.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal("120", context.Request.Headers["X-Raft-Commit-Index"]);
        Assert.Equal(0L, guard.ClampedRequests);
    }

    [Fact]
    public async Task Invalid_or_overflowing_headers_are_left_to_DotNext_validation()
    {
        var context = CreateAppendEntriesContext(
            precedingRecordIndex: long.MaxValue,
            entriesCount: 1L,
            commitIndex: long.MaxValue);
        var guard = new RaftAppendEntriesCommitIndexGuard(
            NullLogger<RaftAppendEntriesCommitIndexGuard>.Instance);

        await guard.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(long.MaxValue.ToString(), context.Request.Headers["X-Raft-Commit-Index"]);
        Assert.Equal(0L, guard.ClampedRequests);
    }

    private static DefaultHttpContext CreateAppendEntriesContext(
        long precedingRecordIndex,
        long entriesCount,
        long commitIndex)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Raft-Message-Type"] = "AppendEntries";
        context.Request.Headers["X-Raft-Preceding-Record-Index"] = precedingRecordIndex.ToString();
        context.Request.Headers["X-Raft-Entries-Count"] = entriesCount.ToString();
        context.Request.Headers["X-Raft-Commit-Index"] = commitIndex.ToString();
        return context;
    }
}
