import { useEffect, useMemo, useRef, useState } from "react";
import YAML from "js-yaml";
import Toggle from "../../components/ui/Toggle";
import Field from "../../components/ui/Field";
import ToolCard from "./components/ToolCard";
import type { McpTool, UiTool } from "./types";
import { fromBase64Utf8, isProbablyBase64, safeJsonParse, toBase64Utf8 } from "../../utils/encoding";
import "./SwaggerToMcpPage.scss";

const API_BASE = (import.meta.env.VITE_MCP_API_BASE_URL ?? "").replace(/\/+$/, "");
const api = (path: string) => (API_BASE ? `${API_BASE}${path}` : path);

function clampUrlBase(baseUrl: string): string {
  // Avoid trailing slashes to make share URL stable (still accept the backend side)
  return baseUrl.replace(/\/+$/, "");
}

function buildOriginBase(): string {
  // Respect Vite base for apps hosted under subpath
  const base = (import.meta as any).env?.BASE_URL ?? "/";
  const baseNoSlash = String(base).replace(/\/+$/, "");
  return window.location.origin + baseNoSlash;
}

function buildMinimalPrompt(opts: { loadedTools: McpTool[]; uiTools: McpTool[] }): any {
  const prompt: any = {};
  const origMap = new Map(opts.loadedTools.map((t) => [t.name, t]));
  const activeTools = opts.uiTools.filter((t) => !t.isDisabled).map((t) => t.name);

  if (activeTools.length !== opts.loadedTools.length) prompt.activeTools = activeTools;

  const modified: any[] = [];
  for (const t of opts.uiTools) {
    const orig = origMap.get(t.name);
    if (!orig) {
      modified.push({ name: t.name, description: t.description, inputSchema: t.inputSchema });
      continue;
    }
    const descChanged = t.description !== orig.description;
    const schemaChanged = JSON.stringify(t.inputSchema) !== JSON.stringify(orig.inputSchema);
    if (descChanged || schemaChanged) {
      const delta: any = { name: t.name };
      if (descChanged) delta.description = t.description;
      if (schemaChanged) delta.inputSchema = t.inputSchema;
      modified.push(delta);
    }
  }
  if (modified.length) prompt.tools = modified;
  return prompt;
}

function mergeToolsFromPrompt(loadedTools: McpTool[], prompt: any): McpTool[] {
  const activeSet = prompt?.activeTools ? new Set<string>(prompt.activeTools) : null;

  const originMap = new Map(loadedTools.map((t) => [t.name, t]));
  const merged: McpTool[] = [];

  for (const orig of loadedTools) {
    const override = (prompt?.tools || []).find((t: any) => t?.name === orig.name);
    const active = activeSet ? activeSet.has(orig.name) : true;

    merged.push({
      ...orig,
      description: override?.description || orig.description,
      inputSchema: override?.inputSchema || orig.inputSchema,
      isOverridden: !!override,
      isDescriptionOverridden: !!(override && override.description),
      isInputSchemaOverridden: !!(override && override.inputSchema),
      isDisabled: !active,
      isAdded: false,
    });
  }

  for (const t of prompt?.tools || []) {
    if (!originMap.has(t.name)) {
      merged.push({
        ...t,
        isOverridden: false,
        isDescriptionOverridden: !!t.description,
        isInputSchemaOverridden: !!t.inputSchema,
        isDisabled: false,
        isAdded: true,
        inputSchema: t.inputSchema || {},
        description: t.description || "(Added by YAML)",
        endpoint: { contentType: "application/json" },
      });
    }
  }

  return merged;
}

async function readFileAsBase64(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      const s = String(reader.result || "");
      const idx = s.indexOf("base64,");
      resolve(idx >= 0 ? s.substring(idx + 7) : s);
    };
    reader.onerror = (e) => reject(e);
    reader.readAsDataURL(file);
  });
}

