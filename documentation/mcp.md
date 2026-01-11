# SlimFaas MCP [![Docker SlimFaas](https://img.shields.io/docker/pulls/axaguildev/slimfaas-mcp.svg?label=docker+pull+slimfaas-mcp)](https://hub.docker.com/r/axaguildev/slimfaas-mcp/builds) [![Docker Image Size](https://img.shields.io/docker/image-size/axaguildev/slimfaas-mcp?label=image+size+slimfaas-mcp)](https://hub.docker.com/r/axaguildev/slimfaas/builds) [![Docker Image Version](https://img.shields.io/docker/v/axaguildev/slimfaas-mcp?sort=semver&label=latest+version+slimfaas-mcp)](https://hub.docker.com/r/axaguildev/slimfaas-mcp/builds) [![Artifact Hub](https://img.shields.io/endpoint?url=https://artifacthub.io/badge/repository/slimfaas-mcp)](https://artifacthub.io/packages/search?repo=slimfaas-mcp)
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

        * Query – `openapi_url`, `base_url`, `mcp_prompt`, `oauth`, `structured_content`, `tool_prefix`
    * `GET  /tools` – list all generated MCP tools
      * Query – `openapi_url`, `base_url`, `mcp_prompt`, `tool_prefix`
    * `POST /tools/{toolName}` – execute a proxied call to the API
      * Query – `openapi_url`, `base_url`, `mcp_prompt`, `oauth`, `structured_content`, `tool_prefix`
    * `GET /{oauth}/.well-known/oauth-protected-resource` – for client dynamic authorization server discovery (RFC 9728)
* **Minimal Web UI** served at `/index.html` for interactive testing.

---

## 📖 Query Parameters Cheat Sheet

> Applies to both `POST /mcp` (JSON-RPC endpoint) and the UI helper endpoint `POST /tools/{toolName}`.

| Parameter            | Required | Type / Format                                                      | Purpose                                                                                                | Notes                                                                                                                                                                                     | Example                                            |
|----------------------| -------- |--------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------------------------|
| `openapi_url`        | ✅ Yes    | Absolute URL                                                       | Location of the OpenAPI (JSON) document to consume.                                                    | Used to discover endpoints (tools).                                                                                                                                                       | `https://petstore3.swagger.io/api/v3/openapi.json` |
| `base_url`           | ✅ Yes | Absolute URL                                                       | Overrides the base URL used for actual API calls.                                                      | Useful if the OpenAPI doc is hosted separately from the API origin.                                                                                                                       | `https://petstore3.swagger.io/api/v3`              |
| `mcp_prompt`         | Optional | **Base64** of a JSON object (or YAML converted to JSON)            | Filters/overrides tools (`activeTools`, descriptions, input/output schemas).                           | Must be **UTF-8 base64**. Server applies overrides **before** exposing `tools/list`.                                                                                                      | See base64 example below                           |
| `oauth`              | Optional | **Base64** of an OAuth Protected Resource Metadata JSON (RFC 9728) | Lets the server advertise protected resource metadata.                                                 | If present **and** there is **no** `Authorization` header, server returns `401` with a `WWW-Authenticate: Bearer resource_metadata=".../.well-known/oauth-protected-resource"` challenge. | See base64 example below                           |
| `structured_content` | Optional | Boolean (`true`/`false`)                                           | Enables inclusion of **`structuredContent`** (JSON object) in responses.                               | When `true` and the upstream response is JSON, the server returns **both** a human-readable `content[]` block (with a `text` item) **and** a machine-readable `structuredContent` object. | `true`                                             |
| `tool_prefix`        | Optional | string                                                             | Prefix all tool name with **`value_`**                                                                 | Useful to prevent mismatch between tools name on differents services                                                                                                                      | `?tool_prefix=youhou`                              |
| `cache_expiration`         | Optional | unsigned short int                                                 | Cache time for the OpenAPI document, in minutes (set to 0 to disable caching). Defaults to 10 minutes. | Avoid multiple requests to your OpenAPI document                                                                                                                                          | `?expiration=10`                                   |

---

### Ready-to-use examples

#### 1) `oauth` (base64)

**Raw JSON:**

```
{
    "resource":"https://api.example.com/v1/",
    "authorization_servers":["https://auth.example.com"],
    "scopes_supported":["read:data","write:data"]
}
```

**Base64 (UTF-8):**

```
eyJyZXNvdXJjZSI6Imh0dHBzOi8vYXBpLmV4YW1wbGUuY29tL3YxLyIsImF1dGhvcml6YXRpb25fc2VydmVycyI6WyJodHRwczovL2F1dGguZXhhbXBsZS5jb20iXSwic2NvcGVzX3N1cHBvcnRlZCI6WyJyZWFkOmRhdGEiLCJ3cml0ZTpkYXRhIl19
```

> ⚠️ Always **URL-encode** base64 when putting it in a query string (escape `+`, `/`, `=`).

---

#### 2) `mcp_prompt` (base64)

