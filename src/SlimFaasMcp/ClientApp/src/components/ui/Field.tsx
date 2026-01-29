import type { ReactNode } from "react";
import "./Field.scss";

type Props = {
  label: string;
  hint?: string;
  children: ReactNode;
};

export default function Field({ label, hint, children }: Props) {
  return (
    <div className="field">
      <div className="field__label">{label}</div>
      {hint ? <div className="field__hint">{hint}</div> : null}
      <div className="field__control">{children}</div>
    </div>
  );
}
