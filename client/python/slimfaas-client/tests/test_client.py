"""
Tests unitaires pour slimfaas-client.
"""

from __future__ import annotations

import asyncio
import base64
import gc
import hashlib
import json
import weakref
from unittest.mock import AsyncMock, MagicMock

import pytest
from websockets.asyncio.server import serve
from websockets.exceptions import ConnectionClosed

from slimfaas_client._models import (
    AsyncRequest,
    BinaryFrame,
    StreamMessage,
    SubscribeStreamConfig,
    FunctionVisibility,
    FunctionTrust,
    MessageType,
    PublishEvent,
    SlimFaasClientConfig,
    SubscribeEventConfig,
    SyncRequest,
    PathVisibilityConfig,
)
from slimfaas_client._client import SlimFaasClient, SlimFaasRegistrationError


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def make_config(**kwargs) -> SlimFaasClientConfig:
    defaults = dict(
        function_name="test-job",
        subscribe_events=[SubscribeEventConfig(name="my-event")],
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
    @pytest.mark.asyncio
    @pytest.mark.parametrize("size,pattern", [(2_000_000, bytes(range(256))), (2_097_152, bytes(range(256))),
                                              (4_194_304, bytes(range(256))), (2_097_152, b"\xfb\xef\xbe")])
    async def test_real_connection_delivers_large_async_envelopes(self, size, pattern):
        body = (pattern * ((size + len(pattern) - 1) // len(pattern)))[:size]
        received = []
        async def server(ws):
            await ws.recv()
            await ws.send(make_envelope(MessageType.REGISTER_RESPONSE, {"success": True, "connectionId": "large"}))
            envelope = make_envelope(MessageType.ASYNC_REQUEST, {
                "elementId": "large", "method": "POST", "path": "/work", "tryNumber": 0,
                "body": base64.b64encode(body).decode(),
            }).replace("+", "\\u002B")
            await ws.send(envelope)
            try:
                callback = json.loads(await ws.recv())
                assert callback["payload"]["statusCode"] == 200
            except ConnectionClosed:
                pass  # The client-side await still fails on an oversized envelope.
        async with serve(server, "127.0.0.1", 0) as listener:
            port = listener.sockets[0].getsockname()[1]
            client = SlimFaasClient(f"ws://127.0.0.1:{port}", make_config())
            async def handle(req):
                received.append((len(req.body), hashlib.sha256(req.body).digest(), req.try_number))
                return 200
            client.on_async_request(handle)
            await asyncio.wait_for(client._connect_and_loop(), timeout=15)
        assert received == [(size, hashlib.sha256(body).digest(), 0)]

    @pytest.mark.asyncio
    async def test_configured_message_limit_still_closes_oversized_envelopes(self):
        close_codes = []
        async def server(ws):
            await ws.recv()
            await ws.send(make_envelope(MessageType.REGISTER_RESPONSE, {"success": True, "connectionId": "bounded"}))
            await ws.send("x" * 2048)
            await ws.wait_closed()
            close_codes.append(ws.close_code)
        async with serve(server, "127.0.0.1", 0) as listener:
            port = listener.sockets[0].getsockname()[1]
            client = SlimFaasClient(f"ws://127.0.0.1:{port}", make_config(), max_message_size=1024)
            with pytest.raises(ConnectionClosed, match="1009"):
                await asyncio.wait_for(client._connect_and_loop(), timeout=15)
        assert close_codes == [1009]

    @pytest.mark.parametrize("option", ["max_message_size", "max_queue"])
    @pytest.mark.parametrize("value", [0, -1, None, True])
    def test_receive_limits_cannot_be_disabled(self, option, value):
        with pytest.raises(ValueError, match=option):
            SlimFaasClient("ws://fake", make_config(), **{option: value})

    @pytest.mark.asyncio
    @pytest.mark.parametrize("stream", [False, True])
    @pytest.mark.parametrize("failure", [False, True])
    async def test_pending_dispatch_is_retained_until_handler_and_callback_complete(self, stream, failure):
        client = SlimFaasClient("ws://fake", make_config())
        ws = MockWebSocket([])
        started = asyncio.Event()
        waiting = None

        async def handler(request):
            nonlocal waiting
            future = asyncio.get_running_loop().create_future()
            waiting = weakref.ref(future)
            started.set()
            await future
            if failure:
                raise RuntimeError("Application handler failed")
            return 200

        if stream:
            client.on_stream_message(handler)
        else:
            client.on_async_request(handler)
        payload = {"elementId": "pending", "method": "POST", "path": "/work"}
        if stream:
            payload.update(streamName="orders", messageId="generation_1")
        await client._handle_message(ws, make_envelope(MessageType.ASYNC_REQUEST, payload))
        await asyncio.wait_for(started.wait(), timeout=5)
        gc.collect()
        assert waiting() is not None
        assert len(client._dispatch_tasks) == 1
        assert ws.sent == []
        task = next(iter(client._dispatch_tasks))
        waiting().set_result(None)
        await asyncio.wait_for(task, timeout=5)
        assert client._dispatch_tasks == set()
        assert json.loads(ws.sent[0])["payload"] == {"elementId": "pending", "statusCode": 500 if failure else 200}

    def test_stream_registration_contract(self):
        config = make_config(subscribe_streams=[SubscribeStreamConfig("orders", FunctionVisibility.PUBLIC)])
        payload = config.to_register_payload()["configuration"]
        assert payload["subscribeStreams"] == [{"name": "orders", "visibility": "Public"}]
        assert payload["streamMaxAgeSeconds"] == 86400
        assert payload["streamMaxLength"] == 100000

    @pytest.mark.asyncio
    @pytest.mark.parametrize("status", [200, 202, 503])
    async def test_stream_handler_identity_and_status(self, status):
        client = SlimFaasClient("ws://fake", make_config())
        received = []

        async def handler(message):
            received.append(message)
            return status

        client.on_stream_message(handler)
        ws = MockWebSocket([])
        message = StreamMessage.from_payload({
            "elementId": "stream_a", "streamName": "orders", "messageId": "generation_1",
            "method": "POST", "path": "/work", "body": "ZGF0YQ==", "tryNumber": 2,
        })
        await client._dispatch_stream_message(ws, message)
        assert received == [message]
        assert message.body == b"data"
        assert message.try_number == 2
        assert json.loads(ws.sent[0])["payload"]["statusCode"] == status

    @pytest.mark.asyncio
    async def test_stream_subscription_rejects_an_older_server(self):
        client = SlimFaasClient("ws://fake", make_config(subscribe_streams=[SubscribeStreamConfig("orders")]))
        ws = MockWebSocket([make_envelope(MessageType.REGISTER_RESPONSE, {"success": True, "connectionId": "old"})])
        with pytest.raises(SlimFaasRegistrationError, match="does not support"):
            await client._register(ws)

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
    def test_dead_letter_configuration_uses_existing_registration_contract(self):
        configuration = json.dumps({
            "DefaultAsync": {
                "HttpTimeout": 120,
                "DeadLetterQueue": {"Enabled": True, "MaxAgeSeconds": 604800, "MaxLength": 10000},
            },
        })
        payload = make_config(configuration=configuration).to_register_payload()
        assert payload["configuration"]["configuration"] == configuration
        assert json.loads(payload["configuration"]["configuration"])["DefaultAsync"]["DeadLetterQueue"]["Enabled"]

    def test_to_register_payload(self):
        config = make_config(
            function_name="my-job",
            subscribe_events=[SubscribeEventConfig(name="ev1", visibility=FunctionVisibility.PUBLIC)],
            default_visibility=FunctionVisibility.PRIVATE,
            number_parallel_request=3,
        )
        p = config.to_register_payload()
        assert p["functionName"] == "my-job"
        assert p["configuration"]["subscribeEvents"] == [{"name": "ev1", "visibility": "Public"}]
        assert p["configuration"]["defaultVisibility"] == "Private"
        assert p["configuration"]["numberParallelRequest"] == 3

    def test_to_register_payload_no_visibility(self):
        """Sans visibilité explicite, la clé 'visibility' ne doit pas apparaître dans le payload."""
        config = make_config(
            function_name="my-job",
            subscribe_events=[SubscribeEventConfig(name="ev1")],
        )
        p = config.to_register_payload()
        assert p["configuration"]["subscribeEvents"] == [{"name": "ev1"}]

    def test_to_register_payload_paths(self):
        config = make_config(
            function_name="my-job",
            subscribe_events=[],
            paths_start_with_visibility=[
                PathVisibilityConfig(path="/admin", visibility=FunctionVisibility.PRIVATE),
                PathVisibilityConfig(path="/api"),
            ],
        )
        p = config.to_register_payload()
        assert p["configuration"]["pathsStartWithVisibility"] == [
            {"path": "/admin", "visibility": "Private"},
            {"path": "/api", "visibility": "Public"},
        ]


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


# ---------------------------------------------------------------------------
# Tests du streaming synchrone (frames binaires)
# ---------------------------------------------------------------------------

class TestSyncRequestDispatch:
    """Réponse envoyée à SlimFaas quand le handler synchrone échoue."""

    CORRELATION_ID = "0f8fad5b-d9cb-469f-a165-70867728950e"

    async def _run_sync_request(self, handler) -> list[tuple[int, str, bytes]]:
        client = SlimFaasClient("ws://fake", make_config())
        client.on_sync_request(handler)
        ws = MockWebSocket([])
        client._ws = ws  # type: ignore[assignment]

        start = json.dumps({"method": "GET", "path": "/report", "query": "", "headers": {}}).encode("utf-8")
        client._handle_binary_frame(ws, BinaryFrame.encode(MessageType.SYNC_REQUEST_START, self.CORRELATION_ID, start))  # type: ignore[arg-type]
        client._handle_binary_frame(ws, BinaryFrame.encode(MessageType.SYNC_REQUEST_END, self.CORRELATION_ID))  # type: ignore[arg-type]
        for _ in range(10):
            await asyncio.sleep(0)

        frames = []
        for data in ws.sent:
            msg_type, correlation_id, _, length = BinaryFrame.decode_header(data)
            frames.append((msg_type, correlation_id, data[BinaryFrame.HEADER_SIZE:BinaryFrame.HEADER_SIZE + length]))
        return frames

    @pytest.mark.asyncio
    async def test_handler_failure_before_start_sends_500(self):
        async def handler(req: SyncRequest) -> None:
            raise ValueError("boom")

        frames = await self._run_sync_request(handler)

        assert [f[0] for f in frames] == [MessageType.SYNC_RESPONSE_START, MessageType.SYNC_RESPONSE_END]
        assert json.loads(frames[0][2])["statusCode"] == 500
        assert all(f[1] == self.CORRELATION_ID for f in frames)

    @pytest.mark.asyncio
    async def test_handler_failure_after_start_cancels_the_stream(self):
        async def handler(req: SyncRequest) -> None:
            await req.response.start(200, {"Content-Type": ["text/csv"]})
            await req.response.write(b"id,total\n1,")
            raise ValueError("boom")

        frames = await self._run_sync_request(handler)

        assert [f[0] for f in frames] == [
            MessageType.SYNC_RESPONSE_START,
            MessageType.SYNC_RESPONSE_CHUNK,
            MessageType.SYNC_CANCEL,
        ]
        assert json.loads(frames[0][2])["statusCode"] == 200
        assert all(f[1] == self.CORRELATION_ID for f in frames)

    @pytest.mark.asyncio
    async def test_handler_failure_after_complete_sends_nothing_more(self):
        async def handler(req: SyncRequest) -> None:
            await req.response.write(b"done")
            await req.response.complete()
            raise ValueError("boom")

        frames = await self._run_sync_request(handler)

        assert [f[0] for f in frames] == [
            MessageType.SYNC_RESPONSE_START,
            MessageType.SYNC_RESPONSE_CHUNK,
            MessageType.SYNC_RESPONSE_END,
        ]
