import { useEffect, useMemo, useRef, useState } from "react";
import Field from "../../components/ui/Field";
import Toggle from "../../components/ui/Toggle";
import { renderMarkdownToSafeHtml, enhanceCodeBlocksWithCopy } from "./markdown";
import { MCPChat, type McpServerMap } from "./mcpChat";
import { safeJsonParse } from "../../utils/encoding";
import "./ChatPage.scss";

type ChatMsg = {
  id: string;
  who: "user" | "bot";
  text: string;
  ts: number;
};

function uid(prefix = "msg") {
  return `${prefix}_${Math.random().toString(36).slice(2, 10)}`;
}

export default function ChatPage() {
  const [openaiKey, setOpenaiKey] = useState("");
  const [model, setModel] = useState("gpt-4o");
  const [serversJson, setServersJson] = useState(`{
  "slimfaas": "http://localhost:8080/mcp"
}`);

  const [busy, setBusy] = useState(false);
  const [inited, setInited] = useState(false);
  const [errorMsg, setErrorMsg] = useState("");

  const [userMsg, setUserMsg] = useState("");
  const [messages, setMessages] = useState<ChatMsg[]>([
    {
      id: uid(),
      who: "bot",
      text: "Configure your MCP servers and OpenAI key, then click **Init**.\n\nTip: send with **Ctrl+Enter**.",
      ts: Date.now(),
    },
  ]);

  const chatRef = useRef<HTMLDivElement | null>(null);
  const chat = useRef<MCPChat | null>(null);

  const [showKey, setShowKey] = useState(false);

  const addMsg = (who: "user" | "bot", text: string) => {
    setMessages((prev) => [...prev, { id: uid(), who, text, ts: Date.now() }]);
  };

  useEffect(() => {
    const el = chatRef.current;
    if (!el) return;
    el.scrollTop = el.scrollHeight;
    enhanceCodeBlocksWithCopy(el);
  }, [messages]);

  const onInit = async () => {
    if (busy) return;
    setErrorMsg("");
    setBusy(true);

    try {
      const key = openaiKey.trim();
      if (!key) throw new Error("Please provide your OpenAI API key.");
      const parsed = safeJsonParse<McpServerMap>(serversJson);
      if (!parsed.ok) throw new Error(`Invalid MCP servers JSON: ${parsed.error}`);
      if (!parsed.value || typeof parsed.value !== "object" || !Object.keys(parsed.value).length) {
        throw new Error("MCP servers map is empty.");
      }

      await chat.current?.close();
      chat.current = new MCPChat(parsed.value, key, model.trim() || "gpt-4o");
      await chat.current.init();
      setInited(true);
      addMsg("bot", "✅ Agent initialized. You can chat now.");
    } catch (e: any) {
      setErrorMsg(e?.message ?? String(e));
      addMsg("bot", `❌ ${e?.message ?? String(e)}`);
      setInited(false);
    } finally {
      setBusy(false);
    }
  };

  const onClose = async () => {
    await chat.current?.close();
    setInited(false);
    addMsg("bot", "🔌 Connections closed.");
  };

  const onClear = () => setMessages([]);

  const onSend = async () => {
    if (busy) return;
    const txt = userMsg.trim();
    if (!txt) return;

    if (!chat.current || !inited) {
      addMsg("bot", "❌ Please click **Init** first.");
      return;
    }

    setBusy(true);
    setUserMsg("");
    addMsg("user", txt);

    try {
      const reply = await chat.current.send(txt);
      addMsg("bot", reply);
    } catch (e: any) {
      setErrorMsg(e?.message ?? String(e));
      addMsg("bot", `❌ ${e?.message ?? String(e)}`);
    } finally {
      setBusy(false);
    }
  };

  const renderedMsgs = useMemo(() => {
    return messages.map((m) => ({
      ...m,
      safeHtml: m.who === "bot" ? renderMarkdownToSafeHtml(m.text) : "",
    }));
  }, [messages]);

  return (
    <div className="chat-page">
      <div className="chat-page__header">
        <h1 className="chat-page__headline">Chat</h1>
        <p className="chat-page__sub">
          Chat with an agent that can call tools across one or more MCP servers.
        </p>
      </div>

      <div className="chat-page__layout">
        <section className="sf-card">
          <div className="sf-card__inner chat-page__settings">
            <div className="sf-title">Settings</div>

            <Field label="MCP servers (JSON)" hint='Example: { "slimfaas": "http://localhost:8080/mcp" }'>
              <textarea className="sf-textarea" value={serversJson} onChange={(e) => setServersJson(e.target.value)} />
            </Field>

            <Field label="OpenAI API key">
              <input
                className="sf-input"
                type={showKey ? "text" : "password"}
                value={openaiKey}
                onChange={(e) => setOpenaiKey(e.target.value)}
                placeholder="sk-..."
              />
            </Field>

            <Toggle
              checked={showKey}
              onChange={setShowKey}
              label="Show API key"
              hint="(Avoid sharing this in screenshots)"
            />

            <Field label="Model name" hint="Any chat model available for your key">
              <input className="sf-input" value={model} onChange={(e) => setModel(e.target.value)} />
            </Field>

            <div style={{ display: "flex", gap: 10, flexWrap: "wrap" }}>
              <button className="sf-btn sf-btn--primary" type="button" onClick={onInit} disabled={busy}>
                {busy ? "Working..." : "Init"}
              </button>
              <button className="sf-btn" type="button" onClick={onClose} disabled={!inited}>
                Close
              </button>
              <button className="sf-btn sf-btn--danger" type="button" onClick={onClear}>
                Clear
              </button>
            </div>

            {errorMsg ? (
              <div style={{ color: "rgba(251,113,133,0.95)", fontWeight: 650, fontSize: 13 }}>
                {errorMsg}
              </div>
            ) : null}

            <div className="sf-muted" style={{ fontSize: 12 }}>
              Send with <span className="sf-kbd">Ctrl</span>+<span className="sf-kbd">Enter</span>
            </div>
          </div>
        </section>

        <section className="sf-card">
          <div className="sf-card__inner chat-page__chat">
            <div className="sf-title">Conversation</div>

            <div className="chat-box" ref={chatRef}>
              {renderedMsgs.length ? (
                renderedMsgs.map((m) => (
                  <div key={m.id} className={`msg msg--${m.who}`}>
                    <div className="msg__meta">
                      <span>{m.who === "user" ? "You" : "Agent"}</span>
                      <span className="sf-muted" style={{ fontSize: 11 }}>
                        {new Date(m.ts).toLocaleTimeString()}
                      </span>
                    </div>

                    {m.who === "bot" ? (
                      <div
                          className="msg__content sf-markdown"
                        dangerouslySetInnerHTML={{ __html: m.safeHtml }}
                      />
                    ) : (
                      <div className="msg__content">{m.text}</div>
                    )}
                  </div>
                ))
              ) : (
                <p className="chat-box__hint">No messages yet.</p>
              )}
            </div>

            <div className="chat-page__composer">
              <Field label="Message">
                <textarea
                  className="sf-textarea"
                  value={userMsg}
                  onChange={(e) => setUserMsg(e.target.value)}
                  placeholder="Write a message..."
                  onKeyDown={(e) => {
                    if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
                      e.preventDefault();
                      onSend();
                    }
                  }}
                />
              </Field>
              <div className="chat-page__composer-row">
                <div className="sf-muted" style={{ fontSize: 12 }}>
                  The agent can call MCP tools. Use concise instructions for best results.
                </div>
                <button className="sf-btn sf-btn--primary" type="button" onClick={onSend} disabled={busy}>
                  {busy ? "Sending..." : "Send"}
                </button>
              </div>
            </div>
          </div>
        </section>
      </div>
    </div>
  );
}
