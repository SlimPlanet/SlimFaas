"""
slimfaas-client
===============

Python library to connect Jobs or virtual functions to SlimFaas via WebSocket.

Each instance of `SlimFaasClient` connects to SlimFaas and registers itself
as a "virtual replica" of a given function. SlimFaas can then route async
requests and publish/subscribe events to it instead of making HTTP calls.

Quick start::

    import asyncio
    from slimfaas_client import (
        SlimFaasClient, SlimFaasClientConfig,
        SubscribeEventConfig, PathVisibilityConfig,
        FunctionVisibility, FunctionTrust,
        AsyncRequest, PublishEvent,
    )

    async def handle_request(req: AsyncRequest) -> int:
        print(f"Received async request: {req.method} {req.path}")
        # Processing ...
        return 200  # HTTP status code to return

    async def handle_event(evt: PublishEvent) -> None:
        print(f"Received event: {evt.event_name}")

    async def main():
        config = SlimFaasClientConfig(
            function_name="my-job",
            subscribe_events=[
                SubscribeEventConfig(name="fibo-public", visibility=FunctionVisibility.PUBLIC),
                SubscribeEventConfig(name="internal-event"),  # inherits default_visibility
            ],
            default_visibility=FunctionVisibility.PUBLIC,
        )
        async with SlimFaasClient("ws://slimfaas:5003/ws", config) as client:
            client.on_async_request(handle_request)
            client.on_publish_event(handle_event)
            await client.run_forever()

    asyncio.run(main())
"""

from slimfaas_client._client import SlimFaasClient
from slimfaas_client._models import (
    AsyncRequest,
    AsyncCallback,
    BinaryFrame,
    FunctionVisibility,
    FunctionTrust,
    PublishEvent,
    SlimFaasClientConfig,
    SubscribeEventConfig,
    PathVisibilityConfig,
    SyncBodyStream,
    SyncRequest,
    SyncResponse,
    SyncResponseWriter,
)

__all__ = [
    "SlimFaasClient",
    "SlimFaasClientConfig",
    "FunctionVisibility",
    "FunctionTrust",
    "SubscribeEventConfig",
    "PathVisibilityConfig",
    "AsyncRequest",
    "AsyncCallback",
    "BinaryFrame",
    "PublishEvent",
    "SyncBodyStream",
    "SyncRequest",
    "SyncResponse",
    "SyncResponseWriter",
]

