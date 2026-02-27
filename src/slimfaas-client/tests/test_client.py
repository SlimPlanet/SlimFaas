"""
Tests unitaires pour slimfaas-client.
"""

from __future__ import annotations

import asyncio
import json
from unittest.mock import AsyncMock, MagicMock

import pytest

from slimfaas_client._models import (
    AsyncRequest,
    MessageType,
    PublishEvent,
    SlimFaasClientConfig,
)
from slimfaas_client._client import SlimFaasClient, SlimFaasRegistrationError


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def make_config(**kwargs) -> SlimFaasClientConfig:
    defaults = dict(
        function_name="test-job",
        subscribe_events=["my-event"],
    )
    defaults.update(kwargs)
    return SlimFaasClientConfig(**defaults)


def make_envelope(msg_type: int, payload: dict | None, correlation_id: str = "corr-1") -> str:
    return json.dumps({
        "type": msg_type,
        "correlationId": correlation_id,
        "payload": payload,
    })


# ---------------------------------------------------------------------------
# Tests des modèles
# ---------------------------------------------------------------------------

class TestAsyncRequest:
    def test_from_payload_basic(self):
        payload = {
            "elementId": "elem-1",
            "method": "POST",
            "path": "/api/do",
            "query": "?foo=bar",
            "headers": {"content-type": ["application/json"]},
            "body": None,
            "isLastTry": False,
            "tryNumber": 1,
        }
        req = AsyncRequest.from_payload(payload)
        assert req.element_id == "elem-1"
        assert req.method == "POST"
        assert req.path == "/api/do"
        assert req.query == "?foo=bar"
        assert req.body is None
        assert req.is_last_try is False
        assert req.try_number == 1

    def test_from_payload_with_body(self):
        import base64
        body = b"hello world"
        payload = {
            "elementId": "e2",
            "method": "POST",
            "path": "/",
            "query": "",
            "headers": {},
            "body": base64.b64encode(body).decode(),
            "isLastTry": True,
            "tryNumber": 2,
        }
        req = AsyncRequest.from_payload(payload)
        assert req.body == body
        assert req.is_last_try is True
        assert req.try_number == 2


class TestPublishEvent:
    def test_from_payload(self):
        payload = {
            "eventName": "order-created",
            "method": "POST",
            "path": "/events",
            "query": "",
            "headers": {},
            "body": None,
        }
        evt = PublishEvent.from_payload(payload)
        assert evt.event_name == "order-created"
        assert evt.body is None


class TestSlimFaasClientConfig:
    def test_to_register_payload(self):
        config = make_config(
            function_name="my-job",
            subscribe_events=["ev1"],
            default_visibility="Private",
            number_parallel_request=3,
        )
        p = config.to_register_payload()
        assert p["functionName"] == "my-job"
        assert p["configuration"]["subscribeEvents"] == ["ev1"]
        assert p["configuration"]["defaultVisibility"] == "Private"
        assert p["configuration"]["numberParallelRequest"] == 3


# ---------------------------------------------------------------------------
# Tests du client (avec WebSocket mocké)
# ---------------------------------------------------------------------------

class MockWebSocket:
    """Simule un websockets.ClientConnection."""

    def __init__(self, messages: list[str]):
        self._messages = iter(messages)
        self.sent: list[str] = []

    async def send(self, data: str) -> None:
        self.sent.append(data)

    def __aiter__(self):
        return self

    async def __anext__(self):
        try:
            return next(self._messages)
        except StopIteration:
            raise StopAsyncIteration

    async def close(self) -> None:
        pass


class TestSlimFaasClientDispatch:
    """Tests du routage des messages sans connexion réseau réelle."""

    @pytest.mark.asyncio
    async def test_dispatch_async_request_calls_handler(self):
        config = make_config()
        client = SlimFaasClient("ws://fake", config)

        received: list[AsyncRequest] = []

        async def handler(req: AsyncRequest) -> int:
            received.append(req)
            return 200

        client.on_async_request(handler)

        ws = MockWebSocket([])
        payload = {
            "elementId": "e1",
            "method": "POST",
            "path": "/test",
            "query": "",
            "headers": {},
            "body": None,
            "isLastTry": False,
            "tryNumber": 1,
        }
        req = AsyncRequest.from_payload(payload)
        await client._dispatch_async_request(ws, req)  # type: ignore
        await asyncio.sleep(0)  # laisse les tâches tourner

        assert len(received) == 1
        assert received[0].element_id == "e1"
        # Un callback doit avoir été envoyé
        assert len(ws.sent) == 1
        sent = json.loads(ws.sent[0])
        assert sent["type"] == MessageType.ASYNC_CALLBACK
        assert sent["payload"]["statusCode"] == 200

    @pytest.mark.asyncio
    async def test_dispatch_async_request_no_handler_sends_500(self):
        config = make_config()
        client = SlimFaasClient("ws://fake", config)

        ws = MockWebSocket([])
        req = AsyncRequest(
            element_id="e2", method="GET", path="/", query="",
            headers={}, body=None, is_last_try=True, try_number=1,
        )
        await client._dispatch_async_request(ws, req)  # type: ignore
        await asyncio.sleep(0)

        assert len(ws.sent) == 1
        sent = json.loads(ws.sent[0])
        assert sent["payload"]["statusCode"] == 500

    @pytest.mark.asyncio
    async def test_dispatch_publish_event_calls_handler(self):
        config = make_config()
        client = SlimFaasClient("ws://fake", config)

        received: list[PublishEvent] = []

        async def handler(evt: PublishEvent) -> None:
            received.append(evt)

        client.on_publish_event(handler)

        evt = PublishEvent(
            event_name="order-created",
            method="POST",
            path="/events",
            query="",
            headers={},
            body=None,
        )
        await client._dispatch_publish_event(evt)  # type: ignore
        await asyncio.sleep(0)

        assert len(received) == 1
        assert received[0].event_name == "order-created"

    @pytest.mark.asyncio
    async def test_handler_exception_sends_500(self):
        config = make_config()
        client = SlimFaasClient("ws://fake", config)

        async def bad_handler(req: AsyncRequest) -> int:
            raise ValueError("boom")

        client.on_async_request(bad_handler)

        ws = MockWebSocket([])
        req = AsyncRequest(
            element_id="e3", method="POST", path="/", query="",
            headers={}, body=None, is_last_try=False, try_number=1,
        )
        await client._dispatch_async_request(ws, req)  # type: ignore
        await asyncio.sleep(0)

        sent = json.loads(ws.sent[0])
        assert sent["payload"]["statusCode"] == 500

    @pytest.mark.asyncio
    async def test_dispatch_async_202_does_not_auto_callback(self):
        """Si le handler retourne 202, aucun callback automatique ne doit être envoyé."""
        config = make_config()
        client = SlimFaasClient("ws://fake", config)

        async def long_handler(req: AsyncRequest) -> int:
            return 202

        client.on_async_request(long_handler)

        ws = MockWebSocket([])
        req = AsyncRequest(
            element_id="e4", method="POST", path="/", query="",
            headers={}, body=None, is_last_try=False, try_number=1,
        )
        await client._dispatch_async_request(ws, req)  # type: ignore
        await asyncio.sleep(0)

        # Aucun callback automatique
        assert len(ws.sent) == 0

