import { useMemo } from "react";
import YAML from "js-yaml";
import Badge from "../../../components/ui/Badge";
import Field from "../../../components/ui/Field";
import type { UiTool } from "../types";
import "./ToolCard.scss";

type Props = {
  tool: UiTool;
  structuredContentEnabled: boolean;
  onChangeJsonInput: (toolName: string, txt: string) => void;
  onChangeTextInput: (toolName: string, key: string, value: string) => void;
  onChangeFileInput: (toolName: string, key: string, file: File | null) => void;
  onRun: (toolName: string) => void;
  output?: string;
};

function needsFile(val: any): boolean {
  return Boolean(
    val &&
      (val.format === "binary" ||
        (val.properties && val.properties.data && val.properties.data.contentEncoding === "base64")),
  );
}

export default function ToolCard({
  tool,
  structuredContentEnabled,
  onChangeJsonInput,
  onChangeTextInput,
  onChangeFileInput,
  onRun,
  output,
}: Props) {
  const contentType = tool.endpoint?.contentType || "application/json";

  const stateClass = useMemo(() => {
    if (tool.isDisabled) return "tool-card tool-card--disabled";
    if (tool.isAdded) return "tool-card tool-card--added";
    if (tool.isOverridden) return "tool-card tool-card--overridden";
    return "tool-card";
  }, [tool.isAdded, tool.isDisabled, tool.isOverridden]);

  const badges = useMemo(() => {
    const b: { kind: "ok" | "new" | "off"; text: string }[] = [];
    if (tool.isAdded) b.push({ kind: "new", text: "🆕 Added" });
    if (tool.isDisabled) b.push({ kind: "off", text: "⛔ Disabled" });
    if (tool.isOverridden) b.push({ kind: "ok", text: "✅ Overridden" });
    return b;
  }, [tool.isAdded, tool.isDisabled, tool.isOverridden]);

  const props = (tool.inputSchema?.properties || {}) as Record<string, any>;

  const inputSchemaYaml = useMemo(() => YAML.dump(tool.inputSchema || {}), [tool.inputSchema]);
  const outputSchemaYaml = useMemo(() => YAML.dump(tool.outputSchema || {}), [tool.outputSchema]);

  return (
    <section className={stateClass}>
      <div className="tool-card__inner">
        <div className="tool-card__top">
          <h3 className="tool-card__name">{tool.name}</h3>
          <div className="tool-card__badges">
            {badges.map((b, idx) => (
              <span key={idx} style={{ marginLeft: idx ? 8 : 0 }}>
                <Badge kind={b.kind} text={b.text} />
              </span>
            ))}
          </div>
        </div>

        <p className={`tool-card__desc ${tool.isDescriptionOverridden ? "tool-card__desc--changed" : ""}`}>
          {tool.description}
        </p>

        <div className="tool-card__cols">
          <div className="tool-card__col">
            <div className="tool-card__col-title">Input schema</div>
            <pre className={`tool-card__schema ${tool.isInputSchemaOverridden ? "tool-card__schema--changed" : ""}`}>
{inputSchemaYaml}
            </pre>
          </div>

          <div className="tool-card__col">
            <div className="tool-card__col-title">Output schema</div>
            <pre className="tool-card__schema">
{structuredContentEnabled ? outputSchemaYaml : "(hidden — enable structured_content)"}
            </pre>
          </div>
        </div>

        <div className="tool-card__actions">
          {!tool.isDisabled ? (
            <button className="sf-btn sf-btn--primary" onClick={() => onRun(tool.name)} type="button">
              Run
            </button>
          ) : null}
          <span className="tool-card__content-type">Content-Type: {contentType}</span>
        </div>

        {!tool.isDisabled ? (
          <div className="tool-card__form">
            {contentType === "multipart/form-data" || contentType === "application/octet-stream" ? (
              <div className="tool-card__form-grid">
                {Object.entries(props).map(([key, val]) => {
                  const isFile = needsFile(val);
                  return (
                    <Field key={key} label={isFile ? `${key} (file)` : key}>
                      {isFile ? (
                        <input
                          className="sf-input"
                          type="file"
                          onChange={(e) => onChangeFileInput(tool.name, key, e.target.files?.[0] ?? null)}
                        />
                      ) : (
                        <input
                          className="sf-input"
                          type="text"
                          value={tool.uiTextInputs?.[key] ?? ""}
                          onChange={(e) => onChangeTextInput(tool.name, key, e.target.value)}
                        />
                      )}
                    </Field>
                  );
                })}
              </div>
            ) : (
              <Field label="JSON input" hint="Provide the tool arguments as JSON">
                <textarea
                  className="sf-textarea"
                  value={tool.uiJsonInput ?? "{}"}
                  onChange={(e) => onChangeJsonInput(tool.name, e.target.value)}
                />
              </Field>
            )}

            {output ? <div className="tool-card__output">{output}</div> : null}
          </div>
        ) : (
          <div className="sf-muted" style={{ fontSize: 13 }}>
            Tool disabled
          </div>
        )}
      </div>
    </section>
  );
}
