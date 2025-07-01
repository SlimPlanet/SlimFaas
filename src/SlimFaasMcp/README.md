# MCP Swagger Proxy for SlimFaas

This project is a **runtime MCP proxy** that dynamically generates SlimFaas-compatible MCP Tools from any remote Swagger (OpenAPI v2/v3) document.
Each endpoint from the source API is exposed as a "tool" with on-the-fly YAML manifest generation and real-time proxy execution.

---

## 🚀 Features

- **Dynamic download** of any remote Swagger (OpenAPI v2 or v3) document at runtime
- **Parses and exposes** all endpoints (GET, POST, PUT, DELETE, etc.)
- **No static code generation**: everything is live, built from the Swagger URL
- **Key Endpoints**:
    - `JSON RPC POST /mcp?openapi_url=<swagger_url>&base_url=<base_url>`: Model context Protocol Endpoint
    - `GET /tools?url=<swagger_url>`: List all available MCP Tools
    - `POST /tools/{toolName}?url=<swagger_url>`: Execute a proxied call to the actual API endpoint
    - `GET /manifest.yaml?url=<swagger_url>`: Generate a SlimFaas-compatible MCP YAML manifest
- **Minimalist Web UI** available at `/index.html` for interactive testing

---

## 📦 Installation & Quick Start

1. **Clone this repository** or copy the provided code

3. **Run the API:**

    ```sh
    dotnet run
    ```

   The API will listen on `http://localhost:5000` by default.

---

## 🖥️ Web UI for Tool Testing

Go to:
http://localhost:5000/index.html
- Enter a Swagger URL (see the list below)
- Load, explore, and call MCP Tools directly from your browser

---

## 🌐 Public Swagger/OpenAPI URLs for Testing

Here are some public Swagger/OpenAPI URLs you can use (**no authentication required**, tested June 2025):

| API Name                   | OpenAPI / Swagger URL                                                                   | Notes                                   |
|----------------------------|-----------------------------------------------------------------------------------------|-----------------------------------------|
| Petstore (OpenAPI v3)      | https://petstore3.swagger.io/api/v3/openapi.json                                        | Classic, always up                      |
| Swagger Petstore (v2)      | https://petstore.swagger.io/v2/swagger.json                                             | Classic, always up                      |

> ⚠️ Some APIs may require a key for certain endpoints, but most GETs work without one.

---

## 📖 Main API Endpoints

- `JSON RPC POST /mcp?openapi_url=<swagger_url>&base_url=<base_url>`
    - Model context Protocol Endpoint

- `GET /tools?openapi_url=<swagger_url>&base_url=<base_url>`
    - List all dynamically generated MCP tools for the provided Swagger
- `POST /tools/{toolName}?openapi_url=<swagger_url>&base_url=<base_url>`
    - Execute a proxied call to the specified tool, sending parameters as JSON body
- `GET /manifest.yaml?openapi_url=<swagger_url>&base_url=<base_url>`
    - Generate a valid SlimFaas MCP YAML manifest for all exposed tools
- `GET /index.html`
    - Access the minimalist web UI for testing

---

## 🛠️ Example Usage

1. Open `http://localhost:5000/index.html` in your browser
2. Paste a public Swagger URL (for example: `https://petstore3.swagger.io/api/v3/openapi.json`)
3. Click "Load Tools"
4. Use the UI to:
    - List all generated MCP Tools
    - Test any tool by providing a JSON input
    - View live proxied responses

---

## 🚧 Known Limitations

- OpenAPI v2/v3 and YAML/JSON support is basic; for complex specs, adapt as needed
- No authentication, advanced headers handled by default (contributions welcome)

---

## 🤝 Contributions

Pull requests welcome for:
- Better OpenAPI v2/v3 support
- Auth/header management
- UI/UX improvements
- More manifest options, etc.

---

**Enjoy & happy hacking!**