**Raw JSON — example override:**

```
{
    "activeTools":["getPets","post_pet_petId_uploadImage"],
    "tools":[
        {
            "name":"post_pet_petId_uploadImage",
            "description":"Uploads an image",
            "inputSchema":{
                "type":"object",
                "properties":{
                    "petId":{"type":"integer"},
                    "body":{"type":"string","format":"binary"}},
                "required":["petId","body"]
            }
        }
    ]
}
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
  &tool_prefix=youhou
  &cache_expiration=10
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
2. Enter a Swagger URL
 - e.g. `https://petstore3.swagger.io/api/v3/openapi.json`
 - e.g. `https://developer.atlassian.com/cloud/jira/platform/swagger-v3.v3.json`
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


### Configure CORS via environment variables

SlimFaas MCP supports CORS configuration through environment variables.
Each variable overrides the default values from `appsettings.json`.

| Environment Variable | Type     | Example Values                                                                                    |
| -------------------- | -------- | ------------------------------------------------------------------------------------------------- |
| `CORS_ORIGINS`       | CSV list | `*` — allow any origin<br>`https://*.axa.com,http://localhost:3000` — allow specific origins      |
| `CORS_METHODS`       | CSV list | `*` — allow all methods<br>`GET,POST,OPTIONS` — allow only these methods                          |
| `CORS_HEADERS`       | CSV list | `*` — allow all headers<br>`Authorization,Content-Type,Dpop` — allow only these headers           |
| `CORS_EXPOSE`        | CSV list | Headers exposed to the browser, e.g. `WWW-Authenticate,Content-Disposition`                       |
| `CORS_CREDENTIALS`   | boolean  | `true` or `false` — whether to allow credentialed requests (cookies, Authorization headers, etc.) |
| `CORS_MAXAGEMINUTES` | integer  | How long preflight responses can be cached (in minutes), e.g. `60`                                |



#### ✅ Example (development)
```bash
CORS_ORIGINS=*
CORS_METHODS=*
CORS_HEADERS=*
CORS_CREDENTIALS=false
CORS_MAXAGEMINUTES=10
```bash

#### ✅ Example (production)
```bash
CORS_ORIGINS=https://*.axa.com,https://tools.axa.com
CORS_METHODS=GET,POST,OPTIONS
CORS_HEADERS=Authorization,Content-Type,Dpop
CORS_EXPOSE=WWW-Authenticate,Content-Disposition
CORS_CREDENTIALS=true
CORS_MAXAGEMINUTES=120
```

**Wildcards** (`*`, `*.domain.com`, `localhost:*`) **are supported** in origins.
Empty or missing values fallback to the default configuration in `appsettings.json`.


### Environment variables

You can log HTTP requests made by SlimFaas MCP by using :
```bash
# Windows
set Logging__LogLevel__SlimFaasMcp=Debug
# Linux/Mac
Logging__LogLevel__SlimFaasMcp=Debug
```

---

### Configure OpenTelemetry

SlimFaas MCP supports OpenTelemetry instrumentation for distributed tracing, metrics, and logging.
Configuration can be provided through `appsettings.json` or environment variables.

| Configuration Key                    | Type          | Description                                                                                          |
| ------------------------------------ | ------------- | ---------------------------------------------------------------------------------------------------- |
| `OpenTelemetry__Enable`              | boolean       | Enable or disable OpenTelemetry instrumentation. Set to `false` to completely disable telemetry.    |
| `OpenTelemetry__ServiceName`         | string        | Name of the service for telemetry data. If not specified, falls back to `OTEL_SERVICE_NAME` environment variable (default behavior). |
| `OpenTelemetry__Endpoint`            | string        | OTLP endpoint URL for exporting telemetry data. If not specified, falls back to `OTEL_EXPORTER_OTLP_ENDPOINT` environment variable (default behavior). |
| `OpenTelemetry__EnableConsoleExporter` | boolean     | Enable console exporter for debugging purposes. Defaults to `false`.                                 |
| `OpenTelemetry__ExcludedUrls`        | string array  | List of URL path prefixes to exclude from tracing. Defaults to `["/health", "/metrics"]`.           |

**Configuration Priority (default behavior):**
1. Configuration values from `appsettings.json` (highest priority)
2. Environment variables `OTEL_SERVICE_NAME` and `OTEL_EXPORTER_OTLP_ENDPOINT` (fallback if configuration values are not specified)
3. If `Enable` is `true` and no `Endpoint` is found in either configuration or environment variables, the OpenTelemetry default value will be used.

#### ✅ Example (appsettings.json)
```json
{
  "OpenTelemetry": {
    "Enable": true,
    "ServiceName": "SlimFaasMcp",
    "Endpoint": "http://otel-collector:4317",
    "EnableConsoleExporter": false,
    "ExcludedUrls": ["/health", "/metrics", "/swagger"]
  }
}
```

