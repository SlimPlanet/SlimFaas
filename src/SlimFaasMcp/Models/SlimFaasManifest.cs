using System.Collections.Generic;

namespace Models
{
    public class SlimFaasManifest
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public List<McpTool> Tools { get; set; }
    }
}
