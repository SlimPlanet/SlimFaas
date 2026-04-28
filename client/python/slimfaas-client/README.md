<div align="center">
  <a href="https://slimfaas.dev/">
    <img src="https://raw.githubusercontent.com/SlimPlanet/SlimFaas/main/src/SlimFaasSite/public/slimfaas.svg" alt="SlimFaas" width="96" />
  </a>
</div>

# slimfaas-client

Python client to connect Jobs or virtual functions to **SlimFaas** via WebSocket.
Lets any process receive async requests and publish/subscribe events without exposing an HTTP port.

[![PyPI](https://img.shields.io/pypi/v/slimfaas-client.svg)](https://pypi.org/project/slimfaas-client)
[![PyPI Downloads](https://img.shields.io/pypi/dm/slimfaas-client.svg)](https://pypi.org/project/slimfaas-client)
[![Python Versions](https://img.shields.io/pypi/pyversions/slimfaas-client.svg)](https://pypi.org/project/slimfaas-client)
[![GitHub](https://img.shields.io/badge/GitHub-SlimFaas-181717?logo=github)](https://github.com/SlimPlanet/SlimFaas)
[![Website](https://img.shields.io/badge/Website-slimfaas.dev-blue)](https://slimfaas.dev/)

Links:

- PyPI package: <https://pypi.org/project/slimfaas-client>
- GitHub repository: <https://github.com/SlimPlanet/SlimFaas>
- SlimFaas website: <https://slimfaas.dev/>

## Requirements

- Python ≥ 3.10
- [UV](https://docs.astral.sh/uv/) as package manager

## Installation

```bash
uv add slimfaas-client
# or from source
uv pip install -e .
```

## Quick start

```python
import asyncio
from slimfaas_client import (
    SlimFaasClient, SlimFaasClientConfig,
    SubscribeEventConfig, FunctionVisibility,
    AsyncRequest, PublishEvent,
)

async def handle_request(req: AsyncRequest) -> int:
    """Called when SlimFaas sends an async-function request.
    Return an HTTP status code (200 = success, 500 = error, 202 = long processing)."""
    print(f"{req.method} {req.path}{req.query}")
    print(f"Body: {req.body}")
    return 200

async def handle_event(evt: PublishEvent) -> None:
    """Called when SlimFaas publishes a publish-event."""
    print(f"Event '{evt.event_name}': {evt.body}")

async def main():
    config = SlimFaasClientConfig(
        function_name="my-job",
        subscribe_events=[
            SubscribeEventConfig(name="order-created"),
            SubscribeEventConfig(name="order-updated"),
        ],
        default_visibility=FunctionVisibility.PUBLIC,
        number_parallel_request=5,
    )

    async with SlimFaasClient("ws://slimfaas:5003/ws", config) as client:
        client.on_async_request(handle_request)
        client.on_publish_event(handle_event)
        await client.run_forever()

asyncio.run(main())
```

## Full configuration

```python
from slimfaas_client import (
    SlimFaasClientConfig, SubscribeEventConfig, PathVisibilityConfig,
    FunctionVisibility, FunctionTrust,
)

config = SlimFaasClientConfig(
    function_name="my-job",

    # SlimFaas/DependsOn
    depends_on=["other-function"],

    # SlimFaas/SubscribeEvents — each entry may override visibility individually
    subscribe_events=[
        SubscribeEventConfig(name="my-event", visibility=FunctionVisibility.PUBLIC),
        SubscribeEventConfig(name="internal-event"),  # inherits default_visibility
    ],

    # SlimFaas/DefaultVisibility
    default_visibility=FunctionVisibility.PUBLIC,  # or PRIVATE

    # SlimFaas/PathsStartWithVisibility
    paths_start_with_visibility=[
        PathVisibilityConfig(path="/admin", visibility=FunctionVisibility.PRIVATE),
    ],

    # SlimFaas/Configuration
    configuration='{"key": "value"}',

    # SlimFaas/ReplicasStartAsSoonAsOneFunctionRetrieveARequest
    replicas_start_as_soon_as_one_function_retrieve_a_request=True,

    # SlimFaas/NumberParallelRequest
    number_parallel_request=10,

    # SlimFaas/NumberParallelRequestPerPod
    number_parallel_request_per_pod=5,

    # SlimFaas/DefaultTrust
    default_trust=FunctionTrust.TRUSTED,  # or UNTRUSTED
)
```

## Sync streaming (HTTP-over-WebSocket)

```python
from slimfaas_client import SyncRequest

async def handle_sync(req: SyncRequest) -> None:
    body = b'{"status": "ok"}'
    await req.response.start(200, {"Content-Type": ["application/json"]})
    await req.response.write(body)
    await req.response.complete()

client.on_sync_request(handle_sync)
```

## Long-running requests (status 202)

Return `202` to acknowledge the request without completing it yet,
then call `send_callback` when done:

```python
async def handle_long(req: AsyncRequest) -> int:
    asyncio.create_task(process_in_background(req))
    return 202  # "I'll handle it — will call back"

async def process_in_background(req: AsyncRequest) -> None:
    await asyncio.sleep(10)
    await client.send_callback(req.element_id, 200)
```

## Dependency injection

The handlers are plain async functions, so you can close over any dependency
you resolved from your DI framework:

```python
# Example with a database session from SQLAlchemy
from sqlalchemy.ext.asyncio import AsyncSession

async def make_handler(session: AsyncSession):
    async def handle_request(req: AsyncRequest) -> int:
        await session.execute(...)  # use the injected session
        return 200
    return handle_request

client.on_async_request(await make_handler(db_session))
```

## Automatic reconnection

The client reconnects automatically after a disconnection.
Configure the delay between attempts and the keepalive ping interval:

```python
client = SlimFaasClient(
    "ws://...",
    config,
    reconnect_delay=10.0,
    ping_interval=30.0,  # use 0 to disable keepalive pings
)
```

## Important rules

1. **`function_name` must not match an existing Kubernetes Deployment name.**
   SlimFaas will reject the registration with a `SlimFaasRegistrationError`.

2. **All clients sharing the same `function_name` must have the exact same configuration.**
   Mismatches are rejected on connection.

## Development

```bash
uv sync --extra dev
uv run pytest
```