#### ✅ Example (using environment variables only - default fallback behavior)
```bash
OpenTelemetry__Enable=true
OTEL_SERVICE_NAME=SlimFaasMcp-Production
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
OpenTelemetry__ExcludedUrls__0=/health
OpenTelemetry__ExcludedUrls__1=/metrics
OpenTelemetry__ExcludedUrls__2=/swagger
```

#### ✅ Example (Docker with configuration values)
```bash
docker run --rm -p 8080:8080 \
  -e ASPNETCORE_URLS="http://*:8080" \
  -e OpenTelemetry__Enable="true" \
  -e OpenTelemetry__ServiceName="SlimFaasMcp" \
  -e OpenTelemetry__Endpoint="http://otel-collector:4317" \
  -e OpenTelemetry__ExcludedUrls__0="/health" \
  -e OpenTelemetry__ExcludedUrls__1="/metrics" \
  axaguildev/slimfaas-mcp:latest
```

#### ✅ Example (Docker using environment variables fallback - default behavior)
```bash
docker run --rm -p 8080:8080 \
  -e ASPNETCORE_URLS="http://*:8080" \
  -e OpenTelemetry__Enable="true" \
  -e OTEL_SERVICE_NAME="SlimFaasMcp" \
  -e OTEL_EXPORTER_OTLP_ENDPOINT="http://otel-collector:4317" \
  -e OpenTelemetry__ExcludedUrls__0="/health" \
  -e OpenTelemetry__ExcludedUrls__1="/metrics" \
  axaguildev/slimfaas-mcp:latest
```

**Notes:**
- When `Enable` is `false`, OpenTelemetry instrumentation is completely disabled to avoid unnecessary overhead
- The `ServiceName` is optional; if not specified in configuration, it automatically falls back to the `OTEL_SERVICE_NAME` environment variable (this is the default OpenTelemetry behavior)
- The `Endpoint` automatically falls back to `OTEL_EXPORTER_OTLP_ENDPOINT` if not specified in configuration (this is the default OpenTelemetry behavior)
- **URL exclusion**: URLs specified in `ExcludedUrls` are filtered from tracing based on **case-insensitive path prefix matching**. For example, `/health` will exclude `/health`, `/health/live`, `/health/ready`, etc.
- Empty or missing `ExcludedUrls` configuration will use the default values `["/health", "/metrics"]`
- URL filtering only applies to **traces**; metrics and logs are not affected

The instrumentation includes:
- **Traces**: ASP.NET Core requests, HTTP client calls
- **Metrics**: ASP.NET Core metrics, HTTP client metrics
- **Logs**: Application logs with OpenTelemetry integration

------------------------------------------------------------------------

## 🔐 MCP `_meta` → HTTP Header Mapping

SlimFaas MCP allows you to **dynamically map values sent in the MCP
`_meta` object to outgoing HTTP headers**.\
This is especially useful when using MCP clients such as **Spring AI**,
which can transmit authentication tokens via `_meta` instead of standard
HTTP headers.

This feature makes it possible to: - Inject `Authorization` headers from
`_meta` - Forward session IDs or correlation IDs - Keep your MCP clients
fully portable and stateless

------------------------------------------------------------------------

### ✅ Configuration (`appsettings.json`)

You can define the mapping using the `McpMetaHeaderMapping` section:

``` json
{
  "McpMetaHeaderMapping": {
    "authToken": "Authorization",
    "xSessionId": "X-Session-Id"
  }
}
```

`_meta` Key    Injected HTTP Header
  -------------- ----------------------
`authToken`    `Authorization`
`xSessionId`   `X-Session-Id`

------------------------------------------------------------------------

### ✅ MCP Request Example

``` json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": {
    "name": "get_dashboard_policies",
    "arguments": {},
    "_meta": {
      "authToken": "eyJhbGciOiJSUzI1NiIsInR5cCI...",
      "xSessionId": "abc-123"
    }
  }
}
```

This will automatically generate the following outgoing headers:

    Authorization: Bearer eyJhbGciOiJSUzI1NiIsInR5cCI...
    X-Session-Id: abc-123

> ✅ If the mapped header is `Authorization`, SlimFaas MCP
> **automatically prepends `Bearer`** if missing.

------------------------------------------------------------------------

### ✅ OAuth Challenge Compatibility

If you use the `oauth` query parameter (RFC 9728):

-   SlimFaas MCP checks for `Authorization` **both** in:
    -   incoming HTTP headers
    -   mapped `_meta` values
-   If no token is found, a `401` challenge is returned
-   If `_meta.authToken` is mapped to `Authorization`, the challenge is
    **automatically bypassed**

------------------------------------------------------------------------

### ✅ Compatibility

-   ✅ Spring AI MCP Client
-   ✅ Custom MCP Clients
-   ✅ Browser-based MCP Calls
-   ✅ Secure OAuth / DPoP / PKCE / mTLS setups

------------------------------------------------------------------------

This feature ensures **clean separation between MCP payload transport
and HTTP security**, while remaining fully MCP-compliant and
framework-agnostic.
