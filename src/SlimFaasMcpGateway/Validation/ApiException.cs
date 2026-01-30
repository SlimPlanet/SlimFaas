using System.Net;
using System.Text.Json;

namespace SlimFaasMcpGateway.Api.Validation;

public sealed class ApiException : Exception
{
    public int StatusCode { get; }

    public ApiException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}

public static class ExceptionHandlingExtensions
{
    public static IApplicationBuilder UseApiExceptionHandling(this IApplicationBuilder app)
    {
        return app.Use(async (ctx, next) =>
        {
            try
            {
                await next();
            }
            catch (ApiException ex)
            {
                ctx.Response.StatusCode = ex.StatusCode;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                var payload = JsonSerializer.Serialize(new { error = ex.Message });
                await ctx.Response.WriteAsync(payload);
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                var payload = JsonSerializer.Serialize(new { error = "Internal server error", detail = ex.Message });
                await ctx.Response.WriteAsync(payload);
            }
        });
    }
}
