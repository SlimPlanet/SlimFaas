// Small, opt-in consumers used by the Live Stream recording.
internal static class AgentTourEndpoints
{
    internal static void MapAgentTour(this WebApplication app, string? role)
    {
        if (role is not ("evaluator" or "audit")) return;

        // Each process starts with its own copy of the tool definition.
        string? cachedTool = "catalog-v1";
        string? handledResult = null;
        app.MapPost("/tour/tools/invalidate", () =>
        {
            Interlocked.Exchange(ref cachedTool, null);
            return TypedResults.NoContent();
        });
        app.MapPost("/tour/results/{resultId}", (string resultId) =>
        {
            // A retry for the same ID produces the same observation.
            Interlocked.Exchange(ref handledResult, resultId);
            return TypedResults.NoContent();
        });
        app.MapGet("/tour/observation", () => TypedResults.Text(
            $"{role} {Volatile.Read(ref cachedTool) ?? "invalidated"} {Volatile.Read(ref handledResult) ?? "pending"}"));
    }
}
