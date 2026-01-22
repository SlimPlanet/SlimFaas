// ClientApp/src/polyfills/mcp_stdio.ts
export class StdioClientTransport {
    constructor() {
        throw new Error("MCP stdio transport is not supported in the browser. Use HTTP/SSE transport.");
    }
}
export class StdioServerTransport {
    constructor() {
        throw new Error("MCP stdio transport is not supported in the browser.");
    }
}
