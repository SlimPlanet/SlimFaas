import React, { useEffect, useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { apiJson } from "../lib/api";
import type { ConfigurationListItemDto } from "../lib/types";

export default function ConfigurationsPage() {
  const [items, setItems] = useState<ConfigurationListItemDto[]>([]);
  const [query, setQuery] = useState("");
  const [error, setError] = useState<string | null>(null);

  async function load() {
    try {
      setError(null);
      const data = await apiJson<ConfigurationListItemDto[]>("/api/configurations");
      setItems(data);
    } catch (e: any) {
      setError(e?.message ?? String(e));
    }
  }

  useEffect(() => {
    void load();
  }, []);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return items;
    return items.filter(
      (x) =>
        x.name.toLowerCase().includes(q) ||
        x.tenantName.toLowerCase().includes(q) ||
        x.gatewayUrl.toLowerCase().includes(q)
    );
  }, [items, query]);

  return (
    <section className="page">
      <div className="page__header">
        <h1 className="page__title">Configurations</h1>
        <div className="page__actions">
          <input
            className="input input--search"
            placeholder="Search…"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
          <button className="button" onClick={() => void load()}>
            Refresh
          </button>
          <Link className="button button--primary" to="/configurations/new">
            + New configuration
          </Link>
        </div>
      </div>

      {error && <div className="alert alert--error">{error}</div>}

      <div className="card">
        <div className="table">
          <div className="table__row table__row--header">
            <div className="table__cell">Tenant</div>
            <div className="table__cell">Name</div>
            <div className="table__cell">Gateway URL</div>
            <div className="table__cell">Created</div>
            <div className="table__cell table__cell--actions">Actions</div>
          </div>
          {filtered.map((x) => (
            <div className="table__row" key={x.id}>
              <div className="table__cell">{x.tenantName}</div>
              <div className="table__cell">{x.name}</div>
              <div className="table__cell">
                <code className="code">{x.gatewayUrl}</code>
              </div>
              <div className="table__cell">{new Date(x.createdAtUtc).toLocaleString()}</div>
              <div className="table__cell table__cell--actions">
                <Link className="button button--ghost" to={`/configurations/${x.id}`}>
                  Edit
                </Link>
                <Link className="button button--ghost" to={`/configurations/${x.id}/deployments`}>
                  Deploy
                </Link>
              </div>
            </div>
          ))}
          {filtered.length === 0 && (
            <div className="table__row">
              <div className="table__cell table__cell--empty">No configurations</div>
            </div>
          )}
        </div>
      </div>
    </section>
  );
}
