using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Prometheus;
using SlimFaasKafka.Config;
using SlimFaasKafka.Kafka;
using SlimFaasKafka.Services;
using SlimFaasKafka.Workers;

var builder = WebApplication.CreateBuilder(args);

// Configuration standard : appsettings.json + variables d'environnement
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

// Options
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection("Kafka"));
builder.Services.Configure<SlimFaasOptions>(builder.Configuration.GetSection("SlimFaas"));
builder.Services.Configure<BindingsOptions>(builder.Configuration.GetSection("SlimFaasKafka"));

// HTTP client pour SlimFaas
builder.Services.AddHttpClient<ISlimFaasClient, SlimFaasClient>((sp, client) =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SlimFaasOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.HttpTimeoutSeconds));
});

// Worker Kafka
builder.Services.AddSingleton<IKafkaLagProvider, KafkaLagProvider>();
builder.Services.AddHostedService<KafkaMonitoringWorker>();

// API minimal + Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Middleware Prometheus pour instrumenter les requêtes HTTP
app.UseHttpMetrics();

// Endpoint /metrics (texte Prometheus)
app.MapMetrics();

// Endpoints applicatifs

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Retourne la config des bindings courants (pour debug)
app.MapGet("/bindings", (
    Microsoft.Extensions.Options.IOptionsMonitor<BindingsOptions> options) =>
{
    return Results.Ok(options.CurrentValue);
});

// Endpoint manuel pour déclencher un wake
app.MapPost("/wake/{functionName}", async (
    string functionName,
    ISlimFaasClient client,
    CancellationToken ct) =>
{
    await client.WakeAsync(functionName, ct);
    return Results.Accepted();
});

app.Run();
