using Microsoft.AspNetCore.Mvc;
using SlimFaasMcp.Services;

namespace SlimFaasMcp.Controllers;

[ApiController]
[Route("[controller]")]
public class ManifestController(ToolProxyService toolProxyService) : ControllerBase
{
    [HttpGet("/manifest.yaml")]
    public async Task<IActionResult> GetManifest([FromQuery] string openapi_url, [FromQuery] string? base_url = null)
    {
        var yaml = await toolProxyService.GenerateManifestYamlAsync(openapi_url, base_url);
        return Content(yaml, "application/x-yaml");
    }
}
