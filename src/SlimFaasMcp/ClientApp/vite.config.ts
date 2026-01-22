import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { fileURLToPath, URL } from "node:url";

// Vite build output: ClientApp/dist
export default defineConfig({
  plugins: [react()],
  // Works well when copied under ASP.NET wwwroot (relative assets)
  base: "./",
      build: {
        outDir: "dist",
            emptyOutDir: true,
            rollupOptions: {
              // ✅ exclude Node-only MCP stdio transport from the browser bundle
                  external: (id) => id.includes("@modelcontextprotocol/sdk") && id.endsWith("/client/stdio.js"),
                },
      },
  define: {
      "process.env": {}, // ✅ avoid "process is not defined" in browser
      global: "globalThis",
  },
  server: {
    port: 5173,
  },
    resolve: {
      alias: {
           "node:async_hooks": fileURLToPath(new URL("./src/polyfills/async_hooks.ts", import.meta.url)),
          async_hooks: fileURLToPath(new URL("./src/polyfills/async_hooks.ts", import.meta.url)),
              "@modelcontextprotocol/sdk/client/stdio": fileURLToPath(new URL("./src/polyfills/mcp_stdio.ts", import.meta.url)),
         "@modelcontextprotocol/sdk/dist/esm/client/stdio.js": fileURLToPath(new URL("./src/polyfills/mcp_stdio.ts", import.meta.url)),
      },
  },
});
