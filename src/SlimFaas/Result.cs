namespace SlimFaas;



public record ErrorResult(string Key, string? Description = null);


public record ResultWithError<T>(T? Data, ErrorResult? Error = null)
{
    public T? Data { get; set; } = Data;

    public ErrorResult? Error { get; set; } = Error;

    public bool IsSuccess => Error == null;

}
