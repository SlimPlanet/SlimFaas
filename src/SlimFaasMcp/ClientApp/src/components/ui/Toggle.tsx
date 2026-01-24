import "./Toggle.scss";

type Props = {
  checked: boolean;
  onChange: (v: boolean) => void;
  label: string;
  hint?: string;
};

export default function Toggle({ checked, onChange, label, hint }: Props) {
  return (
    <label className="toggle">
      <input
        className="toggle__input"
        type="checkbox"
        checked={checked}
        onChange={(e) => onChange(e.target.checked)}
      />
      <span className={`toggle__pill ${checked ? "toggle__pill--on" : ""}`}>
        <span className="toggle__dot" />
      </span>
      <span className="toggle__text">
        <span className="toggle__label">{label}</span>
        {hint ? <span className="toggle__hint">{hint}</span> : null}
      </span>
    </label>
  );
}
