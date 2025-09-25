using System.Net.Mime;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Add OpenAPI/Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Calculator API",
        Version = "v1",
        Description = "A tiny, well-documented Minimal API for basic arithmetic operations (add, multiply, divide).",
        Contact = new OpenApiContact
        {
            Name = "API Support",
            Email = "support@example.com"
        },
        License = new OpenApiLicense
        {
            Name = "MIT License"
        }
    });

    // Include XML comments if produced (helpful for DTOs)
    var xmlPath = Path.Combine(AppContext.BaseDirectory, $"{typeof(Program).Assembly.GetName().Name}.xml");
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
    }

    // Optional: nicer schema ids for records
    options.CustomSchemaIds(t => t.FullName!.Replace("+", "."));
});

var app = builder.Build();

// Enable Swagger by default (dev & prod)
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Calculator API v1");
    c.DocumentTitle = "Calculator API";
});

// Basic health route
app.MapGet("/", () => Results.Redirect("/swagger"))
   .ExcludeFromDescription();

var group = app.MapGroup("/api/v1/calculator")
    .WithTags("Calculator")
    .WithOpenApi(g => new(g)
    {
        Summary = "Calculator operations",
        Description = "Group of basic arithmetic endpoints operating on two numbers."
    });


// ADD
group.MapPost("/add",
    Results<Ok<CalculationResult>, BadRequest<ProblemDetails>> (CalculationRequest body) =>
    {
        var result = body.A + body.B;
        return TypedResults.Ok(new CalculationResult("add", body.A, body.B, result));
    })
.WithName("Add")
.Accepts<CalculationRequest>("application/json")
.Produces<CalculationResult>(StatusCodes.Status200OK, "application/json")
.Produces<ProblemDetails>(StatusCodes.Status400BadRequest, "application/json")
.WithOpenApi(op =>
{
    op.Summary = "Add two numbers";
    op.Description = "Returns the sum of operands `A` and `B`. Uses IEEE 754 double-precision.";
    EnsureRequestBody(op, "Request body containing the two operands.", required: true);
    op.Responses["200"].Description = "Successful addition result.";
    op.Responses["400"].Description = "Invalid request payload.";
    return op;
});

// MULTIPLY
group.MapPost("/multiply",
    Results<Ok<CalculationResult>, BadRequest<ProblemDetails>> (CalculationRequest body) =>
    {
        var result = body.A * body.B;
        return TypedResults.Ok(new CalculationResult("multiply", body.A, body.B, result));
    })
.WithName("Multiply")
.Accepts<CalculationRequest>("application/json")
.Produces<CalculationResult>(StatusCodes.Status200OK, "application/json")
.Produces<ProblemDetails>(StatusCodes.Status400BadRequest, "application/json")
.WithOpenApi(op =>
{
    op.Summary = "Multiply two numbers";
    op.Description = "Returns the product of operands `A` and `B`. Uses IEEE 754 double-precision.";
    EnsureRequestBody(op, "Request body containing the two operands.", required: true);
    op.Responses["200"].Description = "Successful multiplication result.";
    op.Responses["400"].Description = "Invalid request payload.";
    return op;
});

// DIVIDE
group.MapPost("/divide",
    Results<Ok<CalculationResult>, BadRequest<ProblemDetails>> (CalculationRequest body) =>
    {
        if (body.B == 0)
        {
            return TypedResults.BadRequest(CreateProblem(
                title: "Division by zero",
                detail: "The divisor 'B' must be non-zero.",
                instance: "/api/v1/calculator/divide"));
        }
        var result = body.A / body.B;
        return TypedResults.Ok(new CalculationResult("divide", body.A, body.B, result,
            double.IsFinite(result) ? null : "Result is non-finite (Infinity or NaN)."));
    })
.WithName("Divide")
.Accepts<CalculationRequest>("application/json")
.Produces<CalculationResult>(StatusCodes.Status200OK, "application/json")
.Produces<ProblemDetails>(StatusCodes.Status400BadRequest, "application/json")
.WithOpenApi(op =>
{
    op.Summary = "Divide two numbers";
    op.Description = "Returns `A / B`. `B` must be non-zero. Uses IEEE 754 double-precision.";
    EnsureRequestBody(op, "Request body containing the two operands.", required: true);
    op.Responses["200"].Description = "Successful division result.";
    op.Responses["400"].Description = "Invalid request payload (e.g., division by zero).";
    return op;
});

app.Run();

// Helper to mutate/initialize RequestBody (because OpenApiRequestBody is a class, not a record)
static void EnsureRequestBody(OpenApiOperation op, string description, bool required = true)
{
    op.RequestBody ??= new OpenApiRequestBody();
    op.RequestBody.Description = description;
    op.RequestBody.Required = required;

}
static ProblemDetails CreateProblem(string title, string detail, string instance, int status = StatusCodes.Status400BadRequest)
    => new()
    {
        Title = title,
        Detail = detail,
        Instance = instance,
        Status = status,
        Type = "about:blank"
    };

// DTOs
/// <summary>
/// A simple arithmetic request with two operands.
/// </summary>
/// <param name="A">First operand.</param>
/// <param name="B">Second operand.</param>
public record CalculationRequest(double A, double B);

/// <summary>
/// Standard operation result.
/// </summary>
/// <param name="Operation">Name of the operation ("add", "multiply", "divide").</param>
/// <param name="A">First operand echoed back.</param>
/// <param name="B">Second operand echoed back.</param>
/// <param name="Result">Computed value.</param>
/// <param name="Note">Optional extra information (e.g., precision notes).</param>
public record CalculationResult(string Operation, double A, double B, double Result, string? Note = null);



// Helpers
