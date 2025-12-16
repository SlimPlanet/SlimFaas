# ClusterFileDemoProdish

Minimal API demo: binary file replication across a DotNext cluster (Raft + messaging over HTTP).

## Endpoints

- `POST /data/file?key=element_id&ttl=8908980890`  
  Body: raw bytes (`Content-Type` preserved).  
  If `key` is missing, a GUID is generated.  
  Returns: `element_id` (string).

- `GET /data/file/{element_id}`  
  Returns: bytes with the original `Content-Type` and `Content-Disposition` (if provided).

- `DELETE /data/file/{element_id}`

## “production-ish” strategy

- **Writes (POST/DELETE)**: leader-only (followers respond `307` to the leader).
- **Replication**: leader **broadcasts** metadata + bytes (best effort).
- **Self-heal**:
  - If a node receives `GET` for a file it doesn’t have, it **pulls** it from the leader (or another node).
  - On startup (and every minute), a worker requests a **meta dump** from the leader and pulls any missing files.

## Run 3 nodes locally

> Use explicit loopback IPs (not `localhost`) for DotNext clustering.

### Terminal 1
```bash
dotnet run --project src/ClusterFileDemoProdish.csproj --   --urls http://127.0.0.1:3262   --cluster:port 3262   --fileStorage:rootPath data/node1
```

### Terminal 2
```bash
dotnet run --project src/ClusterFileDemoProdish.csproj --   --urls http://127.0.0.1:3263   --cluster:port 3263   --fileStorage:rootPath data/node2
```

### Terminal 3
```bash
dotnet run --project src/ClusterFileDemoProdish.csproj --   --urls http://127.0.0.1:3264   --cluster:port 3264   --fileStorage:rootPath data/node3
```

## Quick test

Upload to node 1:

```bash
curl -X POST "http://127.0.0.1:3262/data/file?ttl=60000"   -H "Content-Type: application/pdf"   -H "Content-Disposition: attachment; filename=\"demo.pdf\""   --data-binary @demo.pdf
```

Fetch from node 2 (it may pull if it missed the broadcast):

```bash
curl -v "http://127.0.0.1:3263/data/file/<ID_FROM_POST>" -o out.pdf
```

Delete (always send to any node, it will redirect to leader if needed):

```bash
curl -v -X DELETE "http://127.0.0.1:3264/data/file/<ID_FROM_POST>"
```
