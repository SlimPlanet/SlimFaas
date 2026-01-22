import { MultiServerMCPClient } from "@langchain/mcp-adapters";
import { ChatOpenAI } from "@langchain/openai";
// LangGraph prebuilt createReactAgent is deprecated in v1.
// Prefer createAgent from langchain package.
import { createReactAgent } from "@langchain/langgraph/prebuilt";

export type McpServerMap = Record<string, string>;

function buildMcpConfig(mcpServerMap: McpServerMap) {
  const mcpServers: Record<string, any> = {};
  for (const [name, url] of Object.entries(mcpServerMap)) {
    mcpServers[name] = {
      transport: "http",
      automaticSSEFallback: false,
      url,
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

export class MCPChat {
  private readonly mcpServerMap: McpServerMap;
  private readonly openaiKey: string;
  private readonly modelName: string;

  private client: MultiServerMCPClient | null = null;
  private agent: any | null = null;
  private initialized = false;

  constructor(mcpServerMap: McpServerMap, openaiKey: string, modelName = "gpt-4o") {
    this.mcpServerMap = mcpServerMap;
    this.openaiKey = openaiKey;
    this.modelName = modelName;
  }

  async init() {
    if (this.initialized) return;

    const cfg = buildMcpConfig(this.mcpServerMap);
    this.client = new MultiServerMCPClient(cfg);

    const tools = await this.client.getTools();

    const llm = new ChatOpenAI({
      apiKey: this.openaiKey,
      model: this.modelName,
      temperature: 0,
    });

    // LangChain v1 agent API
    this.agent = createReactAgent({
      llm,
      tools,
    });

    this.initialized = true;
  }

  async send(userText: string): Promise<string> {
    if (!this.initialized || !this.agent) throw new Error("Agent not initialized.");

    const state = await this.agent.invoke({
      messages: [{ role: "user", content: userText }],
    });

    const msgs = state?.messages ?? [];
    const last = msgs[msgs.length - 1];

    const content = Array.isArray(last?.content)
      ? last.content.map((b: any) => (typeof b === "string" ? b : b?.text ?? "")).join("\n")
      : (last?.content ?? "");

    return content || "[Empty response]";
  }

  async close() {
    try {
      await (this.client as any)?.close?.();
    } catch {
      // ignore
    }
    this.client = null;
    this.agent = null;
    this.initialized = false;
  }
}
