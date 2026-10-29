# SlimFaasMcp

SlimFaasMcp exposes OpenAPI operations as MCP tools and forwards tool calls to the HTTP API.

## JSON request bodies

An OpenAPI `application/json` request body is represented by the tool's `body` argument. The body's object structure is retained, whether its schema is defined inline or through a `$ref`. An object with one property remains an object.

For example, a `POST /ask` operation with this request schema:

```json
{
  "type": "object",
  "properties": {
    "question": { "type": "string" }
  },
  "required": ["question"]
}
```

accepts these MCP tool arguments:

```json
{"body":{"question":"What is SlimFaas?"}}
```

### Call the tool

The example assumes your API listens on `http://127.0.0.1:8000`, exposes `POST /ask` with the schema above, and serves its OpenAPI document at `/openapi.json`. SlimFaasMcp must be able to reach that API.

With .NET 10 and Node.js 24 or later installed, start SlimFaasMcp from the repository root:

```bash
dotnet run --project src/SlimFaasMcp --no-launch-profile -- --urls http://127.0.0.1:5269
```

In another terminal, call the generated `post_ask` tool through MCP JSON-RPC:

```bash
curl -fsS \
  'http://127.0.0.1:5269/mcp?openapi_url=http%3A%2F%2F127.0.0.1%3A8000%2Fopenapi.json&base_url=http%3A%2F%2F127.0.0.1%3A8000' \
  -H 'Content-Type: application/json' \
  --data '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"post_ask","arguments":{"body":{"question":"What is SlimFaas?"}}}}'
```

The upstream API receives this JSON body, without the MCP `body` wrapper:

```json
{"question":"What is SlimFaas?"}
```

## Upgrading existing callers

Earlier versions advertised the sole property's schema as the `body` argument for JSON objects with one property. After upgrading, refresh the discovered tool schemas with `tools/list` and update affected callers from `{"body":"What is SlimFaas?"}` to `{"body":{"question":"What is SlimFaas?"}}`. Update custom `mcp_prompt` schema overrides that describe these object bodies as scalars.

Scalar JSON request bodies and multipart form field mapping retain their existing behavior.
