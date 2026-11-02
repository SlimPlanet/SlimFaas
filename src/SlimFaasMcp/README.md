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

## Path and query parameters

Path and query argument values are percent-encoded. A path value always stays inside its segment of the operation's URL: for `GET /pets/{id}/toys`, `{"id":"a b/c?d"}` calls `GET /pets/a%20b%2Fc%3Fd/toys`, and `{"id":"../admin"}` calls `GET /pets/..%2Fadmin/toys`.

Strings are sent without their JSON quotes. Numbers and booleans are sent as written in JSON, so `{"id":true}` calls `GET /pets/true/toys`; earlier versions sent `True` and `False` in paths. A `null` path value leaves its placeholder empty.

When a path segment renders as exactly `.` or `..`, SlimFaasMcp does not call the API and returns a `400` tool result such as `{"error":"Path segment {id} cannot render as a dot segment (. or ..)"}`. URL handling would otherwise resolve such a segment and leave the operation's path. A dot inside a larger segment is sent unchanged: for `/pets/prefix-{id}/toys`, `{"id":".."}` calls `/pets/prefix-../toys`.

## Upgrading existing callers

Earlier versions advertised the sole property's schema as the `body` argument for JSON objects with one property. After upgrading, refresh the discovered tool schemas with `tools/list` and update affected callers from `{"body":"What is SlimFaas?"}` to `{"body":{"question":"What is SlimFaas?"}}`. Update custom `mcp_prompt` schema overrides that describe these object bodies as scalars.

Scalar JSON request bodies and multipart form field mapping retain their existing behavior.
