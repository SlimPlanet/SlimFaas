using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using SlimFaasMcpGateway.Auth;
using SlimFaasMcpGateway.Data;
using SlimFaasMcpGateway.Dto;
using SlimFaasMcpGateway.Gateway;
using SlimFaasMcpGateway.Options;
using SlimFaasMcpGateway.Services;
using SlimFaasMcpGateway.Api.Validation;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<GatewayOptions>().Bind(builder.Configuration).ValidateDataAnnotations();
builder.Services.AddOptions<SecurityOptions>().Bind(builder.Configuration.GetSection("Security")).ValidateDataAnnotations();
builder.Services.AddOptions<ObservabilityOptions>().Bind(builder.Configuration.GetSection("Observability")).ValidateDataAnnotations();
builder.Services.AddOptions<GatewayHttpOptions>().Bind(builder.Configuration.GetSection("Gateway")).ValidateDataAnnotations();

builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddMemoryCache();

builder.Services.AddDbContext<GatewayDbContext>(opt =>
{
    var cs = builder.Configuration.GetConnectionString("Sqlite") ?? "Data Source=slimfaas_mcp_gateway.db";
    opt.UseSqlite(cs);
    
    // Use compiled model for NativeAOT compatibility
    #if RELEASE
    opt.UseModel(SlimFaasMcpGateway.Data.CompiledModels.GatewayDbContextModel.Instance);
    #endif
    
    opt.EnableSensitiveDataLogging(false);
    opt.EnableDetailedErrors(false);
});

builder.Services.AddHttpClient("upstream", (sp, http) =>
{
    var opts = sp.GetRequiredService<IOptions<GatewayHttpOptions>>().Value;
    http.Timeout = TimeSpan.FromSeconds(Math.Clamp((double)opts.HttpClient.TimeoutSeconds, 5.0, 600.0));
});

builder.Services.AddHealthChecks();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, SlimFaasMcpGateway.Serialization.ApiJsonContext.Default);
});

