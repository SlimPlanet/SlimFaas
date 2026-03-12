"""
Data models for the SlimFaas WebSocket protocol.
"""

from __future__ import annotations

import struct
from dataclasses import dataclass, field
from enum import IntEnum, Enum
from typing import Callable, Optional, Tuple


# ---------------------------------------------------------------------------
# Protocol (must match WebSocketMessageType on the C# side)
# ---------------------------------------------------------------------------

class MessageType(IntEnum):
    REGISTER = 0
    REGISTER_RESPONSE = 1
    ASYNC_REQUEST = 2
    ASYNC_CALLBACK = 3
    PUBLISH_EVENT = 4
    PING = 5
    PONG = 6

    # Synchronous streaming (binary frames)
    SYNC_REQUEST_START = 0x10
    SYNC_REQUEST_CHUNK = 0x11
    SYNC_REQUEST_END = 0x12
    SYNC_RESPONSE_START = 0x20
    SYNC_RESPONSE_CHUNK = 0x21
    SYNC_RESPONSE_END = 0x22
    SYNC_CANCEL = 0x30


# ---------------------------------------------------------------------------
# Typed enums (replace magic strings)
# ---------------------------------------------------------------------------

class FunctionVisibility(str, Enum):
    """Visibility of a function, event, or path."""

    PUBLIC = "Public"
    """Accessible from outside the namespace."""

    PRIVATE = "Private"
    """Accessible only from within the namespace."""


class FunctionTrust(str, Enum):
    """Trust level of a function."""

    TRUSTED = "Trusted"
    """Trusted function (no additional restrictions)."""

    UNTRUSTED = "Untrusted"
    """Untrusted function (security restrictions applied)."""


# ---------------------------------------------------------------------------
# Structures for SubscribeEvents and PathsStartWithVisibility
# ---------------------------------------------------------------------------

@dataclass
class SubscribeEventConfig:
    """
    Describes an event to subscribe to, with an optional visibility override.

    If ``visibility`` is ``None``, the client's ``default_visibility`` is used.

    Example::

        SubscribeEventConfig(name="fibo-public", visibility=FunctionVisibility.PUBLIC)
        SubscribeEventConfig(name="fibo-private")  # inherits default_visibility
    """

    name: str
    """Event name (e.g. "fibo-public")."""

    visibility: Optional[FunctionVisibility] = None
    """Visibility override, or None to inherit default_visibility."""


@dataclass
class PathVisibilityConfig:
    """
    Describes a visibility rule for a path prefix.

    Example::

        PathVisibilityConfig(path="/admin", visibility=FunctionVisibility.PRIVATE)
    """

    path: str
    """Path prefix (e.g. "/admin")."""

    visibility: FunctionVisibility = FunctionVisibility.PUBLIC
    """Visibility for this path prefix."""


# ---------------------------------------------------------------------------
# Client configuration
# ---------------------------------------------------------------------------

@dataclass
class SlimFaasClientConfig:
    """
    Configuration for a WebSocket function or job.

    Maps to the following Kubernetes SlimFaas annotations:
    - ``SlimFaas/DependsOn``
    - ``SlimFaas/SubscribeEvents``
    - ``SlimFaas/DefaultVisibility``
    - ``SlimFaas/PathsStartWithVisibility``
    - ``SlimFaas/Configuration``
    - ``SlimFaas/ReplicasStartAsSoonAsOneFunctionRetrieveARequest``
    - ``SlimFaas/NumberParallelRequest``
    - ``SlimFaas/NumberParallelRequestPerPod``
    - ``SlimFaas/DefaultTrust``
    """

    function_name: str
    """Unique name of the function or job (equivalent to the Kubernetes Deployment name)."""

    depends_on: list[str] = field(default_factory=list)
    """Names of functions this one depends on (SlimFaas/DependsOn)."""

    subscribe_events: list[SubscribeEventConfig] = field(default_factory=list)
    """
    Events to subscribe to (SlimFaas/SubscribeEvents).
    Each entry may individually override visibility.
    If ``SubscribeEventConfig.visibility`` is None, ``default_visibility`` is used.
    """

    default_visibility: FunctionVisibility = FunctionVisibility.PUBLIC
    """Default visibility (SlimFaas/DefaultVisibility)."""

    paths_start_with_visibility: list[PathVisibilityConfig] = field(default_factory=list)
    """Visibility rules per path prefix (SlimFaas/PathsStartWithVisibility)."""

    configuration: str = ""
    """Free-form JSON configuration for the function (SlimFaas/Configuration)."""

    replicas_start_as_soon_as_one_function_retrieve_a_request: bool = False
    """SlimFaas/ReplicasStartAsSoonAsOneFunctionRetrieveARequest."""

    number_parallel_request: int = 10
    """Maximum number of parallel requests across all replicas (SlimFaas/NumberParallelRequest)."""

    number_parallel_request_per_pod: int = 10
    """Maximum number of parallel requests per pod (SlimFaas/NumberParallelRequestPerPod)."""

    default_trust: FunctionTrust = FunctionTrust.TRUSTED
    """Trust level (SlimFaas/DefaultTrust)."""

    def to_register_payload(self) -> dict:
        return {
            "functionName": self.function_name,
            "configuration": {
                "dependsOn": self.depends_on,
                "subscribeEvents": [
                    {"name": e.name, **({"visibility": e.visibility.value} if e.visibility is not None else {})}
                    for e in self.subscribe_events
                ],
                "defaultVisibility": self.default_visibility.value,
                "pathsStartWithVisibility": [
                    {"path": p.path, "visibility": p.visibility.value}
                    for p in self.paths_start_with_visibility
                ],
                "configuration": self.configuration,
                "replicasStartAsSoonAsOneFunctionRetrieveARequest": self.replicas_start_as_soon_as_one_function_retrieve_a_request,
                "numberParallelRequest": self.number_parallel_request,
                "numberParallelRequestPerPod": self.number_parallel_request_per_pod,
                "defaultTrust": self.default_trust.value,
            },
        }


