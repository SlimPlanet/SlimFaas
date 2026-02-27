using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using SlimFaas.WebSocket;

namespace SlimFaas;



public sealed class PromQlRequest
{
public required string Query { get; init; }
public long? NowUnixSeconds { get; init; }
}

public sealed class ErrorResponse
{
public string Error { get; init; } = string.Empty;
}

public sealed class PromQlResponse
{
public PromQlResponse(double value) => Value = value;
public double Value { get; init; }
}

public sealed record MetricsStoreDebugResponse(
IReadOnlyCollection<string> RequestedMetricNames,
int TimestampBuckets,
int SeriesCount,
int TotalPoints
);



[JsonSerializable(typeof(PromQlRequest))]
[JsonSerializable(typeof(PromQlResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(MetricsStoreDebugResponse))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(ValidationProblemDetails))]
[JsonSerializable(typeof(HttpValidationProblemDetails))]
[JsonSerializable(typeof(WebSocketEnvelope))]
[JsonSerializable(typeof(RegisterPayload))]
[JsonSerializable(typeof(RegisterResponsePayload))]
[JsonSerializable(typeof(AsyncRequestPayload))]
[JsonSerializable(typeof(AsyncCallbackPayload))]
[JsonSerializable(typeof(PublishEventPayload))]
[JsonSerializable(typeof(WebSocketFunctionConfiguration))]
public partial class AppJsonContext : JsonSerializerContext;
