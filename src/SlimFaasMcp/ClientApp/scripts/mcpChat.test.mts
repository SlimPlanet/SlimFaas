import assert from "node:assert/strict";
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import { once } from "node:events";
import test from "node:test";
import { MCPAdapter } from "@langchain/mcp-adapters";
import { buildMcpConfig, MCPChat } from "../src/pages/chat/mcpChat.ts";

async function mockMcpServer(handler: (request: IncomingMessage, response: ServerResponse) => void) {
  const server = createServer(handler);
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const address = server.address();
  assert.ok(address && typeof address !== "string");
  return {
    url: `http://127.0.0.1:${address.port}/mcp`,
    close: () => new Promise<void>((resolve, reject) => {
      server.close((error) => error ? reject(error) : resolve());
      server.closeAllConnections();
    }),
  };
}

test("chat discovers and calls prefixed tools through Streamable HTTP", { timeout: 10_000 }, async () => {
  const calls: string[] = [];
  const server = await mockMcpServer(async (request, response) => {
    if (request.method !== "POST") {
      response.writeHead(405).end();
      return;
    }
    const chunks: Buffer[] = [];
    for await (const chunk of request) chunks.push(chunk);
    const message = JSON.parse(Buffer.concat(chunks).toString());
    calls.push(message.method);
    if (message.id === undefined) {
      response.writeHead(202).end();
      return;
    }
    let result: unknown;
    switch (message.method) {
      case "initialize":
        result = { protocolVersion: "2025-03-26", capabilities: { tools: {} }, serverInfo: { name: "test", version: "1.0.0" } };
        break;
      case "tools/list":
        result = { tools: [{ name: "echo", description: "Echo text", inputSchema: { type: "object", properties: { text: { type: "string" } }, required: ["text"] } }] };
        break;
      case "tools/call":
        assert.equal(message.params.name, "echo");
        result = { content: [{ type: "text", text: message.params.arguments.text }] };
        break;
      default:
        response.writeHead(400).end();
        return;
    }
    response.writeHead(200, { "Content-Type": "application/json" });
    response.end(JSON.stringify({ jsonrpc: "2.0", id: message.id, result }));
  });
  const adapter = new MCPAdapter(buildMcpConfig({ demo: server.url }));
  const chat = new MCPChat({ demo: server.url }, "unused-test-key");
  try {
    await chat.init();
    const tools = await adapter.listTools();
    assert.equal(tools.length, 1);
    assert.equal(tools[0].name, "demo_echo");
    assert.equal(await tools[0].invoke({ text: "hello" }), "hello");
    assert.ok(calls.includes("notifications/initialized"));
    assert.ok(calls.includes("tools/call"));
  } finally {
    await chat.close();
    await adapter.close();
    await server.close();
  }
});

test("chat rejects failed HTTP discovery without falling back to SSE", { timeout: 10_000 }, async () => {
  const methods: string[] = [];
  const server = await mockMcpServer((request, response) => {
    methods.push(request.method ?? "");
    response.writeHead(404).end();
  });
  const chat = new MCPChat({ missing: server.url }, "unused-test-key");
  try {
    await assert.rejects(chat.init());
    assert.ok(methods.includes("POST"));
    assert.ok(!methods.includes("GET"));
  } finally {
    await chat.close();
    await server.close();
  }
});
