import React from "react";
import "../styles/diff.scss";

interface DiffLine {
  type: "Unchanged" | "Inserted" | "Deleted" | "Modified" | "Imaginary";
  text: string;
  position: number | null;
}

interface DiffViewerProps {
  lines: DiffLine[];
}

export default function DiffViewer({ lines }: DiffViewerProps) {
  if (!lines || lines.length === 0) {
    return <div className="diff-viewer__empty">No changes</div>;
  }

  return (
    <div className="diff-viewer">
      <table className="diff-viewer__table">
        <tbody>
          {lines.map((line, index) => {
            const className = `diff-viewer__line diff-viewer__line--${line.type.toLowerCase()}`;
            const prefix = getPrefix(line.type);

            return (
              <tr key={index} className={className}>
                <td className="diff-viewer__line-number">{line.position ?? ""}</td>
                <td className="diff-viewer__prefix">{prefix}</td>
                <td className="diff-viewer__text">
                  <code>{line.text}</code>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function getPrefix(type: string): string {
  switch (type) {
    case "Inserted":
      return "+";
    case "Deleted":
      return "-";
    case "Modified":
      return "~";
    default:
      return " ";
  }
}
