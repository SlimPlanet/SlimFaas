using SlimFaas.Kubernetes;
using SlimFaas.Workers;

namespace SlimFaas;

public static class DebugRoutes
{
    public static IEndpointRouteBuilder MapDebugRoutes(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/debug");

        group.MapPost("/promql/eval", async (
                PromQlRequest req,
                PromQlMiniEvaluator eval,
                IMetricsScrapingGuard guard,
                IRequestedMetricsRegistry registry,
                IReplicasService replicas,
                IScalerProvider provider,
                CancellationToken cancellationToken) =>
            {
                // Active le scraping côté PromQL
                guard.EnablePromql();

                // Enregistre les métriques demandées par cette requête
                registry.RegisterFromQuery(req.Query);

                if (string.IsNullOrWhiteSpace(req.Query))
                    return Results.BadRequest(new ErrorResponse { Error = "query is required" });

                double result;
                try
                {
                    if (req.Source is not null)
                    {
                        var function = replicas.Deployments.Functions.FirstOrDefault(f => f.Deployment == req.Deployment);
                        if (function?.Scale is not { } scale ||
                            ExternalMetricsSource.Resolve(function.Namespace, function.Deployment, scale, req.Source) is null)
                            return Results.BadRequest(new ErrorResponse { Error = "source requires a deployment and a configured external source" });
                        var observation = await provider.GetAsync(new(function.Namespace, function.Deployment, scale,
                            new ScaleTrigger(Query: req.Query, Source: req.Source),
                            req.NowUnixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()), cancellationToken);
                        if (observation.State != ScalerState.Valid)
                            return Results.BadRequest(new ErrorResponse { Error = $"External source signal is {observation.State}" });
                        result = observation.Value;
                    }
                    else
                        result = eval.Evaluate(req.Query, req.NowUnixSeconds, req.Deployment);

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
