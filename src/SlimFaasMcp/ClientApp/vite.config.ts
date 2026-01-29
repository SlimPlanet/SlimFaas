import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { fileURLToPath, URL } from "node:url";


const asyncHooksShim = fileURLToPath(new URL("./src/polyfills/async_hooks.ts", import.meta.url));
const mcpStdioShim = fileURLToPath(new URL("./src/polyfills/mcp_stdio.ts", import.meta.url));

// Vite build output: ClientApp/dist
export default defineConfig({
  plugins: [react()],
  // Works well when copied under ASP.NET wwwroot (relative assets)
  base: "./",
      build: {
        outDir: "dist",
            emptyOutDir: true,
      },
  define: {
      "process.env": {}, // ✅ avoid "process is not defined" in browser
      global: "globalThis",
  },
  server: {
    port: 5173,
  },
    resolve: {
        alias: [
            // async_hooks polyfill
            { find: /^node:async_hooks$/, replacement: asyncHooksShim },
            { find: /^async_hooks$/, replacement: asyncHooksShim },

            // ✅ MCP stdio polyfill (match avec .js ET sans .js)
            { find: /^@modelcontextprotocol\/sdk\/client\/stdio(\.js)?$/, replacement: mcpStdioShim },
            { find: /^@modelcontextprotocol\/sdk\/server\/stdio(\.js)?$/, replacement: mcpStdioShim },

            // au cas où une dépendance importe les chemins dist/esm
            { find: /^@modelcontextprotocol\/sdk\/dist\/esm\/client\/stdio\.js$/, replacement: mcpStdioShim },
            { find: /^@modelcontextprotocol\/sdk\/dist\/esm\/server\/stdio\.js$/, replacement: mcpStdioShim },
        ],
  },
});
