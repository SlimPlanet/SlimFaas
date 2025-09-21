// vite.config.js
import { defineConfig } from "vite";
import path from "node:path";

const empty = path.resolve(__dirname, "src/shims/empty.js");

export default defineConfig({
    // Certaines libs lisent process/global: on met des no-ops sûrs
    define: {
        "process.env": {},
        global: "globalThis",
    },
    resolve: {
        // très important: privilégie les branches "browser"
        conditions: ["browser", "development"],

        alias: [
            // ⚠️ Coupe net les deps Node traînées par la branche stdio
            { find: /^child_process$/,              replacement: empty },
            { find: /^node:child_process$/,         replacement: empty },
            { find: /^fs$/,                          replacement: empty },
            { find: /^node:fs$/,                     replacement: empty },
            { find: /^net$/,                         replacement: empty },
            { find: /^node:net$/,                    replacement: empty },
            { find: /^tls$/,                         replacement: empty },
            { find: /^node:tls$/,                    replacement: empty },

            // Certaines versions tirent ces deps: on les neutralise (regex = tout sous-chemin)
            { find: /^which(\/.*)?$/,                replacement: empty },
            { find: /^cross-spawn(\/.*)?$/,          replacement: empty },

            // Facultatif: parfois "readline" ou "os" apparaissent
            { find: /^readline(\/.*)?$/,             replacement: empty },
            { find: /^os(\/.*)?$/,                   replacement: empty },
        ],
    },

    optimizeDeps: {
        // Évite que Vite pré-bundle la mauvaise branche
        exclude: [
            "@langchain/mcp-adapters",
            "cross-spawn",
            "which",
        ],
    },
});
