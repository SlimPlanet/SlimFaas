import React, { useEffect, useMemo, useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { apiJson } from "../lib/api";
import type { ConfigurationDto, EnvironmentListDto, LoadCatalogResponseDto, TenantListItemDto } from "../lib/types";

type Mode = "create" | "edit";

const DEFAULT_AUTH_POLICY = `issuer:
  - "https://issuer.example.com"
audience:
  - "mcp-gateway"
jwksUrl: "https://issuer.example.com/.well-known/jwks.json"
algorithms:
  - "RS256"
requiredClaims:
  scope: "mcp"
dpop:
  enabled: false
  iatWindowSeconds: 300
`;

const DEFAULT_RATE_LIMIT_POLICY = `type: fixedWindow
permitLimit: 30
windowSeconds: 60
queueLimit: 0
identity: subject
rejectionStatusCode: 429
rejectionMessage: "Too Many Requests"
`;

export default function ConfigurationEditorPage({ mode }: { mode: Mode }) {
  const { id } = useParams();
  const navigate = useNavigate();

  const [tenants, setTenants] = useState<TenantListItemDto[]>([]);
  const [envs, setEnvs] = useState<string[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [author, setAuthor] = useState("unknown");

  const [tenantId, setTenantId] = useState<string | null>(null);
  const [name, setName] = useState("");
  const [upstreamMcpUrl, setUpstreamMcpUrl] = useState("");
  const [description, setDescription] = useState("");
  const [discoveryJwtToken, setDiscoveryJwtToken] = useState<string>(""); // empty means clear, undefined means keep; UI uses checkbox
  const [changeDiscoveryToken, setChangeDiscoveryToken] = useState(false);

  const [catalogOverrideYaml, setCatalogOverrideYaml] = useState("");
  const [enforceAuthEnabled, setEnforceAuthEnabled] = useState(false);
  const [authPolicyYaml, setAuthPolicyYaml] = useState(DEFAULT_AUTH_POLICY);
  const [rateLimitEnabled, setRateLimitEnabled] = useState(false);
  const [rateLimitPolicyYaml, setRateLimitPolicyYaml] = useState(DEFAULT_RATE_LIMIT_POLICY);
  const [catalogCacheTtlMinutes, setCatalogCacheTtlMinutes] = useState(5);

  const [hasExistingToken, setHasExistingToken] = useState(false);

  const pageTitle = mode === "create" ? "New configuration" : "Edit configuration";

  async function loadLookups() {
    const [t, e] = await Promise.all([
      apiJson<TenantListItemDto[]>("/api/tenants"),
      apiJson<EnvironmentListDto>("/api/environments"),
    ]);
    setTenants(t);
    setEnvs(e.environments);
  }

  async function loadConfiguration(cfgId: string) {
    const dto = await apiJson<ConfigurationDto>(`/api/configurations/${cfgId}`);
    setTenantId(dto.tenantId ?? null);
    setName(dto.name);
    setUpstreamMcpUrl(dto.upstreamMcpUrl);
    setDescription(dto.description ?? "");
    setCatalogOverrideYaml(dto.catalogOverrideYaml ?? "");
    setEnforceAuthEnabled(dto.enforceAuthEnabled);
    setAuthPolicyYaml(dto.authPolicyYaml ?? DEFAULT_AUTH_POLICY);
    setRateLimitEnabled(dto.rateLimitEnabled);
    setRateLimitPolicyYaml(dto.rateLimitPolicyYaml ?? DEFAULT_RATE_LIMIT_POLICY);
    setCatalogCacheTtlMinutes(dto.catalogCacheTtlMinutes ?? 0);
    setHasExistingToken(dto.hasDiscoveryJwtToken);
  }

  useEffect(() => {
    void (async () => {
      try {
        setError(null);
        await loadLookups();
        if (mode === "edit" && id) {
          await loadConfiguration(id);
        } else {
          // defaults
          setTenantId(null);
        }
      } catch (e: any) {
        setError(e?.message ?? String(e));
      }
    })();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mode, id]);

  const tenantOptions = useMemo(() => {
    const arr = [...tenants];
    arr.sort((a, b) => a.name.localeCompare(b.name));
    return arr;
  }, [tenants]);

  async function loadCatalog() {
    if (!id) return;
    try {
      setLoading(true);
      setError(null);
      const res = await apiJson<LoadCatalogResponseDto>(`/api/configurations/${id}/load-catalog`, "POST");
      setCatalogOverrideYaml(res.catalogYaml);
    } catch (e: any) {
      setError(e?.message ?? String(e));
    } finally {
      setLoading(false);
    }
  }

  async function save() {
    try {
      setLoading(true);
      setError(null);

      const payload = {
        name,
        tenantId: tenantId || null,
        upstreamMcpUrl,
        description: description || null,
        discoveryJwtToken: changeDiscoveryToken ? discoveryJwtToken : null,
        catalogOverrideYaml: catalogOverrideYaml || null,
        enforceAuthEnabled,
        authPolicyYaml: enforceAuthEnabled ? authPolicyYaml : null,
        rateLimitEnabled,
        rateLimitPolicyYaml: rateLimitEnabled ? rateLimitPolicyYaml : null,
        catalogCacheTtlMinutes: Number.isFinite(catalogCacheTtlMinutes) ? catalogCacheTtlMinutes : 0,
      };

      if (mode === "create") {
        const created = await apiJson<ConfigurationDto>("/api/configurations", "POST", payload, author);
        navigate(`/configurations/${created.id}`);
      } else if (mode === "edit" && id) {
        await apiJson<ConfigurationDto>(`/api/configurations/${id}`, "PUT", payload, author);
        await loadConfiguration(id);
      }
    } catch (e: any) {
      setError(e?.message ?? String(e));
    } finally {
      setLoading(false);
    }
  }

  async function remove() {
    if (!id) return;
    if (!confirm("Delete this configuration?")) return;
    try {
      setLoading(true);
      setError(null);
      await apiJson(`/api/configurations/${id}`, "DELETE", undefined, author);
      navigate("/");
    } catch (e: any) {
      setError(e?.message ?? String(e));
    } finally {
      setLoading(false);
    }
  }

  return (
    <section className="page">
      <div className="page__header">
        <div className="page__header-left">
          <h1 className="page__title">{pageTitle}</h1>
          {mode === "edit" && id && (
            <div className="page__subtitle">
              <Link className="link" to={`/configurations/${id}/deployments`}>Deployment</Link>
              <span className="sep">•</span>
              <span>Environments: {envs.join(", ")}</span>
            </div>
          )}
        </div>
        <div className="page__actions">
          <Link className="button" to="/">Back</Link>
          {mode === "edit" && id && (
            <button className="button button--danger" onClick={() => void remove()} disabled={loading}>
              Delete
            </button>
          )}
          <button className="button button--primary" onClick={() => void save()} disabled={loading || !name.trim() || !upstreamMcpUrl.trim()}>
            Save
          </button>
        </div>
      </div>

      {error && <div className="alert alert--error">{error}</div>}

      <div className="grid grid--two">
        <div className="card">
          <h2 className="card__title">Basics</h2>

          <label className="field">
            <span className="field__label">Audit author</span>
            <input className="input" value={author} onChange={(e) => setAuthor(e.target.value)} />
            <span className="field__hint">Sent as <code className="code">X-Audit-Author</code> (default: unknown).</span>
          </label>

          <label className="field">
            <span className="field__label">Tenant</span>
            <select className="input" value={tenantId ?? ""} onChange={(e) => setTenantId(e.target.value || null)}>
              <option value="">default</option>
              {tenantOptions
                .filter((t) => t.name.toLowerCase() !== "default")
                .map((t) => (
                  <option value={t.id} key={t.id}>{t.name}</option>
                ))}
            </select>
          </label>

          <label className="field">
            <span className="field__label">Configuration name</span>
            <input className="input" value={name} onChange={(e) => setName(e.target.value)} placeholder="my-mcp" />
            <span className="field__hint">Used to generate the gateway URL path segment.</span>
          </label>

          <label className="field">
            <span className="field__label">Upstream MCP base URL</span>
            <input className="input" value={upstreamMcpUrl} onChange={(e) => setUpstreamMcpUrl(e.target.value)} placeholder="https://mcp.example.com" />
          </label>

          <label className="field">
            <span className="field__label">Description</span>
            <input className="input" value={description} onChange={(e) => setDescription(e.target.value)} />
          </label>

          <label className="field">
            <span className="field__label">Catalog cache TTL (minutes)</span>
            <input
              className="input"
              type="number"
              min={0}
              value={catalogCacheTtlMinutes}
              onChange={(e) => setCatalogCacheTtlMinutes(parseInt(e.target.value || "0", 10))}
            />
            <span className="field__hint">Applies to <code className="code">/tools</code>, <code className="code">/resources</code>, <code className="code">/prompts</code>.</span>
          </label>

          <div className="divider" />

          <div className="toggle">
            <input
              id="changeToken"
              type="checkbox"
              checked={changeDiscoveryToken}
              onChange={(e) => setChangeDiscoveryToken(e.target.checked)}
            />
            <label htmlFor="changeToken" className="toggle__label">
              Update discovery token
              {hasExistingToken && <span className="badge">token stored</span>}
            </label>
          </div>

          {changeDiscoveryToken && (
            <label className="field">
              <span className="field__label">Discovery JWT token</span>
              <input
                className="input"
                value={discoveryJwtToken}
                onChange={(e) => setDiscoveryJwtToken(e.target.value)}
                placeholder="Leave empty to clear"
              />
              <span className="field__hint">
                Stored encrypted in SQLite. Never returned by the API in plaintext.
              </span>
            </label>
          )}

          {mode === "edit" && id && (
            <div className="card__actions">
              <button className="button" onClick={() => void loadCatalog()} disabled={loading}>
                Load catalog (tools/resources/prompts)
              </button>
            </div>
          )}
        </div>

        <div className="card">
          <h2 className="card__title">Catalog override (YAML)</h2>
          <textarea
            className="textarea textarea--code"
            value={catalogOverrideYaml}
            onChange={(e) => setCatalogOverrideYaml(e.target.value)}
            placeholder="Paste editable YAML here (e.g. tools allow-list, description overrides, etc.)"
            rows={16}
          />
          <div className="hint">
            The gateway applies allow-listing and simple scalar overrides to tools/resources/prompts responses.
          </div>
        </div>

        <div className="card">
          <h2 className="card__title">Authentication enforcement</h2>

          <div className="toggle">
            <input
              id="authEnabled"
              type="checkbox"
              checked={enforceAuthEnabled}
              onChange={(e) => setEnforceAuthEnabled(e.target.checked)}
            />
            <label htmlFor="authEnabled" className="toggle__label">Enforce JWT / OIDC (optional DPoP)</label>
          </div>

          {enforceAuthEnabled && (
            <label className="field">
              <span className="field__label">Auth policy (YAML)</span>
              <textarea
                className="textarea textarea--code"
                value={authPolicyYaml}
                onChange={(e) => setAuthPolicyYaml(e.target.value)}
                rows={14}
              />
              <span className="field__hint">
                Minimal fields: issuer (list), audience (list), jwksUrl (string). Optionally requiredClaims + dpop.
              </span>
            </label>
          )}
        </div>

        <div className="card">
          <h2 className="card__title">Rate limiting</h2>

          <div className="toggle">
            <input
              id="rlEnabled"
              type="checkbox"
              checked={rateLimitEnabled}
              onChange={(e) => setRateLimitEnabled(e.target.checked)}
            />
            <label htmlFor="rlEnabled" className="toggle__label">Enable per-identity rate limiting</label>
          </div>

          {rateLimitEnabled && (
            <label className="field">
              <span className="field__label">Rate limit policy (YAML)</span>
              <textarea
                className="textarea textarea--code"
                value={rateLimitPolicyYaml}
                onChange={(e) => setRateLimitPolicyYaml(e.target.value)}
                rows={12}
              />
              <span className="field__hint">
                identity can be: <code className="code">subject</code>, <code className="code">client_id</code>, <code className="code">ip</code>, or <code className="code">header:X-Api-Key</code>.
              </span>
            </label>
          )}
        </div>
      </div>
    </section>
  );
}