# ---------------------------------------------------------------------------
# Messages received by the client
# ---------------------------------------------------------------------------

@dataclass
class AsyncRequest:
    """Asynchronous request sent by SlimFaas to the WebSocket client."""

    element_id: str
    """Unique identifier of the queue entry."""

    method: str
    """HTTP method (GET, POST, PUT, DELETE, …)."""

    path: str
    """Request path."""

    query: str
    """Query string (including the leading '?' if non-empty)."""

    headers: dict[str, list[str]]
    """HTTP headers."""

    body: Optional[bytes]
    """Request body (decoded from base64)."""

    is_last_try: bool
    """True if this is the last retry attempt."""

    try_number: int
    """Attempt number (starts at 1)."""

    @classmethod
    def from_payload(cls, payload: dict) -> "AsyncRequest":
        body_b64: Optional[str] = payload.get("body")
        import base64
        body = base64.b64decode(body_b64) if body_b64 else None
        return cls(
            element_id=payload["elementId"],
            method=payload["method"],
            path=payload["path"],
            query=payload.get("query", ""),
            headers={k: v for k, v in payload.get("headers", {}).items()},
            body=body,
            is_last_try=payload.get("isLastTry", False),
            try_number=payload.get("tryNumber", 1),
        )


@dataclass
class AsyncCallback:
    """Callback response to send to SlimFaas after processing an AsyncRequest."""

    element_id: str
    status_code: int = 200


@dataclass
class PublishEvent:
    """Publish/subscribe event received from SlimFaas."""

    event_name: str
    method: str
    path: str
    query: str
    headers: dict[str, list[str]]
    body: Optional[bytes]

    @classmethod
    def from_payload(cls, payload: dict) -> "PublishEvent":
        body_b64: Optional[str] = payload.get("body")
        import base64
        body = base64.b64decode(body_b64) if body_b64 else None
        return cls(
            event_name=payload["eventName"],
            method=payload["method"],
            path=payload["path"],
            query=payload.get("query", ""),
            headers={k: v for k, v in payload.get("headers", {}).items()},
            body=body,
        )


# ---------------------------------------------------------------------------
# Synchronous streaming — binary frames
# ---------------------------------------------------------------------------

class BinaryFrame:
    """
    Utility for encoding/decoding binary frames for synchronous streaming.

    Format: [type 1B][correlationId 36B ASCII][flags 1B][length 4B BE][payload nB]
    Total header: 42 bytes.
    """

    HEADER_SIZE = 42
    FLAG_END_OF_STREAM = 0x01

    @staticmethod
    def encode(msg_type: int, correlation_id: str, payload: bytes = b"", flags: int = 0) -> bytes:
        corr = correlation_id.ljust(36)[:36].encode("ascii")
        length = len(payload)
        header = struct.pack(">B", msg_type) + corr + struct.pack(">BI", flags, length)
        return header + payload

    @staticmethod
    def decode_header(data: bytes) -> Tuple[int, str, int, int]:
        """Returns (type, correlationId, flags, payloadLength)."""
        if len(data) < BinaryFrame.HEADER_SIZE:
            raise ValueError(f"Binary frame must be at least {BinaryFrame.HEADER_SIZE} bytes")
        msg_type = data[0]
        correlation_id = data[1:37].decode("ascii").strip()
        flags = data[37]
        length = struct.unpack(">I", data[38:42])[0]
        return msg_type, correlation_id, flags, length


