import { MultiServerMCPClient } from "@langchain/mcp-adapters";
import { createReactAgent } from "@langchain/langgraph/prebuilt";
import { ChatOpenAI } from "@langchain/openai";
// Markdown + sécurité + coloration
import { marked } from "marked";
import DOMPurify from "dompurify";
import hljs from "highlight.js";
import "highlight.js/styles/github-dark.css"; // thème (choisis-en un autre si tu veux)

function enhanceCodeBlocksWithCopy() {
    document.querySelectorAll(".msg.bot pre").forEach((pre) => {
        if (pre.dataset.copyEnhanced) return;
        pre.dataset.copyEnhanced = "1";
        const btn = document.createElement("button");
        btn.textContent = "Copier";
        btn.style.cssText = "position:absolute; right:8px; top:6px; padding:4px 8px; font-size:12px; cursor:pointer;";
        const wrap = document.createElement("div");
        wrap.style.position = "relative";
        pre.parentNode.insertBefore(wrap, pre);
        wrap.appendChild(pre);
        wrap.appendChild(btn);
        btn.addEventListener("click", async () => {
            const code = pre.querySelector("code")?.innerText ?? pre.innerText ?? "";
            await navigator.clipboard.writeText(code);
            btn.textContent = "Copié ✓";
            setTimeout(() => (btn.textContent = "Copier"), 1200);
        });
    });
}


function renderMarkdown(mdText) {
    // 1) Markdown -> HTML (tu peux configurer marked si besoin)
    const rawHtml = marked.parse(mdText ?? "");

    // 2) Ajoute coloration syntaxique aux blocs <pre><code>
    const wrapper = document.createElement("div");
    wrapper.innerHTML = rawHtml;
    wrapper.querySelectorAll("pre code").forEach((block) => {
        hljs.highlightElement(block);
    });

    // 3) Sanitize HTML (anti-XSS)
    return DOMPurify.sanitize(wrapper.innerHTML, {
        USE_PROFILES: { html: true, svg: false, svgFilters: false, mathMl: false },
    });
}

function addMsgHtml(html, who = "bot") {
    const div = document.createElement("div");
    div.className = `msg ${who === "user" ? "user" : "bot"}`;
    div.innerHTML = html; // déjà purifié
    chatBox.appendChild(div);
    chatBox.scrollTop = chatBox.scrollHeight;
}


class MCPChat {
    constructor(mcpServerMap, openaiKey, modelName = "gpt-4o-2024-08-06") {
        this.mcpServerMap = mcpServerMap;
        this.openaiKey = openaiKey;
        this.modelName = modelName;
        this.client = null;
        this.agent = null;
        this.initialized = false;
    }

    _buildMcpConfig() {
        const mcpServers = {};
        for (const [name, url] of Object.entries(this.mcpServerMap)) {
            mcpServers[name] = {
                //transport: "sse",
                transport: "http",                 // ✅ HTTP POST “classique”, sans stream
                automaticSSEFallback: false,
                url,
                //reconnect: { enabled: true, maxAttempts: 5, delayMs: 1500 },
                // headers: { Authorization: "Bearer ..." }, // si votre proxy MCP en a besoin
            };
        }
        return {
            throwOnLoadError: true,
            useStandardContentBlocks: true,
            prefixToolNameWithServerName: true,
            additionalToolNamePrefix: "",
            mcpServers,
        };
    }

    async init() {
        if (this.initialized) return;
        const cfg = this._buildMcpConfig();
        this.client = new MultiServerMCPClient(cfg);

        const tools = await this.client.getTools();
        const llm = new ChatOpenAI({
            apiKey: this.openaiKey,
            model: this.modelName,
            temperature: 0,
        });
        this.agent = createReactAgent({ llm, tools });
        this.initialized = true;
    }

    async send(userText) {
        if (!this.initialized) throw new Error("Agent non initialisé.");
        const state = await this.agent.invoke({
            messages: [{ role: "user", content: userText }],
        });
        const msgs = state?.messages ?? [];
        const last = msgs[msgs.length - 1];
        const content = Array.isArray(last?.content)
            ? last.content.map(b => (typeof b === "string" ? b : b?.text ?? "")).join("\n")
            : (last?.content ?? "");
        return content || "[Réponse vide]";
    }

    async close() {
        try { await this.client?.close?.(); } catch {}
        this.client = null;
        this.agent = null;
        this.initialized = false;
    }
}

// ---- Wiring UI ----
const $ = (id) => document.getElementById(id);

const chatBox = $("chat");
const sendBtn = $("sendBtn");
const clearBtn = $("clearBtn");
const initBtn = $("initBtn");
const closeBtn = $("closeBtn");

const userMsg = $("userMsg");
const serversEl = $("serversJson");
const keyEl = $("openaiKey");
const modelEl = $("model");

let chat = null;
let busy = false;

function addMsg(text, who = "bot") {
    if (who === "bot") {
        const safeHtml = renderMarkdown(String(text ?? ""));
        addMsgHtml(safeHtml, "bot");
        enhanceCodeBlocksWithCopy();

    } else {
        const div = document.createElement("div");
        div.className = "msg user";
        div.textContent = text;
        chatBox.appendChild(div);
        chatBox.scrollTop = chatBox.scrollHeight;
    }
}


async function onInit() {
    if (busy) return;
    try {
        busy = true; initBtn.disabled = true;

        const key = keyEl.value.trim();
        if (!key) throw new Error("Veuillez saisir votre clé OpenAI.");
        let serversObj;
        try {
            serversObj = JSON.parse(serversEl.value);
        } catch {
            throw new Error("JSON des serveurs MCP invalide.");
        }
        if (!serversObj || typeof serversObj !== "object" || !Object.keys(serversObj).length) {
            throw new Error("Le dictionnaire de serveurs MCP est vide.");
        }

        chat?.close?.();
        chat = new MCPChat(serversObj, key, modelEl.value.trim() || "gpt-4o-2024-08-06");
        await chat.init();
        addMsg("✅ Agent initialisé. Vous pouvez discuter.", "bot");
    } catch (e) {
        console.error(e);
        addMsg("❌ " + (e?.message || String(e)), "bot");
    } finally {
        busy = false; initBtn.disabled = false;
    }
}

async function onSend() {
    if (busy) return;
    const text = userMsg.value.trim();
    if (!text) return;

    try {
        busy = true; sendBtn.disabled = true;
        userMsg.value = "";
        addMsg(text, "user");
        const reply = await chat.send(text);
        addMsg(reply, "bot");
    } catch (e) {
        console.error(e);
        addMsg("❌ " + (e?.message || String(e)), "bot");
    } finally {
        busy = false; sendBtn.disabled = false;
    }
}

function onClear() { chatBox.innerHTML = ""; }
async function onClose() { await chat?.close?.(); addMsg("🔌 Connexions fermées.", "bot"); }

initBtn.addEventListener("click", onInit);
closeBtn.addEventListener("click", onClose);
sendBtn.addEventListener("click", onSend);
clearBtn.addEventListener("click", onClear);
userMsg.addEventListener("keydown", (e) => {
    if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
        e.preventDefault(); onSend();
    }
});
