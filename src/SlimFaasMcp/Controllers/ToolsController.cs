using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

namespace Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class ToolsController : ControllerBase
    {
        private readonly Services.ToolProxyService _toolProxyService;

        public ToolsController(Services.ToolProxyService toolProxyService)
        {
            _toolProxyService = toolProxyService;
        }

        [HttpGet]
        public async Task<IActionResult> GetTools([FromQuery] string url)
        {
            var tools = await _toolProxyService.GetToolsAsync(url);
            return Ok(tools);
        }

        [HttpPost("{toolName}")]
        public async Task<IActionResult> ExecuteTool([FromRoute] string toolName, [FromQuery] string url, [FromBody] object input)
        {
            var result = await _toolProxyService.ExecuteToolAsync(url, toolName, input);
            return Ok(result);
        }
    }
}
