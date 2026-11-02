using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class TourEndpoints
{
    internal static void MapTourEndpoints(this IEndpointRouteBuilder endpoints, string slimFaasBaseUrl, bool enabled)
    {
        if (!enabled)
        {
            return;
        }

        endpoints.MapPost("/tour/dlq/{tourId}", async Task<IResult> (
            string tourId, JsonElement body, HttpContext context, IHttpClientFactory factory,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        {
            ILogger logger = loggerFactory.CreateLogger("Fibonacci.Tour");
            string elementId = context.Request.Headers["SlimFaas-Element-Id"].ToString();
            if (tourId.Length is < 1 or > 100 || !tourId.All(character => char.IsAsciiLetterOrDigit(character) || character == '-') ||
                body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("orderId", out _) || elementId.Length == 0)
            {
                logger.LogInvalidTourRequest();
                return TypedResults.Text("A safe tour ID, orderId and async execution ID are required.", statusCode: 400);
            }

            using HttpClient client = factory.CreateClient("internal");
            using HttpResponseMessage dependency = await client.GetAsync(
                new Uri($"{slimFaasBaseUrl}/data/sets/{tourId}-dlq-ready"), cancellationToken);
            if (dependency.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogTourDependencyUnavailable(tourId);
                return TypedResults.Text("Demo dependency is unavailable. Repair it before replaying.", statusCode: 422);
            }
            dependency.EnsureSuccessStatusCode();
            string ready = await dependency.Content.ReadAsStringAsync(cancellationToken);
            if (!string.Equals(ready, "ready", StringComparison.Ordinal))
            {
                logger.LogTourDependencyUnavailable(tourId);
                return TypedResults.Text("Demo dependency must contain ready.", statusCode: 422);
            }

            using var content = new StringContent(elementId, Encoding.UTF8, "text/plain");
            using HttpResponseMessage completion = await client.PostAsync(
                new Uri($"{slimFaasBaseUrl}/data/sets/{tourId}-dlq-completed?ttl=600000"), content, cancellationToken);
            completion.EnsureSuccessStatusCode();
            logger.LogTourCompleted(tourId, elementId);
            return TypedResults.Text(elementId);
        }).WithSummary("Opt-in guided-tour DLQ repair fixture");
    }
}
