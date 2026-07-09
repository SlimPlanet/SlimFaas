# Data Sets API

SlimFaas provides a **Redis-like, cluster-consistent key/value store** through the `/data/sets` endpoints.

Use it to store **small values** (cache entries, JSON state, flags, checkpoints) with a robust replication protocol (SlimData + Raft).

- **Max payload size:** 1 MiB per entry
- **TTL unit:** milliseconds (`ttl` query parameter)
- **Atomic counters:** Redis-like `INCR`, `INCRBY`, `INCRBYFLOAT`, `DECR`, `DECRBY`

> Not for large file storage. For large binaries (PDF, ZIP, audio, PPTX, ...), use **Data Files**: https://slimfaas.dev/data-files

---

## How it works (high-level)

```mermaid
flowchart LR
  C[Client] -->|HTTP| API[SlimFaas /data/sets]
  API -->|SetAsync / GetAsync / DeleteAsync| DB[SlimData]
  DB -->|Raft replication| N1[(Node A)]
  DB -->|Raft replication| N2[(Node B)]
  DB -->|Raft replication| N3[(Node C)]
  DB --> TTL[TTL metadata]
  TTL --> EXP[Auto-expire]
```

---

## Endpoints

Base path: `/data/sets`

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/data/sets?id={id?}&ttl={ttl_ms?}` | Create or overwrite a value |
| `POST` | `/data/sets/{id}/incr` | Atomically increment an integer by 1 |
| `POST` | `/data/sets/{id}/incrby?by={long}` | Atomically increment an integer by `by` |
| `POST` | `/data/sets/{id}/incrbyfloat?by={decimal}` | Atomically increment a decimal value by `by` |
| `POST` | `/data/sets/{id}/decr` | Atomically decrement an integer by 1 |
| `POST` | `/data/sets/{id}/decrby?by={long}` | Atomically decrement an integer by `by` |
| `GET` | `/data/sets/{id}` | Read a value |
| `GET` | `/data/sets` | List entries (IDs + expiration) |
| `DELETE` | `/data/sets/{id}` | Delete a value |

### IDs

- IDs are validated server-side (`IdValidator.IsSafeId`).
- Valid IDs contain 1 to 200 characters: letters, digits, `.`, `_`, `-`.
- If `id` is omitted (or empty), SlimFaas generates one (`Guid.NewGuid().ToString("N")`) and returns it.

### TTL (milliseconds)

- `ttl` is optional and expressed in **milliseconds**.
- When provided, the entry auto-expires after the TTL.

Examples:
- `ttl=60000` → 1 minute
- `ttl=600000` → 10 minutes

---

## Create / overwrite

`POST /data/sets?id={id?}&ttl={ttl_ms?}`

- Body is stored as raw bytes.
- Payload larger than 1 MiB returns **413 Payload Too Large**.

Examples:

Store JSON with a fixed id:
```bash
curl -X POST "http://<slimfaas>/data/sets?id=my-usecase.session-123.state" \
  -H "Content-Type: application/json" \
  --data-binary '{"step":"route","chosen":"kb_rag","confidence":0.92}'
```

Store a string:
```bash
curl -X POST "http://<slimfaas>/data/sets?id=my-usecase.session-123.flag" \
  -H "Content-Type: text/plain" \
  --data-binary "ready"
```

Let SlimFaas generate the id:
```bash
ID=$(curl -s -X POST "http://<slimfaas>/data/sets" --data-binary "hello")
echo "created id=$ID"
```

Store with TTL (10 minutes = 600000 ms):
```bash
curl -X POST "http://<slimfaas>/data/sets?id=my-usecase.session-123.state&ttl=600000" \
  --data-binary "temporary"
```

---

## Atomic counters

Counter operations are executed directly inside the SlimData Raft command. This avoids client-side read/modify/write races: concurrent increments are serialized by the Raft log and each request receives the value produced by its own command.

Supported operations:

| Command | HTTP endpoint | Stored value type | Response |
|---|---|---|---|
| `INCR` | `POST /data/sets/{id}/incr` | UTF-8 integer | new integer value |
| `INCRBY` | `POST /data/sets/{id}/incrby?by={long}` | UTF-8 integer | new integer value |
| `INCRBYFLOAT` | `POST /data/sets/{id}/incrbyfloat?by={decimal}` | UTF-8 decimal | new decimal value |
| `DECR` | `POST /data/sets/{id}/decr` | UTF-8 integer | new integer value |
| `DECRBY` | `POST /data/sets/{id}/decrby?by={long}` | UTF-8 integer | new integer value |

Rules:

- Missing or expired keys start from `0`.
- Integer commands require the existing value to be a strict UTF-8 integer.
- `INCRBYFLOAT` requires the existing value to be a strict UTF-8 decimal number.
- Numeric mutations preserve the existing TTL.
- If a key has expired, the old value and TTL are ignored and removed before applying the mutation.
- Invalid numeric values or integer overflows return **409 Conflict** and do not mutate the stored value.
- Responses are `text/plain` and contain the new value.

Examples:

Increment a counter:
```bash
curl -X POST "http://<slimfaas>/data/sets/request-count/incr"
# 1
```

Increment by a custom amount:
```bash
curl -X POST "http://<slimfaas>/data/sets/request-count/incrby?by=10"
# 11
```

Decrement:
```bash
curl -X POST "http://<slimfaas>/data/sets/request-count/decr"
# 10
```

Increment a decimal value:
```bash
curl -X POST "http://<slimfaas>/data/sets/cost-total/incrbyfloat?by=1.25"
# 1.25
```

Read the stored counter value:
```bash
curl -s "http://<slimfaas>/data/sets/request-count"
# 10
```

---

## Read

`GET /data/sets/{id}`

- Returns raw bytes as `application/octet-stream`.
- `404 Not Found` when missing or expired.

Examples:
```bash
curl -L "http://<slimfaas>/data/sets/my-usecase.session-123.state" -o value.bin
```

If you stored JSON:
```bash
curl -s "http://<slimfaas>/data/sets/my-usecase.session-123.state" | jq .
```

---

## List

`GET /data/sets`

Returns a JSON array:
```json
[
  { "id": "abc", "expireAtUtcTicks": -1 },
  { "id": "xyz", "expireAtUtcTicks": 638720123456789012 }
]
```

- `expireAtUtcTicks` is UTC DateTime ticks (100 ns units).
- `-1` means “no expiration”.

---

## Delete

`DELETE /data/sets/{id}`

- Returns **204 No Content**.

Example:
```bash
curl -X DELETE "http://<slimfaas>/data/sets/my-usecase.session-123.state"
```

---

## Visibility & security

`/data/sets` is protected by the same data visibility policy used by other data endpoints (`DataVisibilityEndpointFilter`).

`appsettings.json`:
```json
{
  "Data": {
    "DefaultVisibility": "Private"
  }
}
```

Env override:
- `Data:DefaultVisibility` → `Data__DefaultVisibility`
