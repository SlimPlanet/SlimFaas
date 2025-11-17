using System.Text.Json.Serialization;

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

[JsonSerializable(typeof(PromQlRequest))]
[JsonSerializable(typeof(PromQlResponse))]
[JsonSerializable(typeof(ErrorResponse))]
public partial class AppJsonContext : JsonSerializerContext;
