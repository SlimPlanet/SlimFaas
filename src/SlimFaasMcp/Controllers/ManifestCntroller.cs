using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

namespace Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class ManifestController : ControllerBase
    {
        private readonly Services.ToolProxyService _toolProxyService;

        public ManifestController(Services.ToolProxyService toolProxyService)
        {
            _toolProxyService = toolProxyService;
        }

        [HttpGet("/manifest.yaml")]
        public async Task<IActionResult> GetManifest([FromQuery] string url)
        {
            var yaml = await _toolProxyService.GenerateManifestYamlAsync(url);
            return Content(yaml, "application/x-yaml");
        }
    }
}