// Observability (OpenTelemetry) - optional
var obs = builder.Configuration.GetSection("Observability:OpenTelemetry").Get<OpenTelemetryOptions>() ?? new();
if (obs.Enabled)
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("SlimFaasMcpGateway"))
        .WithTracing(tracerProviderBuilder =>
        {
            tracerProviderBuilder
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation();

            if (string.Equals(obs.Exporter, "Otlp", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(obs.OtlpEndpoint))
            {
                tracerProviderBuilder.AddOtlpExporter(o => o.Endpoint = new Uri(obs.OtlpEndpoint));
            }
            else
            {
                tracerProviderBuilder.AddConsoleExporter();
            }
        })
        .WithMetrics(meterProviderBuilder =>
        {
            meterProviderBuilder
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation();

            if (string.Equals(obs.Exporter, "Otlp", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(obs.OtlpEndpoint))
            {
                meterProviderBuilder.AddOtlpExporter(o => o.Endpoint = new Uri(obs.OtlpEndpoint));
            }
        });

    builder.Logging.AddOpenTelemetry(o =>
    {
        o.IncludeFormattedMessage = true;
        o.IncludeScopes = true;

        if (string.Equals(obs.Exporter, "Otlp", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(obs.OtlpEndpoint))
        {
            o.AddOtlpExporter(exp => exp.Endpoint = new Uri(obs.OtlpEndpoint));
        }
        else
        {
            o.AddConsoleExporter();
        }
    });
}

// Services
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<ITenantService, TenantService>();
builder.Services.AddScoped<IConfigurationService, ConfigurationService>();
builder.Services.AddScoped<IDeploymentService, DeploymentService>();
builder.Services.AddScoped<IMcpDiscoveryService, McpDiscoveryService>();

builder.Services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
builder.Services.AddSingleton<IJwtValidator, JwtValidator>();
builder.Services.AddSingleton<IDpopValidator, DpopValidator>();

builder.Services.AddSingleton<ICatalogCache, CatalogCache>();
builder.Services.AddSingleton<IRateLimiterService, RateLimiterService>();
builder.Services.AddSingleton<ICatalogOverrideApplier, CatalogOverrideApplier>();

builder.Services.AddScoped<IGatewayResolver, GatewayResolver>();
builder.Services.AddScoped<IGatewayProxyHandler, GatewayProxyHandler>();

// CORS for the SPA (dev friendly)
builder.Services.AddCors(o =>
{
    o.AddPolicy("dev", p => p
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowAnyOrigin());
});

var app = builder.Build();

// DB init
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
    await db.Database.EnsureCreatedAsync();

    // Ensure default tenant exists
    var tenants = scope.ServiceProvider.GetRequiredService<ITenantService>();
    await tenants.EnsureDefaultTenantAsync(CancellationToken.None);
}

// Middleware
app.UseApiExceptionHandling();
app.UseCors("dev");

// Serve static files from wwwroot (SPA)
app.UseStaticFiles();

// Prometheus metrics endpoint (/metrics) + http metrics
app.UseHttpMetrics();
app.MapMetrics();

app.MapHealthChecks("/health");

// API
var api = app.MapGroup("/api");

api.MapGet("/environments", (IOptions<GatewayOptions> opts) =>
{
    var envs = opts.Value.GetEnvironmentsOrDefault();
    return Results.Ok(new EnvironmentListDto(envs));
});

var tenantsApi = api.MapGroup("/tenants");

tenantsApi.MapGet("", async (ITenantService svc, CancellationToken ct) =>
{
    var list = await svc.ListAsync(ct);
    return Results.Ok(list);
});

tenantsApi.MapPost("", async (HttpContext ctx, ITenantService svc, TenantCreateRequest req, CancellationToken ct) =>
{
    var author = AuditAuthor.From(ctx);
    var created = await svc.CreateAsync(req, author, ct);
    return Results.Created($"/api/tenants/{created.Id}", created);
});

tenantsApi.MapPut("/{id:guid}", async (HttpContext ctx, ITenantService svc, Guid id, TenantUpdateRequest req, CancellationToken ct) =>
{
    var author = AuditAuthor.From(ctx);
    var updated = await svc.UpdateAsync(id, req, author, ct);
    return Results.Ok(updated);
});

tenantsApi.MapDelete("/{id:guid}", async (HttpContext ctx, ITenantService svc, Guid id, CancellationToken ct) =>
{
    var author = AuditAuthor.From(ctx);
    await svc.DeleteAsync(id, author, ct);
    return Results.NoContent();
});

var cfgApi = api.MapGroup("/configurations");

cfgApi.MapGet("", async (IConfigurationService svc, CancellationToken ct) =>
{
    var list = await svc.ListAsync(ct);
    return Results.Ok(list);
});

cfgApi.MapGet("/{id:guid}", async (IConfigurationService svc, Guid id, CancellationToken ct) =>
{
    var dto = await svc.GetAsync(id, ct);
    return Results.Ok(dto);
});

cfgApi.MapPost("", async (HttpContext ctx, IConfigurationService svc, ConfigurationCreateOrUpdateRequest req, CancellationToken ct) =>
{
    var author = AuditAuthor.From(ctx);
    var dto = await svc.CreateAsync(req, author, ct);
    return Results.Created($"/api/configurations/{dto.Id}", dto);
});

cfgApi.MapPut("/{id:guid}", async (HttpContext ctx, IConfigurationService svc, Guid id, ConfigurationCreateOrUpdateRequest req, CancellationToken ct) =>
{
    var author = AuditAuthor.From(ctx);
    var dto = await svc.UpdateAsync(id, req, author, ct);
    return Results.Ok(dto);
});

cfgApi.MapDelete("/{id:guid}", async (HttpContext ctx, IConfigurationService svc, Guid id, CancellationToken ct) =>
{
    var author = AuditAuthor.From(ctx);
    await svc.DeleteAsync(id, author, ct);
    return Results.NoContent();
});

cfgApi.MapPost("/{id:guid}/load-catalog", async (IMcpDiscoveryService svc, Guid id, CancellationToken ct) =>
{
    var yaml = await svc.LoadCatalogYamlAsync(id, ct);
    return Results.Ok(new LoadCatalogResponseDto(yaml));
});

cfgApi.MapGet("/{id:guid}/history", async (IAuditService audit, Guid id, CancellationToken ct) =>
{
    var list = await audit.ListAsync("configuration", id, ct);
    return Results.Ok(list);
});

cfgApi.MapGet("/{id:guid}/snapshot/{index:int}", async (IAuditService audit, Guid id, int index, CancellationToken ct) =>
{
    var json = await audit.ReconstructJsonAsync("configuration", id, index, ct);
    var redacted = SecretRedaction.RedactSnapshotJson(json);
    return Results.Text(redacted, "application/json; charset=utf-8");
});

cfgApi.MapGet("/{id:guid}/diff", async (IAuditService audit, Guid id, int from, int to, CancellationToken ct) =>
{
    var diff = await audit.DiffAsync("configuration", id, from, to, ct);
    var redacted = SecretRedaction.RedactDiff(diff);
    return Results.Ok(redacted);
});

cfgApi.MapGet("/{id:guid}/deployments", async (IDeploymentService svc, Guid id, CancellationToken ct) =>
{
    var overview = await svc.GetOverviewAsync(id, ct);
    return Results.Ok(overview);
});

cfgApi.MapPut("/{id:guid}/deployments/{environment}", async (HttpContext ctx, IDeploymentService svc, Guid id, string environment, SetDeploymentRequest req, CancellationToken ct) =>
{
    var author = AuditAuthor.From(ctx);
    await svc.SetDeploymentAsync(id, environment, req.DeployedAuditIndex, author, ct);
    return Results.NoContent();
});

// Gateway proxy routes
app.MapMethods("/gateway/mcp/{tenant}/{environment}/{configurationName}", new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS" },
    async (HttpContext ctx, IGatewayProxyHandler proxy, string tenant, string environment, string configurationName, CancellationToken ct) =>
{
    await proxy.HandleAsync(ctx, tenant, environment, configurationName, rest: "", ct);
});

app.MapMethods("/gateway/mcp/{tenant}/{environment}/{configurationName}/{**rest}", new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS" },
    async (HttpContext ctx, IGatewayProxyHandler proxy, string tenant, string environment, string configurationName, string rest, CancellationToken ct) =>
{
    await proxy.HandleAsync(ctx, tenant, environment, configurationName, rest, ct);
});

// SPA fallback - serve index.html for all non-API routes
app.MapFallbackToFile("index.html");

app.Run();

static class AuditAuthor
{
    public static string From(HttpContext ctx)
    {
        var author = ctx.Request.Headers["X-Audit-Author"].ToString();
        return string.IsNullOrWhiteSpace(author) ? "unknown" : author.Trim();
    }
}

static class SecretRedaction
{
    // Never return plaintext token. Also avoid returning encrypted token (still sensitive).
    public static string RedactSnapshotJson(string json)
    {

        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            if (node is System.Text.Json.Nodes.JsonObject obj)
            {
                if (obj.ContainsKey("DiscoveryJwtTokenProtected"))
                    obj["DiscoveryJwtTokenProtected"] = "***redacted***";
            }
            return node?.ToJsonString(SlimFaasMcpGateway.Audit.AppJsonOptions.Default) ?? json;
        }
        catch
        {
            return json;
        }
    }

    public static AuditDiffDto RedactDiff(AuditDiffDto diff)
    {
        var ops = diff.Patch.Select(op =>
        {
            if (op.Path.Contains("DiscoveryJwtTokenProtected", StringComparison.OrdinalIgnoreCase))
                return op with { Value = "***redacted***" };
            return op;
        }).ToList();

        return diff with { Patch = ops };
    }
}
