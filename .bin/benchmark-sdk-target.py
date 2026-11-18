#!/usr/bin/env python3
"""Independent official Python SDK receiver for the C# bounded feature driver."""
import argparse
import asyncio
import hashlib
import importlib.util
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


async def run(options):
    from slimfaas_client import SlimFaasClient, SlimFaasClientConfig, SubscribeEventConfig
    spec = importlib.util.spec_from_file_location("receipt_fixture", ROOT / "tests/SlimFaas.Tests/Local/chaos_function.py")
    fixture = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(fixture)
    os.environ["CHAOS_FUNCTION"] = options.function
    journal = fixture.Journal(options.receipts)
    config = SlimFaasClientConfig(function_name=options.function, number_parallel_request=256, number_parallel_request_per_pod=256,
        subscribe_events=[SubscribeEventConfig(name=options.event)])

    async def record(request, body, kind):
        await record_hash(request, len(body), hashlib.sha256(body).hexdigest(), kind)

    async def record_hash(request, length, digest, kind):
        headers = {key.lower(): value[0] for key, value in request.headers.items() if value}
        await asyncio.to_thread(journal.append, {"event": kind, "id": headers.get("x-chaos-id", ""),
            "bytes": length, "sha256": digest, "status": 200})

    async def consume(request):
        await record(request, request.body or b"", "started")
        await record(request, request.body or b"", "completed")
        return 200

    async def sync(request):
        length = 0
        digest = hashlib.sha256()
        await request.response.start(200)
        while chunk := await request.body.read(65536):
            digest.update(chunk)
            length += len(chunk)
            await request.response.write(chunk)
        await record_hash(request, length, digest.hexdigest(), "started")
        await request.response.complete()
        await record_hash(request, length, digest.hexdigest(), "completed")

    async with SlimFaasClient(options.url, config) as client:
        client.on_async_request(consume)
        client.on_publish_event(consume)
        client.on_sync_request(sync)
        await client.run_forever()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", required=True)
    parser.add_argument("--function", required=True)
    parser.add_argument("--event", default="benchmark-event")
    parser.add_argument("--receipts", type=Path, required=True)
    asyncio.run(run(parser.parse_args()))
