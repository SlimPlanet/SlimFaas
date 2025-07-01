using Microsoft.AspNetCore.Mvc;
using SlimFaasMcp.Services;

namespace SlimFaasMcp.Controllers;

[ApiController]
[Route("[controller]")]
public class ToolsController : ControllerBase
{
    private readonly ToolProxyService _toolProxyService;

    public ToolsController(ToolProxyService toolProxyService)
    {
        _toolProxyService = toolProxyService;
    }

    [HttpGet]
    public async Task<IActionResult> GetTools([FromQuery] string openapi_url, [FromQuery] string? base_url = null)
    {
        var tools = await _toolProxyService.GetToolsAsync(openapi_url, base_url);
        return Ok(tools);
    }

    [HttpPost("{toolName}")]
    public async Task<IActionResult> ExecuteTool([FromRoute] string toolName, [FromQuery] string openapi_url, [FromBody] object input, [FromQuery] string? base_url = null)
    {
        var result = await _toolProxyService.ExecuteToolAsync(openapi_url, toolName, input, base_url);
        return Ok(result);
    }
}
