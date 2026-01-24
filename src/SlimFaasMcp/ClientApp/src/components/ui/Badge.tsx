import "./Badge.scss";

type Props = { kind?: "ok" | "new" | "off"; text: string };

export default function Badge({ kind = "ok", text }: Props) {
  return <span className={`badge badge--${kind}`}>{text}</span>;
}
