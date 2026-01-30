import React, { useEffect, useMemo, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { apiJson, apiText } from "../lib/api";
import type { AuditDiffDto, AuditTextDiffDto, DeploymentOverviewDto } from "../lib/types";
import DiffViewer from "../components/DiffViewer";

export default function DeploymentPage() {
  const { id } = useParams();
  const [overview, setOverview] = useState<DeploymentOverviewDto | null>(null);
  const [author, setAuthor] = useState("unknown");
  const [error, setError] = useState<string | null>(null);

  const [diffFrom, setDiffFrom] = useState<number | null>(null);
  const [diffTo, setDiffTo] = useState<number | null>(null);
  const [diff, setDiff] = useState<AuditDiffDto | null>(null);
  const [textDiff, setTextDiff] = useState<AuditTextDiffDto | null>(null);
  const [useTextDiff, setUseTextDiff] = useState(false);

  const [snapshotIndex, setSnapshotIndex] = useState<number | null>(null);
  const [snapshotJson, setSnapshotJson] = useState<string | null>(null);

  async function load() {
    if (!id) return;
    try {
      setError(null);
      const data = await apiJson<DeploymentOverviewDto>(`/api/configurations/${id}/deployments`);
      setOverview(data);
      if (data.history.length > 0) {
        setDiffFrom(data.history[0].index);
        setDiffTo(data.history[data.history.length - 1].index);
      }
    } catch (e: any) {
      setError(e?.message ?? String(e));
    }
  }

  useEffect(() => {
    void load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id]);

  const historyOptions = useMemo(() => {
    if (!overview) return [];
    return overview.history.map((h) => h.index);
  }, [overview]);

  async function setDeployment(environmentName: string, deployedAuditIndex: number | null) {
    if (!id) return;
    try {
      setError(null);
      await apiJson(
        `/api/configurations/${id}/deployments/${encodeURIComponent(environmentName)}`,
        "PUT",
        { deployedAuditIndex },
        author
      );
      await load();
    } catch (e: any) {
      setError(e?.message ?? String(e));
    }
  }

  async function loadDiff() {
    if (!id || diffFrom === null || diffTo === null) return;
    try {
      setError(null);
      if (useTextDiff) {
        const d = await apiJson<AuditTextDiffDto>(`/api/configurations/${id}/textdiff?from=${diffFrom}&to=${diffTo}`);
        setTextDiff(d);
        setDiff(null);
      } else {
        const d = await apiJson<AuditDiffDto>(`/api/configurations/${id}/diff?from=${diffFrom}&to=${diffTo}`);
        setDiff(d);
        setTextDiff(null);
      }
    } catch (e: any) {
      setError(e?.message ?? String(e));
    }
  }

  async function loadSnapshot() {
    if (!id || snapshotIndex === null) return;
    try {
      setError(null);
      const text = await apiText(`/api/configurations/${id}/snapshot/${snapshotIndex}`);
      setSnapshotJson(text);
    } catch (e: any) {
      setError(e?.message ?? String(e));
    }
  }

  if (!id) return <div className="alert alert--error">Missing configuration id</div>;

  return (
    <section className="page">
      <div className="page__header">
        <div className="page__header-left">
          <h1 className="page__title">Deployments</h1>
          {overview && (
            <div className="page__subtitle">
              <span className="badge">{overview.tenantName}</span>
              <span className="sep">•</span>
              <span>{overview.configurationName}</span>
            </div>
          )}
        </div>
        <div className="page__actions">
          <Link className="button" to={`/configurations/${id}`}>Back</Link>
          <button className="button" onClick={() => void load()}>Refresh</button>
        </div>
      </div>

      {error && <div className="alert alert--error">{error}</div>}

      <div className="grid">
        <div className="card">
          <h2 className="card__title">Environment deployment</h2>

          <label className="field">
            <span className="field__label">Audit author</span>
            <input className="input" value={author} onChange={(e) => setAuthor(e.target.value)} />
          </label>

          {!overview ? (
            <div className="hint">Loading…</div>
          ) : (
            <div className="table">
              <div className="table__row table__row--header">
                <div className="table__cell">Environment</div>
                <div className="table__cell">Deployed audit index</div>
                <div className="table__cell table__cell--actions">Action</div>
              </div>

              {overview.environments.map((e) => (
                <div className="table__row" key={e.environmentName}>
                  <div className="table__cell">{e.environmentName}</div>
                  <div className="table__cell">
                    <select
                      className="input input--select"
                      value={e.deployedAuditIndex ?? ""}
                      onChange={(ev) => {
                        const v = ev.target.value;
                        void setDeployment(e.environmentName, v === "" ? null : parseInt(v, 10));
                      }}
                    >
                      <option value="">(not deployed)</option>
                      {overview.history.map((h) => (
                        <option value={h.index} key={h.index}>
                          {h.index} — {new Date(h.modifiedAtUtc * 1000).toLocaleString()} — {h.author}
                        </option>
                      ))}
                    </select>
                  </div>
                  <div className="table__cell table__cell--actions">
                    <code className="code">
                      /gateway/mcp/{encodeURIComponent(overview.tenantName)}/{e.environmentName}/{encodeURIComponent(overview.configurationName)}
                    </code>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>

        <div className="card">
          <h2 className="card__title">History diff</h2>
          {!overview ? (
            <div className="hint">Loading…</div>
          ) : (
            <>
              <div className="form-row">
                <label className="field field--inline">
                  <span className="field__label">From</span>
                  <select className="input input--select" value={diffFrom ?? ""} onChange={(e) => setDiffFrom(parseInt(e.target.value, 10))}>
                    {historyOptions.map((x) => (
                      <option value={x} key={x}>{x}</option>
                    ))}
                  </select>
                </label>

                <label className="field field--inline">
                  <span className="field__label">To</span>
                  <select className="input input--select" value={diffTo ?? ""} onChange={(e) => setDiffTo(parseInt(e.target.value, 10))}>
                    {historyOptions.map((x) => (
                      <option value={x} key={x}>{x}</option>
                    ))}
                  </select>
                </label>

                <label className="field field--inline">
                  <input type="checkbox" checked={useTextDiff} onChange={(e) => setUseTextDiff(e.target.checked)} />
                  <span className="field__label">Unified diff</span>
                </label>

                <button className="button" onClick={() => void loadDiff()} disabled={diffFrom === null || diffTo === null}>
                  Compute diff
                </button>
              </div>

              {textDiff && (
                <div className="diff">
                  <div className="diff__meta">
                    <div><strong>From</strong>: #{textDiff.from.index} — {new Date(textDiff.from.modifiedAtUtc * 1000).toLocaleString()} — {textDiff.from.author}</div>
                    <div><strong>To</strong>: #{textDiff.to.index} — {new Date(textDiff.to.modifiedAtUtc * 1000).toLocaleString()} — {textDiff.to.author}</div>
                  </div>
                  <DiffViewer lines={textDiff.unifiedDiff.lines} />
                </div>
              )}

              {diff && (
                <div className="diff">
                  <div className="diff__meta">
                    <div><strong>From</strong>: #{diff.from.index} — {new Date(diff.from.modifiedAtUtc * 1000).toLocaleString()} — {diff.from.author}</div>
                    <div><strong>To</strong>: #{diff.to.index} — {new Date(diff.to.modifiedAtUtc * 1000).toLocaleString()} — {diff.to.author}</div>
                  </div>

                  <div className="diff__ops">
                    {diff.patch.length === 0 ? (
                      <div className="hint">No changes</div>
                    ) : (
                      diff.patch.map((op, i) => (
                        <div className="diff__op" key={i}>
                          <span className={"diff__badge" + (op.remove ? " diff__badge--remove" : " diff__badge--set")}>
                            {op.remove ? "remove" : "set"}
                          </span>
                          <code className="code">{op.path}</code>
                          {!op.remove && <code className="code code--muted">{JSON.stringify(op.value)}</code>}
                        </div>
                      ))
                    )}
                  </div>
                </div>
              )}
            </>
          )}
        </div>

        <div className="card">
          <h2 className="card__title">Snapshot viewer</h2>
          {!overview ? (
            <div className="hint">Loading…</div>
          ) : (
            <>
              <div className="form-row">
                <label className="field field--inline">
                  <span className="field__label">Index</span>
                  <select className="input input--select" value={snapshotIndex ?? ""} onChange={(e) => setSnapshotIndex(parseInt(e.target.value, 10))}>
                    <option value="">Select…</option>
                    {overview.history.map((h) => (
                      <option value={h.index} key={h.index}>{h.index}</option>
                    ))}
                  </select>
                </label>
                <button className="button" onClick={() => void loadSnapshot()} disabled={snapshotIndex === null}>
                  Load snapshot
                </button>
              </div>

              {snapshotJson && (
                <pre className="codeblock">{snapshotJson}</pre>
              )}
            </>
          )}
        </div>
      </div>
    </section>
  );
}
