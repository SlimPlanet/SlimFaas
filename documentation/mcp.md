# SlimFaas MCP [![Docker SlimFaas](https://img.shields.io/docker/pulls/axaguildev/slimfaas-mcp.svg?label=docker+pull+slimfaas-mcp)](https://hub.docker.com/r/axaguildev/slimfaas-mcp/builds) [![Docker Image Size](https://img.shields.io/docker/image-size/axaguildev/slimfaas-mcp?label=image+size+slimfaas-mcp)](https://hub.docker.com/r/axaguildev/slimfaas/builds) [![Docker Image Version](https://img.shields.io/docker/v/axaguildev/slimfaas-mcp?sort=semver&label=latest+version+slimfaas-mcp)](https://hub.docker.com/r/axaguildev/slimfaas-mcp/builds)

Adopt the **Model‑Context‑Protocol (MCP)** at scale—without rewriting a single API! SlimFaas MCP is one of the simpler and faster MCP runtimes available, turning *any* OpenAPI (Swagger) definition into a fully compliant MCP toolset on the fly.

* **Dynamic proxy** – SlimFaas MCP turns *any* `openapi.json` into a 100 % MCP‑ready endpoint, with zero impact on your code and no compromise on security (your OIDC tokens still flow as usual).
* **Live documentation & prompting overrides** – enrich or replace endpoint descriptions and schemas in flight via the `mcp_prompt` parameter.
* **Tiny & native** – single self‑contained binary > 15MB for Linux, macOS and Windows, plus multi‑arch Docker images (x64 & ARM).
* **Flexible** – hot‑swap docs whenever you need—no rebuild, no downtime.
* **Security** - Implement RFC 9728 for client dynamic discovery. Works with all Oauth flow: PKCE, mTLS, DPoP…
  * https://datatracker.ietf.org/doc/rfc9728/

