using SlimFaas.Kubernetes;
using SlimFaas.Workers;

namespace SlimFaas;

public static class DebugRoutes
{
    public static IEndpointRouteBuilder MapDebugRoutes(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/debug");

        group.MapPost("/promql/eval", (
                PromQlRequest req,
                PromQlMiniEvaluator eval,
                IMetricsScrapingGuard guard,
                IRequestedMetricsRegistry registry,
                IReplicasService replicasService,
                IExternalMetricsSourceHealthRegistry externalHealthRegistry) =>
            {
                // Active le scraping côté PromQL
                guard.EnablePromql();

                // Enregistre les métriques demandées par cette requête
                registry.RegisterFromQuery(req.Query);

                if (string.IsNullOrWhiteSpace(req.Query))
                    return Results.BadRequest(new ErrorResponse { Error = "query is required" });
                if (string.IsNullOrWhiteSpace(req.Deployment))
                    return Results.BadRequest(new ErrorResponse { Error = "deployment is required" });

                double result;
                try
                {
                    CompiledPromQlQuery query = PromQlQueryCompiler.Compile(req.Query);
                    string scope = req.Deployment;
                    TimeSpan? externalLookback = null;
                    if (!string.IsNullOrWhiteSpace(req.Source))
                    {
                        DeploymentInformation? deployment = replicasService.Deployments.Functions.FirstOrDefault(
                            function => string.Equals(
                                function.Deployment,
                                req.Deployment,
                                StringComparison.Ordinal));
                        ScaleSource? source = deployment?.Scale?.Sources.FirstOrDefault(
                            source => string.Equals(source.Name, req.Source, StringComparison.Ordinal));
                        if (source is null)
                        {
                            return Results.BadRequest(new ErrorResponse
                            {
                                Error = $"source '{req.Source}' is not configured for deployment '{req.Deployment}'"
                            });
                        }

                        scope = MetricsSourceScope.ForExternal(req.Deployment, source.Name);
                        long now = req.NowUnixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        if (!externalHealthRegistry.IsHealthy(
                                scope,
                                now,
                                query.ReferencedMetricNames,
                                out string reason))
                        {
                            return Results.BadRequest(new ErrorResponse
                            {
                                Error = $"source '{source.Name}' is unavailable: {reason}"
                            });
                        }

                        if (externalHealthRegistry.TryGet(
                                scope,
                                out ExternalMetricsSourceHealth? health) &&
                            health is not null)
                        {
                            externalLookback = TimeSpan.FromMilliseconds(
                                health.ScrapeIntervalMilliseconds * 3L);
                        }
                    }

                    result = externalLookback.HasValue
                        ? eval.Evaluate(query, req.NowUnixSeconds, scope, externalLookback.Value)
                        : eval.Evaluate(query, req.NowUnixSeconds, scope);

                    // IMPORTANT : filtrer NaN / ±Infinity
                    if (double.IsNaN(result) || double.IsInfinity(result))
                    {
                        return Results.BadRequest(new ErrorResponse
                        {
                            Error = "PromQL result is NaN or Infinity (probably no data or division by zero)."
                        });
                    }
                }
                catch (FormatException fe)
                {
                    return Results.BadRequest(new ErrorResponse { Error = fe.Message });
                }
                catch (Exception ex)
                {
                    return Results.Problem(
                        title: "Evaluation error",
                        detail: ex.Message,
                        statusCode: 500
                    );
                }

                return Results.Ok(new PromQlResponse(result));
            })
            .WithName("PromQlEvaluate")
            .Produces<PromQlResponse>(StatusCodes.Status200OK)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapGet("/store", (IMetricsStore store, IRequestedMetricsRegistry registry) =>
            {
                var snapshot = store.Snapshot();

                int timestampBuckets = snapshot.Count;
                int seriesCount = 0;
                int totalPoints = 0;

                foreach (var tsEntry in snapshot)                  // ts
                {
                    foreach (var depEntry in tsEntry.Value)        // deployment
                    {
                        foreach (var podEntry in depEntry.Value)   // podIp
                        {
                            var metrics = podEntry.Value;          // Dictionary<string,double>
                            int metricCount = metrics.Count;

                            totalPoints += metricCount;
                            seriesCount += metricCount;
                        }
                    }
                }

                var response = new MetricsStoreDebugResponse(
                    RequestedMetricNames: registry.GetRequestedMetricNames(),
                    TimestampBuckets: timestampBuckets,
                    SeriesCount: seriesCount,
                    TotalPoints: totalPoints
                );

                return Results.Ok(response);
            })
            .WithName("MetricsStoreDebug")
            .Produces<MetricsStoreDebugResponse>(StatusCodes.Status200OK);

        return app;
    }
}
