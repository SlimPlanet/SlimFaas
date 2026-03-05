"""
Modèles de données pour le protocole WebSocket SlimFaas.
"""

from __future__ import annotations

import struct
from dataclasses import dataclass, field
from enum import IntEnum, Enum
from typing import Callable, Optional, Tuple


# ---------------------------------------------------------------------------
# Protocole (doit correspondre à WebSocketMessageType côté C#)
# ---------------------------------------------------------------------------

class MessageType(IntEnum):
    REGISTER = 0
    REGISTER_RESPONSE = 1
    ASYNC_REQUEST = 2
    ASYNC_CALLBACK = 3
    PUBLISH_EVENT = 4
    PING = 5
    PONG = 6

    # Streaming synchrone (frames binaires)
    SYNC_REQUEST_START = 0x10
    SYNC_REQUEST_CHUNK = 0x11
    SYNC_REQUEST_END = 0x12
    SYNC_RESPONSE_START = 0x20
    SYNC_RESPONSE_CHUNK = 0x21
    SYNC_RESPONSE_END = 0x22
    SYNC_CANCEL = 0x30


# ---------------------------------------------------------------------------
# Enums typés (remplacent les magic strings)
# ---------------------------------------------------------------------------

class FunctionVisibility(str, Enum):
    """Visibilité d'une fonction, d'un évènement ou d'un path."""

    PUBLIC = "Public"
    """Accessible depuis l'extérieur du namespace."""

    PRIVATE = "Private"
    """Accessible uniquement depuis l'intérieur du namespace."""


class FunctionTrust(str, Enum):
    """Niveau de confiance d'une fonction."""

    TRUSTED = "Trusted"
    """Fonction de confiance (pas de restrictions supplémentaires)."""

    UNTRUSTED = "Untrusted"
    """Fonction non-fiable (restrictions de sécurité appliquées)."""


# ---------------------------------------------------------------------------
# Structures pour SubscribeEvents et PathsStartWithVisibility
# ---------------------------------------------------------------------------

@dataclass
class SubscribeEventConfig:
    """
    Décrit un évènement auquel s'abonner, avec une visibilité optionnelle.

    Si ``visibility`` est ``None``, la visibilité par défaut (``default_visibility``)
    du client est utilisée.

    Exemple ::

        SubscribeEventConfig(name="fibo-public", visibility=FunctionVisibility.PUBLIC)
        SubscribeEventConfig(name="fibo-private")  # hérite de default_visibility
    """

    name: str
    """Nom de l'évènement (ex : "fibo-public")."""

    visibility: Optional[FunctionVisibility] = None
    """Surcharge de visibilité, ou None pour hériter de default_visibility."""


@dataclass
class PathVisibilityConfig:
    """
    Décrit une règle de visibilité par préfixe de chemin.

    Exemple ::

        PathVisibilityConfig(path="/admin", visibility=FunctionVisibility.PRIVATE)
    """

    path: str
    """Préfixe de chemin (ex : "/admin")."""

    visibility: FunctionVisibility = FunctionVisibility.PUBLIC
    """Visibilité de ce préfixe de chemin."""


# ---------------------------------------------------------------------------
# Configuration du client
# ---------------------------------------------------------------------------

@dataclass
class SlimFaasClientConfig:
    """
    Configuration d'une fonction/job WebSocket.

    Correspond aux annotations Kubernetes SlimFaas :
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
    """Nom unique de la fonction ou du job (équivalent au nom du Deployment Kubernetes)."""

    depends_on: list[str] = field(default_factory=list)
    """Noms des fonctions dont celle-ci dépend (SlimFaas/DependsOn)."""

    subscribe_events: list[SubscribeEventConfig] = field(default_factory=list)
    """
    Évènements auxquels s'abonner (SlimFaas/SubscribeEvents).
    Chaque entrée peut surcharger la visibilité individuellement.
    Si ``SubscribeEventConfig.visibility`` est None, ``default_visibility`` est utilisé.
    """

    default_visibility: FunctionVisibility = FunctionVisibility.PUBLIC
    """Visibilité par défaut (SlimFaas/DefaultVisibility)."""

    paths_start_with_visibility: list[PathVisibilityConfig] = field(default_factory=list)
    """
    Règles de visibilité par préfixe de chemin (SlimFaas/PathsStartWithVisibility).
    """

    configuration: str = ""
    """Configuration JSON libre de la fonction (SlimFaas/Configuration)."""

    replicas_start_as_soon_as_one_function_retrieve_a_request: bool = False
    """SlimFaas/ReplicasStartAsSoonAsOneFunctionRetrieveARequest."""

    number_parallel_request: int = 10
    """Nombre maximum de requêtes parallèles (SlimFaas/NumberParallelRequest)."""

    number_parallel_request_per_pod: int = 10
    """Nombre maximum de requêtes parallèles par pod (SlimFaas/NumberParallelRequestPerPod)."""

    default_trust: FunctionTrust = FunctionTrust.TRUSTED
    """Niveau de confiance (SlimFaas/DefaultTrust)."""

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
# Messages reçus par le client
# ---------------------------------------------------------------------------

@dataclass
class AsyncRequest:
    """Requête asynchrone envoyée par SlimFaas au client WebSocket."""

    element_id: str
    """Identifiant unique de l'élément de queue."""

    method: str
    """Méthode HTTP (GET, POST, PUT, DELETE, …)."""

    path: str
    """Chemin de la requête."""

    query: str
    """Query string (avec le '?' initial si non vide)."""

    headers: dict[str, list[str]]
    """En-têtes HTTP."""

    body: Optional[bytes]
    """Corps de la requête (décodé depuis base64)."""

    is_last_try: bool
    """True si c'est la dernière tentative."""

    try_number: int
    """Numéro de tentative (commence à 1)."""

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
    """Réponse à envoyer à SlimFaas après traitement d'une AsyncRequest."""

    element_id: str
    status_code: int = 200


