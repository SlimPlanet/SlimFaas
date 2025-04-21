using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Mvc;

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
IServiceCollection serviceCollection = builder.Services;
serviceCollection.AddSingleton<Fibonacci, Fibonacci>();
serviceCollection.AddCors();
serviceCollection.AddHttpClient();

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
    [FromServices] ILogger<Fibonacci> logger,
    [FromServices] Fibonacci fibonacci,
    FibonacciInput input) =>
{
    logger.LogInformation("Fibonacci Called with input: {Input}", input.Input);
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
        var output = new FibonacciOutput();
        var httpResponse1 =
            client.PostAsJsonAsync(
                "http://slimfaas.slimfaas-demo.svc.cluster.local:5000/function/fibonacci4/fibonacci",
                new FibonacciInput() { Input = input.Input - 1 }, FibonacciInputSerializerContext.Default.FibonacciInput);
        await Task.WhenAll(httpResponse1);

        var result1JsonResponse = await httpResponse1.Result.Content.ReadAsStringAsync();
        var result1 = JsonSerializer.Deserialize(result1JsonResponse,
            FibonacciOutputSerializerContext.Default.FibonacciOutput);
        output.Result = result1.Result;
        logger.LogInformation("Current output: {Result}", output.Result);
        return Results.Ok(output);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error in Fibonacci Recursive Internal");
    }
    return Results.BadRequest(new FibonacciRecursiveOutput());
});

app.MapPost("/send-private-fibonacci-event", (
    [FromServices] ILogger<Fibonacci> logger,
    [FromServices] Fibonacci fibonacci,
    [FromServices] HttpClient client,
    FibonacciInput input) =>
{
    logger.LogInformation("Fibonacci Private Event Called");
    var response = client.PostAsJsonAsync("http://slimfaas.slimfaas-demo.svc.cluster.local:5000/publish-event/fibo-private/fibonacci", input, FibonacciInputSerializerContext.Default.FibonacciInput).Result;
    logger.LogInformation("Response status code: {StatusCode}", response.StatusCode);
    logger.LogInformation("Fibonacci Internal Event End");
});

app.MapPost("/fibonacci-recursive", async (
    [FromServices] ILogger<Fibonacci> logger,
    [FromServices] Fibonacci fibonacci,
    [FromServices] HttpClient client,
    FibonacciInput input) =>
{
    try
    {
        logger.LogInformation("Fibonacci Recursive Internal Called: {Input}", input.Input);
        var output = new FibonacciRecursiveOutput();
        if (input.Input <= 2)
        {
            output.Result = 1;
            output.NumberCall = 1;
            return Results.Ok(output);
        }

        var httpResponse1 =
            client.PostAsJsonAsync(
                "http://slimfaas.slimfaas-demo.svc.cluster.local:5000/function/fibonacci3/fibonacci-recursive",
                new FibonacciInput() { Input = input.Input - 1 }, FibonacciInputSerializerContext.Default.FibonacciInput);
        var httpResponse2 =
            client.PostAsJsonAsync(
                "http://slimfaas.slimfaas-demo.svc.cluster.local:5000/function/fibonacci3/fibonacci-recursive",
                new FibonacciInput() { Input = input.Input - 2 }, FibonacciInputSerializerContext.Default.FibonacciInput);
        await Task.WhenAll(httpResponse1, httpResponse2);

        var result1JsonResponse = await httpResponse1.Result.Content.ReadAsStringAsync();
        var result1 = JsonSerializer.Deserialize(result1JsonResponse,
            FibonacciRecursiveOutputSerializerContext.Default.FibonacciRecursiveOutput);
        logger.LogInformation("Current result1: {Result}", result1JsonResponse);
        var result2JsonResponse = await httpResponse2.Result.Content.ReadAsStringAsync();
        var result2 = JsonSerializer.Deserialize(result2JsonResponse,
            FibonacciRecursiveOutputSerializerContext.Default.FibonacciRecursiveOutput);

        logger.LogInformation("Current result2: {Result}", result2JsonResponse);
        output.Result = result1.Result + result2.Result;
        output.NumberCall = result1.NumberCall + result2.NumberCall + 1;
        logger.LogInformation("Current output: {Result}", output.Result);
        return Results.Ok(output);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error in Fibonacci Recursive Internal");
    }
    return Results.BadRequest(new FibonacciRecursiveOutput());
});

app.MapGet("/error", async () =>
{
    await Task.Delay(100);
    throw new Exception("Error");
});


app.Run();

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
