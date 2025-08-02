using System.Text.Json;
using SlimFaasMcp.Models;
using Endpoint = SlimFaasMcp.Models.Endpoint;

namespace SlimFaasMcp.Services;

public interface IRemoteSchemaService
{
    Task<JsonDocument> GetSchemaAsync(string url, string? baseUrl = null, string? authHeader = null);
    IEnumerable<Endpoint> ParseEndpoints(JsonDocument schema);
}
