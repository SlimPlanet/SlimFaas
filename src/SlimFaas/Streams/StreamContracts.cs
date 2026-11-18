using System.Text.Json.Serialization;

namespace SlimFaas.Streams;

/// <summary>Accessible Stream inventory and retained positions.</summary>
public sealed record StreamSummary(string Name, string Visibility, long RetainedMessageCount,
    int VisibleFunctionCount, DateTimeOffset? OldestAppendedAt, DateTimeOffset? NewestAppendedAt,
    int MaxAgeSeconds, int MaxLength, string? EarliestMessageId, string? LatestMessageId,
    string Generation, DateTimeOffset CapturedAt, long Revision, string Availability = "Available");
/// <summary>Independent function consumption; pending, callback and retry counters overlap lag.</summary>
public sealed record StreamFunctionSummary(string Stream, string Function, bool Active, string Generation,
    long StartSequence, long AcknowledgedThrough, long LastAcknowledged, long RetentionGapCount,
    long Lag, long PendingCount, long WaitingForCallbackCount, long RetryScheduledCount, long FailedCount,
    long RunnableCount, int? LastStatus, DateTimeOffset CapturedAt, long Revision);
/// <summary>Retained message metadata; the body is available only through the explicit body route.</summary>
public sealed record StreamMessageMetadata(string Id, DateTimeOffset AppendedAt, string ContentType, long PayloadSize,
    DateTimeOffset CapturedAt, long Revision);
/// <summary>A bounded, stable page of accessible resources.</summary>
public sealed record StreamPage<T>(IReadOnlyList<T> Items, string? NextCursor, DateTimeOffset CapturedAt, long Revision);
/// <summary>Durable append receipt, not function processing success.</summary>
public sealed record StreamAppendReceipt(string Stream, string MessageId);
/// <summary>An explicitly reserved attempt belonging to one function.</summary>
public sealed record StreamReservation(string Stream, string Function, string MessageId, string ElementId,
    int Attempt, DateTimeOffset Deadline);
/// <summary>Safe request error and, when applicable, the current retention boundary.</summary>
public sealed record StreamApiError(string Error, string? EarliestMessageId = null);

[JsonSerializable(typeof(StreamPage<StreamSummary>))]
[JsonSerializable(typeof(StreamPage<StreamFunctionSummary>))]
[JsonSerializable(typeof(StreamPage<StreamMessageMetadata>))]
[JsonSerializable(typeof(StreamSummary))]
[JsonSerializable(typeof(StreamFunctionSummary))]
[JsonSerializable(typeof(StreamMessageMetadata))]
[JsonSerializable(typeof(StreamAppendReceipt))]
[JsonSerializable(typeof(StreamReservation))]
[JsonSerializable(typeof(StreamApiError))]
internal partial class StreamJsonContext : JsonSerializerContext;