Grab the latest binaries on the **[GitHub Releases](https://github.com/SlimPlanet/SlimFaas/releases)** page:

| OS / Arch   | File                      |
| ----------- | ------------------------- |
| Linux x64   | `SlimFaasMcp-linux-x64`   |
| macOS ARM64 | `SlimFaasMcp-osx-arm64`   |
| macOS x64   | `SlimFaasMcp-osx-x64`     |
| Windows x64 | `SlimFaasMcp-win-x64.exe` |

---

This project is a **runtime MCP proxy** that dynamically generates SlimFaas‑compatible MCP tools from any remote Swagger (OpenAPI v3) document. Every endpoint in the source API is exposed as a *tool*, complete with an on‑the‑fly YAML manifest and a real‑time reverse proxy to the underlying API.

---

## 🚀 Features

* **Dynamic download** of any remote Swagger (OpenAPI v3) document at runtime.
* **Parses & exposes** every endpoint (GET, POST, PUT, DELETE, …) as an MCP tool.
* **Documentation overriding** through the `mcp_prompt` mechanism (Base‑64‑encoded JSON/YAML).
* **No static code generation**—everything is live, built from the Swagger URL.
* **Key endpoints**

    * `POST /mcp` (JSON‑RPC 2.0)

        * Query – `openapi_url`, `base_url`, `mcp_prompt`, `oauth`, `structured_content`
    * `GET  /tools` – list all generated MCP tools
      * Query – `openapi_url`, `base_url`, `mcp_prompt`
    * `POST /tools/{toolName}` – execute a proxied call to the API
      * Query – `openapi_url`, `base_url`, `mcp_prompt`, `oauth`, `structured_content`
    * `GET /{oauth}/.well-known/oauth-protected-resource` – for client dynamic authorization server discovery (RFC 9728)
* **Minimal Web UI** served at `/index.html` for interactive testing.

---

## 📖 Query Parameters Cheat Sheet

> Applies to both `POST /mcp` (JSON-RPC endpoint) and the UI helper endpoint `POST /tools/{toolName}`.

| Parameter            | Required | Type / Format                                                      | Purpose                                                                      | Notes                                                                                                                                                                                     | Example                                            |
| -------------------- | -------- | ------------------------------------------------------------------ | ---------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------- |
| `openapi_url`        | ✅ Yes    | Absolute URL                                                       | Location of the OpenAPI (JSON) document to consume.                          | Used to discover endpoints (tools).                                                                                                                                                       | `https://petstore3.swagger.io/api/v3/openapi.json` |
| `base_url`           | ✅ Yes | Absolute URL                                                       | Overrides the base URL used for actual API calls.                            | Useful if the OpenAPI doc is hosted separately from the API origin.                                                                                                                       | `https://petstore3.swagger.io/api/v3`              |
| `mcp_prompt`         | Optional | **Base64** of a JSON object (or YAML converted to JSON)            | Filters/overrides tools (`activeTools`, descriptions, input/output schemas). | Must be **UTF-8 base64**. Server applies overrides **before** exposing `tools/list`.                                                                                                      | See base64 example below                           |
| `oauth`              | Optional | **Base64** of an OAuth Protected Resource Metadata JSON (RFC 9728) | Lets the server advertise protected resource metadata.                       | If present **and** there is **no** `Authorization` header, server returns `401` with a `WWW-Authenticate: Bearer resource_metadata=".../.well-known/oauth-protected-resource"` challenge. | See base64 example below                           |
| `structured_content` | Optional | Boolean (`true`/`false`)                                           | Enables inclusion of **`structuredContent`** (JSON object) in responses.     | When `true` and the upstream response is JSON, the server returns **both** a human-readable `content[]` block (with a `text` item) **and** a machine-readable `structuredContent` object. | `true`                                             |

---

### Ready-to-use examples

#### 1) `oauth` (base64)

**Raw JSON (minified):**

```json
{"resource":"https://api.example.com/v1/","authorization_servers":["https://auth.example.com"],"scopes_supported":["read:data","write:data"]}
```

**Base64 (UTF-8):**

```
eyJyZXNvdXJjZSI6Imh0dHBzOi8vYXBpLmV4YW1wbGUuY29tL3YxLyIsImF1dGhvcml6YXRpb25fc2VydmVycyI6WyJodHRwczovL2F1dGguZXhhbXBsZS5jb20iXSwic2NvcGVzX3N1cHBvcnRlZCI6WyJyZWFkOmRhdGEiLCJ3cml0ZTpkYXRhIl19
```

> ⚠️ Always **URL-encode** base64 when putting it in a query string (escape `+`, `/`, `=`).

---

#### 2) `mcp_prompt` (base64)

**Raw JSON (minified) — example override:**

```json
{"activeTools":["getPets","post_pet_petId_uploadImage"],"tools":[{"name":"post_pet_petId_uploadImage","description":"Uploads an image","inputSchema":{"type":"object","properties":{"petId":{"type":"integer"},"body":{"type":"string","format":"binary"}},"required":["petId","body"]}}]}
```

**Base64 (UTF-8):**

```
eyJhY3RpdmVUb29scyI6WyJnZXRQZXRzIiwicG9zdF9wZXRfcGV0SWRfdXBsb2FkSW1hZ2UiXSwidG9vbHMiOlt7Im5hbWUiOiJwb3N0X3BldF9wZXRJZF91cGxvYWRJbWFnZSIsImRlc2NyaXB0aW9uIjoiVXBsb2FkcyBhbiBpbWFnZSIsImlucHV0U2NoZW1hIjp7InR5cGUiOiJvYmplY3QiLCJwcm9wZXJ0aWVzIjp7InBldElkIjp7InR5cGUiOiJpbnRlZ2VyIn0sImJvZHkiOnsicHlwZSI6InN0cmluZyIsImZvcm1hdCI6ImJpbmFyeSJ9fSwicmVxdWlyZWQiOlsicGV0SWQiLCJib2R5Il19fV19
```

---

#### 3) Full URL (all params together)

```
https://localhost:5001/mcp
  ?openapi_url=https%3A%2F%2Fpetstore3.swagger.io%2Fapi%2Fv3%2Fopenapi.json
  &base_url=https%3A%2F%2Fpetstore3.swagger.io%2Fapi%2Fv3
  &mcp_prompt=eyJhY3RpdmV...fV19
  &oauth=eyJyZXNvdXJ...aIl19
  &structured_content=true
```

> The base64 strings are truncated here for readability—use the full strings above (URL-encoded).


## 📦 Quick start

```bash
# Pull & run the latest multi‑arch image

docker run --rm -p 8080:8080  -e ASPNETCORE_URLS="http://*:8080" axaguildev/slimfaas-mcp:latest
```

The API listens on **[http://localhost:8080](http://localhost:8080)** by default.

---

## 🖥️ Web UI for tool testing

1. Open **[http://localhost:8080/index.html](http://localhost:8080/index.html)**.
2. Enter a Swagger URL (e.g. `https://petstore3.swagger.io/api/v3/openapi.json`).
3. Click **Load Tools**.
4. Explore and call MCP tools directly from your browser.

---

## 📖 Main API endpoints

| Method & path            | Description                                                          |
|--------------------------|----------------------------------------------------------------------|
| `POST /mcp`              | MCP JSON‑RPC 2.0 endpoint (`openapi_url`, `base_url`, `mcp_prompt`). |
| `GET /tools`             | Returns the list of MCP tools generated from the provided Swagger.   |
| `POST /tools/{toolName}` | Executes the specified tool. Body = JSON arguments.                  |
| `GET /{oauth}/.well-known/oauth-protected-resource`  | Authorization server configuration in JSON format                    |
| `GET /index.html`        | Minimalist web UI.                                                   |

---

## 🛠️ Example usage

1. Browse to **[http://localhost:8080/index.html](http://localhost:8080/index.html)**.
2. Use the public PetStore Swagger URL: `https://petstore3.swagger.io/api/v3/openapi.json`.
3. Click **Load Tools**. Displayed informations are exactly the same as an MCP Client will see.
4. For any generated tool:

    * Provide a JSON payload.
    * Click **Run** to see the live proxied response.