export default function SwaggerToMcpPage() {
  const originBase = useMemo(() => buildOriginBase(), []);
  const [swaggerUrl, setSwaggerUrl] = useState("https://petstore3.swagger.io/api/v3/openapi.json");
  const [baseUrl, setBaseUrl] = useState("https://petstore3.swagger.io/api/v3");
  const [toolPrefix, setToolPrefix] = useState("");
  const [cacheExpiration, setCacheExpiration] = useState<string>("");

  const [structuredContent, setStructuredContent] = useState(false);

  // New requirement: checkbox to include Protected Resource Metadata
  const [includePrm, setIncludePrm] = useState(false);

  const [oauthMetaText, setOauthMetaText] = useState(`{
  "resource": "https://api.example.com/v1/",
  "authorization_servers": ["https://auth.example.com"],
  "scopes_supported": ["read:data", "write:data"]
}`);

  const [accessToken, setAccessToken] = useState("");

  const [promptYaml, setPromptYaml] = useState("");
  const [promptB64, setPromptB64] = useState("");

  const [oauthB64, setOauthB64] = useState("");

  const [mcpPromptBase64Input, setMcpPromptBase64Input] = useState("");

  const [loadedTools, setLoadedTools] = useState<McpTool[]>([]);
  const [uiTools, setUiTools] = useState<UiTool[]>([]);
  const [toolOutputs, setToolOutputs] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [errorMsg, setErrorMsg] = useState<string>("");

  const shareUrl = useMemo(() => {
    let url = `${originBase}/mcp?openapi_url=${encodeURIComponent(swaggerUrl.trim())}`;
    const b = clampUrlBase(baseUrl.trim());
    if (b) url += `&base_url=${encodeURIComponent(b)}`;
    if (promptB64 && isProbablyBase64(promptB64)) url += `&mcp_prompt=${encodeURIComponent(promptB64)}`;
    if (includePrm && oauthB64 && isProbablyBase64(oauthB64)) url += `&oauth=${encodeURIComponent(oauthB64)}`;
    if (structuredContent) url += `&structured_content=true`;
    if (toolPrefix.trim()) url += `&tool_prefix=${encodeURIComponent(toolPrefix.trim())}`;
    if (cacheExpiration) url += `&cache_expiration=${encodeURIComponent(cacheExpiration)}`;
    return url;
  }, [originBase, swaggerUrl, baseUrl, promptB64, includePrm, oauthB64, structuredContent, toolPrefix, cacheExpiration]);

  const shareUrlTooLong = shareUrl.length > 8000;

  const bearerHeaders = useMemo<Record<string, string>>(() => {
      const token = accessToken.trim();
      const headers: Record<string, string> = {};
      if (token) headers["Authorization"] = `Bearer ${token}`;
      return headers;
  }, [accessToken]);

  // init structured_content from url query (optional)
  useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    const enabled = params.get("structured_content") === "true";
    setStructuredContent(enabled);
  }, []);

  // compute oauth base64 (if included)
  useEffect(() => {
    if (!includePrm) {
      setOauthB64("");
      return;
    }
    try {
      const b64 = toBase64Utf8(oauthMetaText.trim());
      setOauthB64(b64);
    } catch {
      setOauthB64("");
    }
  }, [oauthMetaText, includePrm]);

  // keep prompt base64 in sync with YAML and tools
  const recomputePromptFromTools = (nextTools: McpTool[], baseLoaded: McpTool[]) => {
    const promptObj = buildMinimalPrompt({ loadedTools: baseLoaded, uiTools: nextTools });
    const b64 = toBase64Utf8(JSON.stringify(promptObj));
    setPromptB64(b64);
  };

  const applyPromptFromYaml = () => {
    let promptObj: any;
    try { promptObj = (YAML.load(promptYaml) || {}) as any; }
    catch (e: any) {
        setErrorMsg(`YAML parse error: ${e?.message ?? String(e)}`);
        return;
    }

    const merged = mergeToolsFromPrompt(loadedTools, promptObj);
    setUiTools(merged.map((t) => ({
      ...t,
      uiJsonInput: "{}",
      uiTextInputs: {},
      uiFileInputs: {},
    })));
    recomputePromptFromTools(merged, loadedTools);
    setErrorMsg("");
  };

  const copyToClipboard = async (txt: string) => {
    try {
      await navigator.clipboard.writeText(txt);
    } catch {
      // ignore
    }
  };

  const loadTools = async () => {
    setErrorMsg("");
    setBusy(true);
    try {
      const swagger = swaggerUrl.trim();
      if (!swagger) throw new Error("Swagger URL is required.");
      const b = clampUrlBase(baseUrl.trim());

      let endpoint = `/tools?openapi_url=${encodeURIComponent(swagger)}`;
      if (b) endpoint += `&base_url=${encodeURIComponent(b)}`;
      if (includePrm && oauthB64) endpoint += `&oauth=${encodeURIComponent(oauthB64)}`;
      if (structuredContent) endpoint += `&structured_content=true`;
      if (toolPrefix.trim()) endpoint += `&tool_prefix=${encodeURIComponent(toolPrefix.trim())}`;
      if (cacheExpiration) endpoint += `&cache_expiration=${encodeURIComponent(cacheExpiration)}`;

      const res = await fetch(api(endpoint), { method: "GET", headers: bearerHeaders as any });
      if (!res.ok) throw new Error(`Load tools failed: HTTP ${res.status}`);
      const tools = (await res.json()) as McpTool[];
      setLoadedTools(tools);

      // If a base64 prompt was provided, try to apply it as YAML in editor
      let initialYaml: string;
      const b64Prompt = mcpPromptBase64Input.trim();
      if (b64Prompt) {
        if (!isProbablyBase64(b64Prompt)) {
          initialYaml = `# Error: invalid base64 input`;
        } else {
          try {
            const jsonStr = fromBase64Utf8(b64Prompt);
            initialYaml = YAML.dump(JSON.parse(jsonStr));
          } catch (e: any) {
            initialYaml = `# Error decoding base64: ${e?.message ?? String(e)}`;
          }
        }
      } else {
        initialYaml = YAML.dump({
          activeTools: tools.map((t) => t.name),
          tools: tools.map((t) => ({
            name: t.name,
            description: t.description,
            inputSchema: t.inputSchema,
            outputSchema: t.outputSchema,
          })),
        });
      }
      setPromptYaml(initialYaml);

      // default ui tools == loaded tools
      const initialUiTools = tools.map((t) => ({
        ...t,
        uiJsonInput: "{}",
        uiTextInputs: {},
        uiFileInputs: {},
      })) as UiTool[];
      setUiTools(initialUiTools);

      // compute prompt b64 from minimal prompt
      recomputePromptFromTools(tools, tools);
    } finally {
      setBusy(false);
    }
  };

  const onChangeJsonInput = (toolName: string, txt: string) => {
    setUiTools((prev) =>
      prev.map((t) => (t.name === toolName ? { ...t, uiJsonInput: txt } : t)),
    );
  };
  const onChangeTextInput = (toolName: string, key: string, value: string) => {
    setUiTools((prev) =>
      prev.map((t) =>
        t.name === toolName ? { ...t, uiTextInputs: { ...(t.uiTextInputs || {}), [key]: value } } : t,
      ),
    );
  };
  const onChangeFileInput = (toolName: string, key: string, file: File | null) => {
    setUiTools((prev) =>
      prev.map((t) =>
        t.name === toolName ? { ...t, uiFileInputs: { ...(t.uiFileInputs || {}), [key]: file } } : t,
      ),
    );
  };

  const onRun = async (toolName: string) => {
    setErrorMsg("");
    const tool = uiTools.find((t) => t.name === toolName);
    const baseLoaded = loadedTools.find((t) => t.name === toolName);
    if (!tool || !baseLoaded) return;

    setToolOutputs((prev) => ({ ...prev, [toolName]: "⏳ Running..." }));

    const swagger = swaggerUrl.trim();
    const b = clampUrlBase(baseUrl.trim());

    let endpoint = `/tools/${encodeURIComponent(toolName)}?openapi_url=${encodeURIComponent(swagger)}`;
    if (b) endpoint += `&base_url=${encodeURIComponent(b)}`;
    if (promptB64) endpoint += `&mcp_prompt=${encodeURIComponent(promptB64)}`;
    if (includePrm && oauthB64) endpoint += `&oauth=${encodeURIComponent(oauthB64)}`;
    if (structuredContent) endpoint += `&structured_content=true`;
    if (toolPrefix.trim()) endpoint += `&tool_prefix=${encodeURIComponent(toolPrefix.trim())}`;
    if (cacheExpiration) endpoint += `&cache_expiration=${encodeURIComponent(cacheExpiration)}`;

    const contentType = tool.endpoint?.contentType || "application/json";
    const headers: Record<string, string> = { ...bearerHeaders };

    let body: string = "{}";

    if (contentType === "multipart/form-data" || contentType === "application/octet-stream") {
      // Send MCP JSON args with embedded base64 file (same behavior as your original)
      const props = (tool.inputSchema?.properties || {}) as Record<string, any>;
      const args: Record<string, any> = {};

      for (const [key, val] of Object.entries(props)) {
        const maybeFile = tool.uiFileInputs?.[key] ?? null;
        if (maybeFile) {
          const base64 = await readFileAsBase64(maybeFile);
          args[key] = {
            data: base64,
            filename: maybeFile.name,
            mimeType: maybeFile.type || "application/octet-stream",
          };
        } else if (val && (val.format === "binary" || val?.properties?.data?.contentEncoding === "base64")) {
          // file was expected but not provided
        } else {
          const txt = tool.uiTextInputs?.[key];
          if (txt) args[key] = txt;
        }
      }
      headers["Content-Type"] = "application/json";
      body = JSON.stringify(args);
    } else {
      headers["Content-Type"] = "application/json";
      const txt = tool.uiJsonInput ?? "{}";
      const parsed = safeJsonParse<any>(txt);
      if (!parsed.ok) {
        setToolOutputs((prev) => ({ ...prev, [toolName]: `❌ Invalid JSON: ${parsed.error}` }));
        return;
      }
      body = JSON.stringify(parsed.value);
    }

    try {
      const res = await fetch(api(endpoint), { method: "POST", headers, body });
      const text = await res.text();
      let out: string;
      try {
        const data = JSON.parse(text);
        out = typeof data === "string" ? data : JSON.stringify(data, null, 2);
      } catch {
        out = text;
      }
      setToolOutputs((prev) => ({ ...prev, [toolName]: out }));
    } catch (e: any) {
      setToolOutputs((prev) => ({ ...prev, [toolName]: `❌ ${e?.message ?? String(e)}` }));
    }
  };

  // When uiTools changes, keep prompt base64 in sync (but avoid on first empty state)
  const lastToolsKey = useRef<string>("");
  useEffect(() => {
    if (!loadedTools.length || !uiTools.length) return;
    // Only recompute if tool metadata changed (not per user input)
    const key = JSON.stringify(uiTools.map((t) => [t.name, t.description, t.inputSchema, t.isDisabled]));
    if (key === lastToolsKey.current) return;
    lastToolsKey.current = key;
    recomputePromptFromTools(uiTools, loadedTools);
  }, [uiTools, loadedTools]);

  // Friendly page header
  return (
    <div className="swagger-page">
      <div className="swagger-page__header">
        <h1 className="swagger-page__headline">Swagger to MCP</h1>
        <p className="swagger-page__sub">
          Turn any OpenAPI/Swagger definition into an MCP endpoint, customize a minimal MCP prompt, and run tools.
        </p>
      </div>

      {errorMsg ? (
        <div className="sf-card" style={{ borderColor: "rgba(251,113,133,0.5)" }}>
          <div className="sf-card__inner">
            <div style={{ color: "rgba(251,113,133,0.95)", fontWeight: 650 }}>{errorMsg}</div>
          </div>
        </div>
      ) : null}

      <div className="swagger-page__grid">
        <div className="swagger-page__split">
          <section className="sf-card">
            <div className="sf-card__inner">
              <div className="sf-title">Configuration</div>

              <div className="swagger-page__form">
                <div className="swagger-page__form-row">
                  <Field label="Swagger URL">
                    <input className="sf-input" value={swaggerUrl} onChange={(e) => setSwaggerUrl(e.target.value)} />
                  </Field>
                  <Field label="Base URL override" hint="Optional — if your server needs a different base URL">
                    <input className="sf-input" value={baseUrl} onChange={(e) => setBaseUrl(e.target.value)} />
                  </Field>
                </div>

                <div className="swagger-page__form-row">
                  <Field label="Tool prefix" hint="Optional — prefix tool names (e.g. myapp)">
                    <input className="sf-input" value={toolPrefix} onChange={(e) => setToolPrefix(e.target.value)} />
                  </Field>
                  <Field label="Swagger cache expiration (minutes)" hint="Optional — sliding expiration">
                    <input
                      className="sf-input"
                      type="number"
                      min={0}
                      value={cacheExpiration}
                      onChange={(e) => setCacheExpiration(e.target.value)}
                      placeholder="e.g. 10"
                    />
                  </Field>
                </div>

                <div className="swagger-page__inline">
                  <Toggle
                    checked={structuredContent}
                    onChange={setStructuredContent}
                    label="Enable structured_content"
                    hint="Include structuredContent in responses (and show output schema)."
                  />
                  <Toggle
                    checked={includePrm}
                    onChange={setIncludePrm}
                    label="Include Protected Resource Metadata"
                    hint="Adds &oauth=... and exposes /.well-known/oauth-protected-resource"
                  />
                </div>

                <Field label="Access token" hint="Optional — sent as Authorization: Bearer ...">
                  <input
                    className="sf-input"
                    value={accessToken}
                    onChange={(e) => setAccessToken(e.target.value)}
                    placeholder="Bearer token (optional)"
                  />
                </Field>

                <Field label="MCP Prompt base64 (optional)" hint="Paste a base64 prompt to prefill the YAML editor before loading tools.">
                  <input
                    className="sf-input"
                    value={mcpPromptBase64Input}
                    onChange={(e) => setMcpPromptBase64Input(e.target.value)}
                    placeholder="(optional)"
                  />
                </Field>

                <div className="swagger-page__actions">
                  <button className="sf-btn sf-btn--primary" type="button" onClick={loadTools} disabled={busy}>
                    {busy ? "Loading..." : "Load tools"}
                  </button>
                  <button className="sf-btn" type="button" onClick={() => copyToClipboard(shareUrl)}>
                    Copy MCP URL
                  </button>
                </div>
              </div>

              <div style={{ height: 14 }} />

              <div className="swagger-page__urls">
                <div className="swagger-page__url-row">
                  <div className="swagger-page__url-label">MCP URL</div>
                  <div
                    className={`swagger-page__url-value ${shareUrlTooLong ? "swagger-page__url-value--too-long" : "swagger-page__url-value--ok"}`}
                    title={shareUrlTooLong ? `⚠️ ${shareUrl.length} characters — too long (max 8000)` : ""}
                  >
                    {shareUrl}
                  </div>
                </div>


              </div>
            </div>
          </section>

          <section className="sf-card">
            <div className="sf-card__inner">
              <div className="sf-title">MCP Prompt (YAML)</div>
              <p className="sf-muted" style={{ marginTop: 0, fontSize: 13 }}>
                Edit the YAML then apply it to override tool descriptions/schemas or disable tools. We keep a minimal prompt and expose its base64 form.
              </p>

              <Field label="YAML prompt">
                <textarea className="sf-textarea" value={promptYaml} onChange={(e) => setPromptYaml(e.target.value)} />
              </Field>

              <div style={{ height: 10 }} />

              <Field label="Base64 encoded JSON prompt" hint="This is what gets injected as &mcp_prompt=...">
                <textarea className="sf-textarea" readOnly value={promptB64} />
              </Field>

              <div style={{ height: 10 }} />

              <div className="swagger-page__actions">
                <button className="sf-btn sf-btn--primary" type="button" onClick={applyPromptFromYaml} disabled={!loadedTools.length}>
                  Apply MCP Prompt
                </button>
                <button className="sf-btn" type="button" onClick={() => copyToClipboard(promptB64)} disabled={!promptB64}>
                  Copy base64
                </button>
              </div>

              {includePrm ? (
                <>
                  <div style={{ height: 18 }} />
                  <div className="sf-title">Protected Resource Metadata (OAuth 2.0)</div>
                  <Field
                    label="Metadata JSON"
                    hint="This JSON is base64-encoded and passed via &oauth=. Your backend should expose /.well-known/oauth-protected-resource."
                  >
                    <textarea className="sf-textarea" value={oauthMetaText} onChange={(e) => setOauthMetaText(e.target.value)} />
                  </Field>
                  <div style={{ height: 10 }} />
                  <Field label="Base64 encoded metadata">
                    <textarea className="sf-textarea" readOnly value={oauthB64} />
                  </Field>
                </>
              ) : null}
            </div>
          </section>
        </div>

        <div style={{ height: 12 }} />

        <section>
          <div className="swagger-page__header" style={{ marginBottom: 10 }}>
            <div className="sf-title">Tools</div>
            <div className="sf-muted" style={{ fontSize: 13 }}>
              Loaded tools are rendered below. You can run them directly (POST /tools/&lt;name&gt;).
            </div>
          </div>

          <div className="swagger-page__tools">
            {uiTools.length ? (
              uiTools.map((t) => (
                <ToolCard
                  key={t.name}
                  tool={t}
                  structuredContentEnabled={structuredContent}
                  onChangeJsonInput={onChangeJsonInput}
                  onChangeTextInput={onChangeTextInput}
                  onChangeFileInput={onChangeFileInput}
                  onRun={onRun}
                  output={toolOutputs[t.name]}
                />
              ))
            ) : (
              <div className="sf-card">
                <div className="sf-card__inner sf-muted" style={{ fontSize: 13 }}>
                  No tools yet — click <b>Load tools</b>.
                </div>
              </div>
            )}
          </div>
        </section>
      </div>
    </div>
  );
}
