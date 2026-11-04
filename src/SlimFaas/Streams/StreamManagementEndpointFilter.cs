namespace SlimFaas.Streams;

internal sealed class StreamManagementEndpointFilter(IStreamService service) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        service.CanManage(context.HttpContext) ? next(context) : ValueTask.FromResult<object?>(TypedResults.NotFound());
}
