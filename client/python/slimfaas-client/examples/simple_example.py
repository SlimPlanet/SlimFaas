"""
Ultra-simple slimfaas-client example in Python.

Connects to SlimFaas via WebSocket, subscribes to "my-event"
and prints every async request / event / sync request received.

Run:
    uv run examples/simple_example.py
"""

import asyncio
from slimfaas_client import (
    SlimFaasClient,
    SlimFaasClientConfig,
    SubscribeEventConfig,
    AsyncRequest,
    PublishEvent,
    SyncRequest,
)


async def handle_async(req: AsyncRequest) -> int:
    print(f"[Async] {req.method} {req.path} — {len(req.body) if req.body else 0} bytes")
    return 200


async def handle_event(evt: PublishEvent) -> None:
    print(f"[Event] {evt.event_name} — {len(evt.body) if evt.body else 0} bytes")


async def handle_sync(req: SyncRequest) -> None:
    body = b'{"status": "ok"}'
    await req.response.start(200, {"Content-Type": ["application/json"]})
    await req.response.write(body)
    await req.response.complete()


async def main() -> None:
    config = SlimFaasClientConfig(
        function_name="simple-python",
        subscribe_events=[SubscribeEventConfig(name="my-event")],
    )

    async with SlimFaasClient("ws://localhost:5003/ws", config) as client:
        client.on_async_request(handle_async)
        client.on_publish_event(handle_event)
        client.on_sync_request(handle_sync)

        print("Listening… (Ctrl+C to stop)")
        await client.run_forever()


if __name__ == "__main__":
    asyncio.run(main())
