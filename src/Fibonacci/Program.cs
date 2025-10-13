using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Mvc;

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
IServiceCollection serviceCollection = builder.Services;
serviceCollection.AddSingleton<Fibonacci, Fibonacci>();
serviceCollection.AddCors();
builder.Services.AddHttpClient("internal", c =>
    {
        c.Timeout = TimeSpan.FromSeconds(60);
        c.DefaultRequestVersion = HttpVersion.Version11;
        c.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        KeepAlivePingDelay = TimeSpan.FromSeconds(30),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
        EnableMultipleHttp2Connections = true
    });
serviceCollection.AddSingleton<RequestCounter>();

serviceCollection.Configure<JsonOptions>(options =>
{
    options.JsonSerializerOptions.PropertyNamingPolicy = null;
    options.JsonSerializerOptions.TypeInfoResolver = JsonTypeInfoResolver.Combine(
        options.JsonSerializerOptions.TypeInfoResolver,
        FibonacciInputSerializerContext.Default,
        FibonacciOutputSerializerContext.Default,
        FibonacciRecursiveOutputSerializerContext.Default);
});

WebApplication app = builder.Build();
app.UseCors(builder => builder
    .AllowAnyOrigin()
    .AllowAnyMethod()
    .AllowAnyHeader()
);

app.MapGet("/health", () => "OK");


app.MapGet("/hello/{name}", ([FromServices] ILogger<Fibonacci> logger, string name) =>
{
    logger.LogInformation("Hello Called with name: {Name}", name);
    return $"Hello {name}!";
});

app.MapGet("/download", ([FromServices] ILogger<Fibonacci> logger) =>
{
    logger.LogInformation("Download Called");
    string path = Path.Combine(Directory.GetCurrentDirectory(), "dog.png");
    return Results.File(path, "image/png");
});


app.MapPost("/fibonacci", (
    HttpContext httpContext,
    [FromServices] ILogger<Fibonacci> logger,
    [FromServices] Fibonacci fibonacci,
    FibonacciInput input) =>
{
    logger.LogInformation("Fibonacci Called with input: {Input}", input.Input);
    // Log Authorization header if present
    if (httpContext.Request.Headers.TryGetValue("Authorization", out var authHeader))
    {
        logger.LogInformation("Authorization Header: {Auth}", authHeader.ToString());
    }
    var output = new FibonacciOutput();
    output.Result = fibonacci.Run(input.Input);
    logger.LogInformation("Fibonacci output: {Output}", output.Result);
    return Results.Ok(output);
});

app.MapPost("/fibonacci4", async (
    [FromServices] ILogger<Fibonacci> logger,
    [FromServices] Fibonacci fibonacci,
    [FromServices] HttpClient client,
    FibonacciInput input) =>
{
    try
    {
        logger.LogInformation("Fibonacci4 Internal Called: {Input}", input.Input);

        using var resp = await client.PostAsJsonAsync(
            "http://slimfaas.slimfaas-demo.svc.cluster.local:5000/function/fibonacci4/fibonacci",
            new FibonacciInput { Input = input.Input - 1 },
            FibonacciInputSerializerContext.Default.FibonacciInput);

        resp.EnsureSuccessStatusCode(); // lève si 4xx/5xx

        var result1 = await resp.Content.ReadFromJsonAsync(
            FibonacciOutputSerializerContext.Default.FibonacciOutput);

        return Results.Ok(new FibonacciOutput { Result = result1!.Result });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error in Fibonacci Recursive Internal");
        return Results.BadRequest(new FibonacciRecursiveOutput());
    }
});

app.MapPost("/send-private-fibonacci-event", async (
    [FromServices] ILogger<Fibonacci> logger,
    [FromServices] Fibonacci fibonacci,
    [FromServices] HttpClient client,
    FibonacciInput input) =>
{
    logger.LogInformation("Fibonacci Private Event Called");

    using var response = await client.PostAsJsonAsync(
        "http://slimfaas.slimfaas-demo.svc.cluster.local:5000/publish-event/fibo-private/fibonacci",
        input, FibonacciInputSerializerContext.Default.FibonacciInput);

    logger.LogInformation("Response status code: {StatusCode}", response.StatusCode);
    logger.LogInformation("Fibonacci Internal Event End");
    return Results.StatusCode((int)response.StatusCode);
});

