# Stream demo

The standard native, Compose and Kubernetes demos already subscribe `fibonacci1`
and `fibonacci2` to `orders`. No feature overlays or activation flags are needed.

```bash
# Native: run from the repository root.
dotnet run --project src/SlimFaas -- local up -f ../../slimfaas.local.yaml

# Compose: retain the standard repository-root build context.
docker compose up -d --build slimfaas fibonacci1 fibonacci2 fibonacci3 fibonacci4
```

Wait for `/data/streams/orders/functions` to show both subscriptions before
publishing, because new subscriptions start at the tail. Then use
[streams.http](streams.http) or the [Streams guide](../../docs/streams.md).
Open `#/streams/orders` and `#/live/traffic` to compare retained messages and
independent function progress. Change the HTTP client's base URL for Compose
or Kubernetes. These examples intentionally use a Public Stream.

Publication returns **202** with `Stream`, `MessageId` and `Location` after
durable commit, independently of recipient readiness or ACK. Observe both
consumers' progress in a separate read; ACK does not remove retained history.

For the standalone two-consumer exercise, use
the [Streams Guided Tour](../../docs/guided-tour.md#9-follow-durable-stream-fan-out)
or Bruno's **Tour / 09 Streams**. Failure repair and replay are part of the
[Async chapter](../../docs/guided-tour.md#repair-and-replay-a-dead-letter).
For installation and job-image requirements, follow the
[Compose guide](../../docs/get-started-docker-compose.md) or
[Kubernetes guide](../../docs/get-started-kubernetes.md).
