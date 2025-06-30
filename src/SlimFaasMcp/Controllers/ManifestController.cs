using Microsoft.AspNetCore.Mvc;
using SlimFaasMcp.Services;

namespace SlimFaasMcp.Controllers;

[ApiController]
[Route("[controller]")]
public class ManifestController : ControllerBase
{
    private readonly ToolProxyService _toolProxyService;

    public ManifestController(ToolProxyService toolProxyService)
    {
        _toolProxyService = toolProxyService;
    }

    [HttpGet("/manifest.yaml")]
    public async Task<IActionResult> GetManifest([FromQuery] string url, [FromQuery] string? base_url = null)
    {
        var yaml = await _toolProxyService.GenerateManifestYamlAsync(url, base_url);
        return Content(yaml, "application/x-yaml");
    }
}