app.MapPost("/fibonacci-recursive", async (
    [FromServices] IHttpClientFactory factory,
    [FromServices] ILogger<Fibonacci> logger,
    [FromServices] Fibonacci fibonacci,
    FibonacciInput input) =>
{
    try
    {
        var client = factory.CreateClient("internal");
        logger.LogInformation("Fibonacci Recursive Internal Called: {Input}", input.Input);

        if (input.Input <= 2)
            return Results.Ok(new FibonacciRecursiveOutput { Result = 1, NumberCall = 1 });

        var t1 = client.PostAsJsonAsync(
            "http://slimfaas.slimfaas-demo.svc.cluster.local:5000/function/fibonacci3/fibonacci-recursive",
            new FibonacciInput { Input = input.Input - 1 },
            FibonacciInputSerializerContext.Default.FibonacciInput);

        var t2 = client.PostAsJsonAsync(
            "http://slimfaas.slimfaas-demo.svc.cluster.local:5000/function/fibonacci3/fibonacci-recursive",
            new FibonacciInput { Input = input.Input - 2 },
            FibonacciInputSerializerContext.Default.FibonacciInput);

        using var r1 = await t1;
        using var r2 = await t2;

        r1.EnsureSuccessStatusCode();
        r2.EnsureSuccessStatusCode();

        var res1 = await r1.Content.ReadFromJsonAsync(
            FibonacciRecursiveOutputSerializerContext.Default.FibonacciRecursiveOutput);
        var res2 = await r2.Content.ReadFromJsonAsync(
            FibonacciRecursiveOutputSerializerContext.Default.FibonacciRecursiveOutput);

        var output = new FibonacciRecursiveOutput
        {
            Result = res1!.Result + res2!.Result,
            NumberCall = res1.NumberCall + res2.NumberCall + 1
        };
        logger.LogInformation("Current output: {Result}", output.Result);
        return Results.Ok(output);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error in Fibonacci Recursive Internal");
        return Results.BadRequest(new FibonacciRecursiveOutput());
    }
});

app.MapGet("/error", async () =>
{
    await Task.Delay(100);
    throw new Exception("Error");
});


app.MapPost("/compute", async ([FromServices] RequestCounter counter, [FromBody] JsonElement body) =>
{
    counter.Begin();

    try
    {
        Console.WriteLine("[fib] body: " + body.GetRawText());
        Console.WriteLine($"InProgress: {counter.InProgress}");
        Console.WriteLine($"State: {counter.State}");
        Console.WriteLine($"Completed: {counter.Completed}");
        Console.WriteLine($"Total: {counter.Completed+counter.InProgress}");
        await Task.Delay(100);

        return Results.Ok(new
        {
            state            = counter.State,
            runningRequests  = counter.InProgress,
            finishedRequests = counter.Completed
        });
    }
    finally
    {
        counter.End();
    }
});



app.Run();

internal class RequestCounter
{
    private int _inProgress   = 0;
    private int _completed    = 0;

    public void Begin() => Interlocked.Increment(ref _inProgress);

    public void End()
    {
        Interlocked.Decrement(ref _inProgress);
        Interlocked.Increment(ref _completed);
    }

    public int  InProgress => _inProgress;
    public int  Completed  => _completed;
    public string State    => _inProgress > 0 ? "processing" : "idle";
}

internal class Fibonacci
{
    public int Run(int i)
    {
        if (i <= 2)
        {
            return 1;
        }

        return Run(i - 1) + Run(i - 2);
    }
}

public record FibonacciInput {
    public int Input { get; set; }
}



[JsonSerializable(typeof(FibonacciInput))]
[JsonSourceGenerationOptions(WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class FibonacciInputSerializerContext : JsonSerializerContext
{
}

public record FibonacciOutput {
    public int Result { get; set; }
}


[JsonSerializable(typeof(FibonacciOutput))]
[JsonSourceGenerationOptions(WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class FibonacciOutputSerializerContext : JsonSerializerContext
{
}

public record FibonacciRecursiveOutput {
    public int Result { get; set; }
    public int NumberCall { get; set; }
}

[JsonSerializable(typeof(FibonacciRecursiveOutput))]
[JsonSourceGenerationOptions(WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class FibonacciRecursiveOutputSerializerContext : JsonSerializerContext
{
}