@dataclass
class PublishEvent:
    """Évènement publish/subscribe reçu depuis SlimFaas."""

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
# Streaming synchrone — frames binaires
# ---------------------------------------------------------------------------

class BinaryFrame:
    """
    Utilitaire pour encoder/décoder des frames binaires de streaming synchrone.

    Format : [type 1B][correlationId 36B ASCII][flags 1B][length 4B BE][payload nB]
    Header total : 42 octets.
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
        """Retourne (type, correlationId, flags, payloadLength)."""
        if len(data) < BinaryFrame.HEADER_SIZE:
            raise ValueError(f"Binary frame must be at least {BinaryFrame.HEADER_SIZE} bytes")
        msg_type = data[0]
        correlation_id = data[1:37].decode("ascii").strip()
        flags = data[37]
        length = struct.unpack(">I", data[38:42])[0]
        return msg_type, correlation_id, flags, length


class SyncBodyStream:
    """
    Stream asynchrone du body d'une requête synchrone reçue via WebSocket.

    Expose les chunks binaires reçus au fil de l'eau comme un objet lisible
    de trois façons :

    1. **Lecture par chunk** (async for) ::

        async for chunk in req.body:
            process(chunk)

    2. **Lecture d'un nombre d'octets précis** ::

        data = await req.body.read(1024)   # retourne jusqu'à 1024 octets
        data = await req.body.read()       # lit tout jusqu'à la fin

    3. **Lecture complète** ::

        all_bytes = await req.body.readall()

    Le stream est terminé quand ``read()`` retourne ``b""`` ou quand
    ``async for`` s'arrête naturellement.
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
        Lit jusqu'à ``n`` octets depuis le stream.
        Si ``n == -1`` (défaut), lit tout jusqu'à la fin du stream.
        Retourne ``b""`` en fin de stream.
        """
        if n == -1:
            return await self.readall()

        # Accumuler jusqu'à avoir n octets ou EOF
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
        """Lit tout le body jusqu'à la fin du stream et retourne les bytes."""
        parts = [self._buf]
        self._buf = b""
        while not self._eof:
            chunk = await self._queue.get()
            if chunk is None:
                self._eof = True
                break
            parts.append(chunk)
        return b"".join(parts)

    # ── Alimentation interne (appelé par le client) ───────────────────────

    def _feed(self, chunk: bytes) -> None:
        """Ajoute un chunk dans la queue (appelé par le driver)."""
        self._queue.put_nowait(chunk)

    def _close(self) -> None:
        """Signale la fin du stream (sentinel None)."""
        self._queue.put_nowait(None)


@dataclass
class SyncRequest:
    """Requête synchrone streamée reçue par le client WebSocket."""

    correlation_id: str
    """Identifiant de corrélation du stream."""

    method: str
    """Méthode HTTP (GET, POST, PUT, DELETE, …)."""

    path: str
    """Chemin de la requête."""

    query: str
    """Query string."""

    headers: dict[str, list[str]]
    """En-têtes HTTP."""

    body: "SyncBodyStream" = field(default_factory=lambda: SyncBodyStream(asyncio.Queue()))
    """
    Stream asynchrone du body de la requête.
    Lisible via ``async for``, ``await body.read(n)`` ou ``await body.readall()``.
    """

    response: "SyncResponseWriter" = field(default=None)  # type: ignore[assignment]
    """
    Writer pour construire la réponse synchrone.

    Utilisation ::

        await req.response.start(200, {"Content-Type": ["application/json"]})
        await req.response.write(b'{"result": 42}')
        await req.response.complete()
    """


class SyncResponseWriter:
    """
    Writer qui encapsule l'envoi de la réponse synchrone vers SlimFaas.

    Utilisation typique dans un handler ``on_sync_request`` ::

        await req.response.start(200, {"Content-Type": ["text/plain"]})
        await req.response.write(b"Hello, world!")
        await req.response.complete()

    Ou de façon plus concise (``start`` est appelé automatiquement si omis) ::

        await req.response.write(b"Hello")
        await req.response.complete()

    ``complete()`` est idempotent et peut être appelé plusieurs fois.
    Si ``start()`` n'a pas été appelé avant ``write()`` ou ``complete()``,
    un status 200 avec des headers vides sera envoyé automatiquement.
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
        """Envoie le début de la réponse (status code + headers)."""
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
        """Envoie un chunk du body de la réponse."""
        if self._completed:
            raise RuntimeError("Response already completed.")
        if not self._started:
            await self.start()
        if data:
            await self._send_chunk(self._correlation_id, data)

    async def complete(self) -> None:
        """Signale la fin de la réponse. Idempotent."""
        if self._completed:
            return
        if not self._started:
            await self.start()
        self._completed = True
        await self._send_end(self._correlation_id)


@dataclass
class SyncResponse:
    """Réponse synchrone à envoyer pour une requête sync streamée."""

    status_code: int = 200
    headers: dict[str, list[str]] = field(default_factory=dict)
