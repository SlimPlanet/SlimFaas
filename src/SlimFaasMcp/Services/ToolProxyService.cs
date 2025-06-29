using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Models;
using Services;
using YamlDotNet.Serialization;

namespace Services
{
    public class ToolProxyService
    {
        private readonly SwaggerService _swaggerService;
        private readonly HttpClient _httpClient = new HttpClient();

        public ToolProxyService(SwaggerService swaggerService)
        {
            _swaggerService = swaggerService;
        }

        public async Task<List<McpTool>> GetToolsAsync(string swaggerUrl)
        {
            var swagger = await _swaggerService.GetSwaggerAsync(swaggerUrl);
            var endpoints = _swaggerService.ParseEndpoints(swagger);

            return endpoints.Select(e => new McpTool
            {
                Name = e.Name,
                Description = e.Summary,
                InputSchema = McpTool.GenerateInputSchema(e.Parameters),
                Endpoint = new McpTool.EndpointInfo
                {
                    Url = e.Url,
                    Method = e.Verb
                }
            }).ToList();
        }

        public async Task<object> ExecuteToolAsync(string swaggerUrl, string toolName, object input)
        {
            var swagger = await _swaggerService.GetSwaggerAsync(swaggerUrl);
            var endpoints = _swaggerService.ParseEndpoints(swagger);
            var endpoint = endpoints.FirstOrDefault(e => e.Name == toolName);

            if (endpoint == null)
                return new { error = "Tool not found" };

            var url = swaggerUrl; // TODO: get real baseUrl from servers/basePath if needed

            // Remplace les paramètres de path
            var callUrl = endpoint.Url;
            var inputDict = JsonSerializer.Deserialize<Dictionary<string, object>>(input.ToString());
            foreach (var p in endpoint.Parameters.Where(p => p.In == "path"))
                callUrl = callUrl.Replace("{" + p.Name + "}", inputDict.TryGetValue(p.Name, out var val) ? val?.ToString() : "");

            // Ajoute query string
            var queryParams = endpoint.Parameters.Where(p => p.In == "query")
                .Where(p => inputDict.ContainsKey(p.Name))
                .Select(p => $"{p.Name}={inputDict[p.Name]}");
            if (queryParams.Any())
                callUrl += "?" + string.Join("&", queryParams);

            // Appel HTTP dynamique
            HttpResponseMessage resp;
            if (endpoint.Verb == "GET")
            {
                resp = await _httpClient.GetAsync(callUrl);
            }
            else
            {
                StringContent? body = null;
                if (endpoint.Parameters.Any(p => p.In == "body"))
                    body = new StringContent(inputDict["body"].ToString()!, Encoding.UTF8, "application/json");
                resp = await _httpClient.SendAsync(new HttpRequestMessage(
                    new HttpMethod(endpoint.Verb), callUrl)
                {
                    Content = body
                });
            }

            var resultStr = await resp.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<object>(resultStr);
        }

        public async Task<string> GenerateManifestYamlAsync(string swaggerUrl)
        {
            var tools = await GetToolsAsync(swaggerUrl);
            var manifest = new SlimFaasManifest
            {
                Name = "mcp-swagger-proxy",
                Description = "Proxy MCP généré dynamiquement",
                Tools = tools
            };

            var serializer = new SerializerBuilder().Build();
            return serializer.Serialize(manifest);
        }
    }
}
