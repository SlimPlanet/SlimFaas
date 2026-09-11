var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHostedService<FibonacciKafkaListener>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run("http://0.0.0.0:8080");
