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

Path and query parameters are separate tool arguments next to `body`. For `PUT /pets/{id}`, the arguments `{"id":42,"body":{"name":"Milo"}}` call `PUT /pets/42` with the body `{"name":"Milo"}`. A Swagger 2.0 `in: body` parameter keeps its own name as the argument, such as `pet`, and its value is sent as the body in the same way.

## Upgrading existing callers

Earlier versions advertised the sole property's schema as the `body` argument for JSON objects with one property. After upgrading, refresh the discovered tool schemas with `tools/list` and update affected callers from `{"body":"What is SlimFaas?"}` to `{"body":{"question":"What is SlimFaas?"}}`. Update custom `mcp_prompt` schema overrides that describe these object bodies as scalars.

Scalar JSON request bodies and multipart form field mapping retain their existing behavior.

## Header parameters

An OpenAPI `in: header` parameter is a tool argument named like the header, and its value is sent as that request header. For `GET /pets` with an `X-Tenant-Id` header parameter, the arguments `{"X-Tenant-Id":"acme"}` send `X-Tenant-Id: acme`. Values follow the OpenAPI `simple` style for headers: strings are sent as-is, numbers and booleans as written (`10`, `true`), arrays as comma-separated values (`[1,2]` sends `1,2`), and objects as key and value pairs (`{"role":"admin","id":5}` sends `role,admin,id,5`, or `role=admin,id=5` when the parameter sets `explode: true`). A parameter described with `content: application/json` instead of `schema` is sent as compact JSON. A `null` argument sends no header.

- As the OpenAPI specification requires, header parameters named `Accept`, `Content-Type` or `Authorization` are ignored.
- A header that SlimFaasMcp already forwards, such as `Authorization`, `Dpop` or a header mapped from `_meta` with `McpMetaHeaderMapping`, takes precedence over a tool argument with the same name.
- A value containing CR, LF or NUL characters is rejected with an error result, and the API is not called.
- A content header such as `Content-Language` needs a request body. On a request without one, such as a `GET`, the argument is rejected with an error result, and the API is not called.
