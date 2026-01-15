namespace SlimFaas.Endpoints;

public static class SlimFaasEndpointsExtensions
{
    public static IEndpointRouteBuilder MapSlimFaasEndpoints(this IEndpointRouteBuilder app)
    {
        // Enregistrer tous les endpoints
        app.MapStatusEndpoints();
        app.MapJobEndpoints();
        app.MapJobScheduleEndpoints();
        app.MapSyncFunctionEndpoints();
        app.MapAsyncFunctionEndpoints();
        app.MapEventEndpoints();

        return app;
    }
}

