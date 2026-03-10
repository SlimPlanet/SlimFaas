"""
Example usage of slimfaas-client.

This script shows how to connect a Python Job to SlimFaas via WebSocket
to receive async-function requests and publish-event events.

Run:
    uv run examples/job_example.py
"""

from __future__ import annotations

import asyncio
import json
import logging

from slimfaas_client import (
    SlimFaasClient, SlimFaasClientConfig,
    SubscribeEventConfig,
    FunctionVisibility, FunctionTrust,
    AsyncRequest, PublishEvent,
)

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)-8s %(message)s")
logger = logging.getLogger(__name__)


# ---------------------------------------------------------------------------
# Handlers
# ---------------------------------------------------------------------------

async def handle_request(req: AsyncRequest) -> int:
    """
    Handle an asynchronous request sent by SlimFaas.

    Return 200 = success (SlimFaas acknowledges the queue entry).
    Return 500 = error (SlimFaas will retry if configured).
    Return 202 = long processing (the client must call send_callback() later).
    """
    logger.info(
        "AsyncRequest: %s %s%s | elementId=%s | body=%s bytes",
        req.method,
        req.path,
        req.query,
        req.element_id,
        len(req.body) if req.body else 0,
    )

    # Example: parse JSON body
    if req.body:
        try:
            data = json.loads(req.body)
            logger.info("Body payload: %s", data)
        except json.JSONDecodeError:
            logger.warning("Body is not JSON")

    # Simulated processing
    await asyncio.sleep(0.1)

    return 200  # Success


async def handle_event(evt: PublishEvent) -> None:
    """Handle a publish/subscribe event."""
    logger.info(
        "PublishEvent: '%s' | %s %s%s | body=%s bytes",
        evt.event_name,
        evt.method,
        evt.path,
        evt.query,
        len(evt.body) if evt.body else 0,
    )


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

async def main() -> None:
    config = SlimFaasClientConfig(
        # Job name (must NOT be the name of an existing Kubernetes Deployment)
        function_name="my-python-job",

        # SlimFaas/SubscribeEvents: listen to these publish-event events
        subscribe_events=[
            SubscribeEventConfig(name="order-created"),
            SubscribeEventConfig(name="order-updated"),
        ],

        # SlimFaas/DefaultVisibility
        default_visibility=FunctionVisibility.PUBLIC,

        # SlimFaas/NumberParallelRequest
        number_parallel_request=5,

        # SlimFaas/DefaultTrust
        default_trust=FunctionTrust.TRUSTED,
    )

    # WebSocket port of SlimFaas (default 5003)
    ws_url = "ws://localhost:5003/ws"

    logger.info("Connecting to SlimFaas at %s with function '%s'…", ws_url, config.function_name)

    async with SlimFaasClient(ws_url, config, reconnect_delay=5.0) as client:
        client.on_async_request(handle_request)
        client.on_publish_event(handle_event)

        logger.info("Client ready. Waiting for messages…")
        await client.run_forever()


if __name__ == "__main__":
    asyncio.run(main())
