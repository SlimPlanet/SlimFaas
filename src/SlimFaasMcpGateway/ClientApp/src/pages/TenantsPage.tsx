import React, { useEffect, useState } from "react";
import { apiJson } from "../lib/api";
import type { TenantListItemDto } from "../lib/types";

export default function TenantsPage() {
  const [items, setItems] = useState<TenantListItemDto[]>([]);
  const [name, setName] = useState("");
  const [desc, setDesc] = useState("");
  const [author, setAuthor] = useState("unknown");
  const [error, setError] = useState<string | null>(null);

  async function load() {
    try {
      setError(null);
      const data = await apiJson<TenantListItemDto[]>("/api/tenants");
      setItems(data);
    } catch (e: any) {
      setError(e?.message ?? String(e));
    }
  }

  useEffect(() => {
    void load();
  }, []);

  async function create() {
    try {
      setError(null);
      await apiJson("/api/tenants", "POST", { name, description: desc || null }, author);
      setName("");
      setDesc("");
      await load();
    } catch (e: any) {
      setError(e?.message ?? String(e));
    }
  }

  return (
    <section className="page">
      <div className="page__header">
        <h1 className="page__title">Tenants</h1>
        <div className="page__actions">
          <button className="button" onClick={() => void load()}>Refresh</button>
        </div>
      </div>

      {error && <div className="alert alert--error">{error}</div>}

      <div className="grid">
        <div className="card">
          <h2 className="card__title">Create tenant</h2>
          <label className="field">
            <span className="field__label">Audit author</span>
            <input className="input" value={author} onChange={(e) => setAuthor(e.target.value)} />
          </label>
          <label className="field">
            <span className="field__label">Name</span>
            <input className="input" value={name} onChange={(e) => setName(e.target.value)} placeholder="default, acme, ..." />
          </label>
          <label className="field">
            <span className="field__label">Description</span>
            <input className="input" value={desc} onChange={(e) => setDesc(e.target.value)} />
          </label>
          <div className="card__actions">
            <button className="button button--primary" onClick={() => void create()} disabled={!name.trim()}>
              Create
            </button>
          </div>
        </div>

        <div className="card">
          <h2 className="card__title">Existing tenants</h2>
          <div className="table">
            <div className="table__row table__row--header">
              <div className="table__cell">Name</div>
              <div className="table__cell">Description</div>
            </div>
            {items.map((t) => (
              <div className="table__row" key={t.id}>
                <div className="table__cell">{t.name}</div>
                <div className="table__cell">{t.description ?? ""}</div>
              </div>
            ))}
            {items.length === 0 && (
              <div className="table__row">
                <div className="table__cell table__cell--empty">No tenants</div>
              </div>
            )}
          </div>

          <div className="hint">
            The gateway uses tenant in the URL: <code className="code">/gateway/mcp/&#123;tenant&#125;/&#123;environment&#125;/&#123;configurationName&#125;</code>.
          </div>
        </div>
      </div>
    </section>
  );
}
