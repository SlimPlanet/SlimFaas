# Stream demo overlays

These are opt-in additions to the standard demo, not standalone deployments.

```bash
# Native: run from the repository root.
dotnet run --project src/SlimFaas -- local up -f ../../slimfaas.local.yaml -f ../../slimfaas.local.streams.yaml

# Compose: retain the standard repository-root build context.
docker compose -f docker-compose.yml -f demo/streams/docker-compose.streams.yml up -d

# Kubernetes: after installing the standard demo in slimfaas-demo.
kubectl -n slimfaas-demo patch deployment fibonacci1 --type merge --patch-file demo/streams/deployment-patch.yml
kubectl -n slimfaas-demo patch deployment fibonacci2 --type merge --patch-file demo/streams/deployment-patch.yml
```

Wait for `/data/streams/orders/functions` to show both subscriptions before
publishing, because new subscriptions start at the tail. Then use
[streams.http](streams.http) or the [Streams guide](../../docs/streams.md).
Open `#/streams/orders` and `#/live/traffic` to compare retained messages and
independent function progress. Change the HTTP client's base URL for Compose
or Kubernetes. These examples intentionally use a Public Stream.