class SyncBodyStream:
    """
    Asynchronous stream of the body of a synchronous request received via WebSocket.

    Exposes the binary chunks received on the fly as a readable object in three ways:

    1. **Chunk-by-chunk** (async for)::

        async for chunk in req.body:
            process(chunk)

    2. **Read a specific number of bytes**::

        data = await req.body.read(1024)   # returns up to 1024 bytes
        data = await req.body.read()       # reads until end

    3. **Read everything at once**::

        all_bytes = await req.body.readall()

    The stream is finished when ``read()`` returns ``b""`` or
    when ``async for`` stops naturally.
    """

    def __init__(self, queue: asyncio.Queue) -> None:
        self._queue = queue
        self._buf = b""
        self._eof = False

    # ── async for chunk in stream ────────────────────────────────────────

    def __aiter__(self) -> "SyncBodyStream":
        return self

    async def __anext__(self) -> bytes:
        if self._buf:
            chunk, self._buf = self._buf, b""
            return chunk
        if self._eof:
            raise StopAsyncIteration
        chunk = await self._queue.get()
        if chunk is None:
            self._eof = True
            raise StopAsyncIteration
        return chunk

    # ── read(n=-1) ───────────────────────────────────────────────────────

    async def read(self, n: int = -1) -> bytes:
        """
        Read up to ``n`` bytes from the stream.
        If ``n == -1`` (default), reads until the end of the stream.
        Returns ``b""`` at end of stream.
        """
        if n == -1:
            return await self.readall()

        # Accumulate until we have n bytes or EOF
        while len(self._buf) < n and not self._eof:
            chunk = await self._queue.get()
            if chunk is None:
                self._eof = True
                break
            self._buf += chunk

        result, self._buf = self._buf[:n], self._buf[n:]
        return result

    # ── readall() ────────────────────────────────────────────────────────

    async def readall(self) -> bytes:
        """Reads the entire body until end of stream and returns the bytes."""
        parts = [self._buf]
        self._buf = b""
        while not self._eof:
            chunk = await self._queue.get()
            if chunk is None:
                self._eof = True
                break
            parts.append(chunk)
        return b"".join(parts)

    # ── Internal feeding (called by the driver) ───────────────────────────

    def _feed(self, chunk: bytes) -> None:
        """Pushes a chunk into the queue (called by the driver)."""
        self._queue.put_nowait(chunk)

    def _close(self) -> None:
        """Signals end of stream (sentinel None)."""
        self._queue.put_nowait(None)


@dataclass
class SyncRequest:
    """Synchronous streaming request received by the WebSocket client."""

    correlation_id: str
    """Correlation ID of the stream."""

    method: str
    """HTTP method (GET, POST, PUT, DELETE, …)."""

    path: str
    """Request path."""

    query: str
    """Query string."""

    headers: dict[str, list[str]]
    """HTTP headers."""

    body: "SyncBodyStream" = field(default_factory=lambda: SyncBodyStream(asyncio.Queue()))
    """
    Asynchronous stream of the request body.
    Readable via ``async for``, ``await body.read(n)`` or ``await body.readall()``.
    """

    response: "SyncResponseWriter" = field(default=None)  # type: ignore[assignment]
    """
    Writer to build the synchronous response.

    Usage::

        await req.response.start(200, {"Content-Type": ["application/json"]})
        await req.response.write(b'{"result": 42}')
        await req.response.complete()
    """


class SyncResponseWriter:
    """
    Writer that encapsulates sending the synchronous response back to SlimFaas.

    Typical usage inside an ``on_sync_request`` handler::

        await req.response.start(200, {"Content-Type": ["text/plain"]})
        await req.response.write(b"Hello, world!")
        await req.response.complete()

    Shorthand (``start`` is called automatically if omitted)::

        await req.response.write(b"Hello")
        await req.response.complete()

    ``complete()`` is idempotent and can be called multiple times.
    If ``start()`` was not called before ``write()`` or ``complete()``,
    a status 200 with empty headers is sent automatically.
    """

    def __init__(
        self,
        correlation_id: str,
        send_start: Callable,
        send_chunk: Callable,
        send_end: Callable,
    ) -> None:
        self._correlation_id = correlation_id
        self._send_start = send_start
        self._send_chunk = send_chunk
        self._send_end = send_end
        self._started = False
        self._completed = False

    async def start(
        self,
        status_code: int = 200,
        headers: Optional[dict[str, list[str]]] = None,
    ) -> None:
        """Send the beginning of the response (status code + headers)."""
        if self._started:
            raise RuntimeError("Response already started.")
        if self._completed:
            raise RuntimeError("Response already completed.")
        self._started = True
        await self._send_start(
            self._correlation_id,
            SyncResponse(status_code=status_code, headers=headers or {}),
        )

    async def write(self, data: bytes) -> None:
        """Send a chunk of the response body."""
        if self._completed:
            raise RuntimeError("Response already completed.")
        if not self._started:
            await self.start()
        if data:
            await self._send_chunk(self._correlation_id, data)

    async def complete(self) -> None:
        """Signal end of response. Idempotent."""
        if self._completed:
            return
        if not self._started:
            await self.start()
        self._completed = True
        await self._send_end(self._correlation_id)


@dataclass
class SyncResponse:
    """Synchronous response to send for a streaming sync request."""

    status_code: int = 200
    headers: dict[str, list[str]] = field(default_factory=dict)
