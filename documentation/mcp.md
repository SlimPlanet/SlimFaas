# SlimFaas MCP [![Docker SlimFaas](https://img.shields.io/docker/pulls/axaguildev/slimfaas-mcp.svg?label=docker+pull+slimfaas-mcp)](https://hub.docker.com/r/axaguildev/slimfaas-mcp/builds) [![Docker Image Size](https://img.shields.io/docker/image-size/axaguildev/slimfaas-mcp?label=image+size+slimfaas-mcp)](https://hub.docker.com/r/axaguildev/slimfaas/builds) [![Docker Image Version](https://img.shields.io/docker/v/axaguildev/slimfaas-mcp?sort=semver&label=latest+version+slimfaas-mcp)](https://hub.docker.com/r/axaguildev/slimfaas-mcp/builds)

Adopt the **Model‑Context‑Protocol (MCP)** at scale—without rewriting a single API!

* **Dynamic proxy** – SlimFaas MCP turns *any* `openapi.json` into a 100 % MCP‑ready endpoint, with zero impact on your code and no compromise on security (your OIDC tokens still flow as usual).
* **Live documentation & prompting overrides** – enrich or replace endpoint descriptions and schemas in flight via the `mcp_prompt` parameter.
* **Tiny & native** – single self‑contained binary > 15MB for Linux, macOS and Windows, plus multi‑arch Docker images (x64 & ARM).
* **Flexible** – hot‑swap docs whenever you need—no rebuild, no downtime.

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

        * Query – `openapi_url`, `base_url`, `mcp_prompt`
    * `GET  /tools` – list all generated MCP tools
    * `POST /tools/{toolName}` – execute a proxied call to the API
* **Minimal Web UI** served at `/index.html` for interactive testing.

---

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
| ------------------------ | -------------------------------------------------------------------- |
| `POST /mcp`              | MCP JSON‑RPC 2.0 endpoint (`openapi_url`, `base_url`, `mcp_prompt`). |
| `GET /tools`             | Returns the list of MCP tools generated from the provided Swagger.   |
| `POST /tools/{toolName}` | Executes the specified tool. Body = JSON arguments.                  |
| `GET /index.html`        | Minimalist web UI.                                                   |

---

## 🛠️ Example usage

1. Browse to **[http://localhost:8080/index.html](http://localhost:8080/index.html)**.
2. Use the public PetStore Swagger URL: `https://petstore3.swagger.io/api/v3/openapi.json`.
3. Click **Load Tools**.
4. For any generated tool:

    * Provide a JSON payload.
    * Click **Run** to see the live proxied response.

