"""
SlimFaasClient - connexion WebSocket principale.
"""

from __future__ import annotations

import asyncio
import json
import logging
import uuid
from typing import Awaitable, Callable, Optional

import websockets
from websockets.asyncio.client import ClientConnection

from slimfaas_client._models import (
    AsyncCallback,
    AsyncRequest,
    BinaryFrame,
    MessageType,
    PublishEvent,
    SlimFaasClientConfig,
    SyncBodyStream,
    SyncRequest,
    SyncResponse,
)

logger = logging.getLogger(__name__)

# Type des callbacks
AsyncRequestHandler = Callable[[AsyncRequest], Awaitable[int]]
PublishEventHandler = Callable[[PublishEvent], Awaitable[None]]
SyncRequestHandler = Callable[["SlimFaasClient", SyncRequest], Awaitable[None]]


class SlimFaasRegistrationError(Exception):
    """Levée quand SlimFaas refuse l'enregistrement du client."""


class SlimFaasClient:
    """
    Client WebSocket SlimFaas.

    Conceptuellement, chaque instance représente un "replica virtuel" d'une
    fonction ou d'un job. SlimFaas lui achemine les requêtes asynchrones et
    les évènements publish/subscribe au lieu de faire des appels HTTP.

    Utilisation :

    .. code-block:: python

        config = SlimFaasClientConfig(
            function_name="my-job",
            subscribe_events=["order-created"],
        )
        async with SlimFaasClient("ws://slimfaas:5003/ws", config) as client:
            client.on_async_request(handle_request)
            client.on_publish_event(handle_event)
            await client.run_forever()

    Les callbacks enregistrés via :meth:`on_async_request` et
    :meth:`on_publish_event` sont appelés dans des tâches asyncio séparées
    afin de ne pas bloquer la boucle de lecture.

    Paramètres
    ----------
    url:
        URL WebSocket de SlimFaas, ex. ``ws://slimfaas:5003/ws``.
    config:
        Configuration de la fonction/job.
    reconnect_delay:
        Délai en secondes entre deux tentatives de reconnexion (défaut : 5 s).
    ping_interval:
        Intervalle en secondes entre deux pings keepalive (défaut : 30 s,
        0 pour désactiver).
    """

    def __init__(
        self,
        url: str,
        config: SlimFaasClientConfig,
        *,
        reconnect_delay: float = 5.0,
        ping_interval: float = 30.0,
    ) -> None:
        self._url = url
        self._config = config
        self._reconnect_delay = reconnect_delay
        self._ping_interval = ping_interval

        self._async_request_handler: Optional[AsyncRequestHandler] = None
        self._publish_event_handler: Optional[PublishEventHandler] = None
        self._sync_request_handler: Optional[SyncRequestHandler] = None

        self._connection_id: Optional[str] = None
        self._ws: Optional[ClientConnection] = None
        self._running = False
        self._stop_event = asyncio.Event()

        # Pending sync request body streams: correlationId -> SyncBodyStream
        self._pending_sync_bodies: dict[str, SyncBodyStream] = {}

    # ------------------------------------------------------------------
    # Gestionnaire de contexte asynchrone
    # ------------------------------------------------------------------

    async def __aenter__(self) -> "SlimFaasClient":
        self._running = True
        self._stop_event.clear()
        return self

    async def __aexit__(self, *_) -> None:
        await self.close()

    # ------------------------------------------------------------------
    # Enregistrement des callbacks
    # ------------------------------------------------------------------

    def on_async_request(self, handler: AsyncRequestHandler) -> None:
        """
        Enregistre le callback appelé pour chaque requête asynchrone.

        Le callback doit retourner un entier représentant le code HTTP
        (ex. 200, 500). SlimFaas utilise ce code pour gérer les retries.
        Retourner 202 indique que le traitement continue en tâche de fond
        et que le callback enverra lui-même le résultat via
        :meth:`send_callback`.
        """
        self._async_request_handler = handler

    def on_publish_event(self, handler: PublishEventHandler) -> None:
        """
        Enregistre le callback appelé pour chaque évènement publish/subscribe.
        """
        self._publish_event_handler = handler

    def on_sync_request(self, handler: SyncRequestHandler) -> None:
        """
        Enregistre le callback appelé pour chaque requête synchrone streamée.

        Le handler reçoit ``(client, sync_request)`` et doit :
        1. Appeler ``await client.send_sync_response_start(corr_id, response)``
        2. Appeler ``await client.send_sync_response_chunk(corr_id, data)`` par chunk
        3. Appeler ``await client.send_sync_response_end(corr_id)``
        """
        self._sync_request_handler = handler

    # ------------------------------------------------------------------
    # Boucle principale
    # ------------------------------------------------------------------

    async def run_forever(self) -> None:
        """
        Lance la boucle de connexion/reconnexion. Retourne quand :meth:`close`
        est appelé.
        """
        self._running = True
        self._stop_event.clear()

        while self._running:
            try:
                await self._connect_and_loop()
            except SlimFaasRegistrationError as exc:
                logger.error("SlimFaas registration failed (fatal): %s", exc)
                break
            except Exception as exc:
                if not self._running:
                    break
                logger.warning(
                    "WebSocket disconnected (%s). Reconnecting in %.1f s…",
                    exc,
                    self._reconnect_delay,
                )
                await asyncio.sleep(self._reconnect_delay)

    async def close(self) -> None:
        """Arrête proprement le client."""
        self._running = False
        self._stop_event.set()
        if self._ws is not None:
            await self._ws.close()

    # ------------------------------------------------------------------
    # Envoi d'un callback manuel (pour les traitements longs - status 202)
    # ------------------------------------------------------------------

    async def send_callback(self, element_id: str, status_code: int = 200) -> None:
        """
        Envoie manuellement le résultat d'une requête asynchrone.

        Utile quand le handler a retourné 202 pour indiquer un traitement long.
        """
        if self._ws is None:
            raise RuntimeError("WebSocket is not connected")
        await self._send_json({
            "type": MessageType.ASYNC_CALLBACK,
            "correlationId": element_id,
            "payload": {
                "elementId": element_id,
                "statusCode": status_code,
            },
        })

    # ------------------------------------------------------------------
    # Implémentation interne
    # ------------------------------------------------------------------

    async def _connect_and_loop(self) -> None:
        logger.info("Connecting to SlimFaas WebSocket at %s …", self._url)

        async with websockets.connect(self._url) as ws:  # type: ignore[attr-defined]
            self._ws = ws
            logger.info("Connected. Registering function '%s' …", self._config.function_name)

            await self._register(ws)

            ping_task = asyncio.create_task(self._ping_loop(ws)) if self._ping_interval > 0 else None

            try:
                async for raw in ws:
                    if isinstance(raw, bytes):
                        # Could be a binary sync frame
                        if len(raw) >= BinaryFrame.HEADER_SIZE:
                            self._handle_binary_frame(ws, raw)
                        else:
                            # Try to decode as UTF-8 text
                            try:
                                await self._handle_message(ws, raw.decode("utf-8"))
                            except UnicodeDecodeError:
                                logger.warning("Received unrecognized binary data (%d bytes)", len(raw))
                    else:
                        await self._handle_message(ws, raw)
            finally:
                if ping_task is not None:
                    ping_task.cancel()
                # Fermer tous les body streams en cours (connexion perdue)
                for stream in self._pending_sync_bodies.values():
                    stream._close()
                self._pending_sync_bodies.clear()
                self._ws = None

    async def _register(self, ws: ClientConnection) -> None:
        correlation_id = str(uuid.uuid4())
        payload = self._config.to_register_payload()

        await self._send_json({
            "type": MessageType.REGISTER,
            "correlationId": correlation_id,
            "payload": payload,
        }, ws=ws)

        # Attend la réponse d'enregistrement
        async for raw in ws:
            if isinstance(raw, bytes):
                raw = raw.decode("utf-8")
            msg = json.loads(raw)
            if msg.get("type") == MessageType.REGISTER_RESPONSE:
                resp_payload = msg.get("payload", {})
                if not resp_payload.get("success"):
                    error = resp_payload.get("error", "Unknown registration error")
                    raise SlimFaasRegistrationError(error)
                self._connection_id = resp_payload.get("connectionId")
                logger.info(
                    "Registered successfully. connectionId=%s",
                    self._connection_id,
                )
                return
            # On ignore les autres messages pendant l'enregistrement
            logger.debug("Ignoring message during registration: type=%s", msg.get("type"))

    async def _handle_message(self, ws: ClientConnection, raw: str) -> None:
        try:
            msg = json.loads(raw)
        except json.JSONDecodeError:
            logger.warning("Received invalid JSON: %s", raw[:200])
            return

        msg_type = msg.get("type")
        payload = msg.get("payload")

        if msg_type == MessageType.ASYNC_REQUEST:
            if payload is None:
                logger.warning("AsyncRequest without payload")
                return
            req = AsyncRequest.from_payload(payload)
            asyncio.create_task(self._dispatch_async_request(ws, req))

        elif msg_type == MessageType.PUBLISH_EVENT:
            if payload is None:
                logger.warning("PublishEvent without payload")
                return
            evt = PublishEvent.from_payload(payload)
            asyncio.create_task(self._dispatch_publish_event(evt))

        elif msg_type == MessageType.PONG:
            logger.debug("Pong received")

        elif msg_type == MessageType.REGISTER_RESPONSE:
            # Peut arriver si l'enregistrement a été re-tenté
            logger.debug("Unexpected RegisterResponse received after registration")

        else:
            logger.debug("Unhandled message type: %s", msg_type)

    async def _dispatch_async_request(self, ws: ClientConnection, req: AsyncRequest) -> None:
        if self._async_request_handler is None:
            logger.warning(
                "Received AsyncRequest for %s but no handler registered. Returning 500.",
                req.element_id,
            )
            await self._send_callback(ws, req.element_id, 500)
            return

        try:
            status_code = await self._async_request_handler(req)
        except Exception as exc:
            logger.error("AsyncRequest handler raised an exception: %s", exc, exc_info=True)
            status_code = 500

        # 202 = le client gérera le callback lui-même
        if status_code != 202:
            await self._send_callback(ws, req.element_id, status_code)

    async def _dispatch_publish_event(self, evt: PublishEvent) -> None:
        if self._publish_event_handler is None:
            logger.debug("Received PublishEvent '%s' but no handler registered.", evt.event_name)
            return

        try:
            await self._publish_event_handler(evt)
        except Exception as exc:
            logger.error("PublishEvent handler raised an exception: %s", exc, exc_info=True)

    async def _send_callback(self, ws: ClientConnection, element_id: str, status_code: int) -> None:
        await self._send_json({
            "type": MessageType.ASYNC_CALLBACK,
            "correlationId": element_id,
            "payload": {
                "elementId": element_id,
                "statusCode": status_code,
            },
        }, ws=ws)

    async def _ping_loop(self, ws: ClientConnection) -> None:
        while True:
            await asyncio.sleep(self._ping_interval)
            try:
                await self._send_json({
                    "type": MessageType.PING,
                    "correlationId": str(uuid.uuid4()),
                    "payload": None,
                }, ws=ws)
            except Exception:
                break

    async def _send_json(self, data: dict, *, ws: Optional[ClientConnection] = None) -> None:
        target = ws or self._ws
        if target is None:
            raise RuntimeError("WebSocket is not connected")
        await target.send(json.dumps(data))

    async def _send_binary(self, data: bytes, *, ws: Optional[ClientConnection] = None) -> None:
        target = ws or self._ws
        if target is None:
            raise RuntimeError("WebSocket is not connected")
        await target.send(data)

    # ------------------------------------------------------------------
    # Streaming synchrone — frames binaires
    # ------------------------------------------------------------------

    def _handle_binary_frame(self, ws: ClientConnection, data: bytes) -> None:
        """Route une frame binaire de streaming sync."""
        msg_type, correlation_id, flags, payload_length = BinaryFrame.decode_header(data)
        payload = data[BinaryFrame.HEADER_SIZE:BinaryFrame.HEADER_SIZE + payload_length]

        if msg_type == MessageType.SYNC_REQUEST_START:
            try:
                start = json.loads(payload.decode("utf-8"))
            except Exception as exc:
                logger.warning("Failed to parse SyncRequestStart: %s", exc)
                return
            body_stream = SyncBodyStream(asyncio.Queue())
            self._pending_sync_bodies[correlation_id] = body_stream
            req = SyncRequest(
                correlation_id=correlation_id,
                method=start.get("method", "GET"),
                path=start.get("path", ""),
                query=start.get("query", ""),
                headers=start.get("headers", {}),
                body=body_stream,
            )
            asyncio.create_task(self._dispatch_sync_request(ws, req))

        elif msg_type == MessageType.SYNC_REQUEST_CHUNK:
            stream = self._pending_sync_bodies.get(correlation_id)
            if stream is not None:
                stream._feed(payload)

        elif msg_type == MessageType.SYNC_REQUEST_END:
            stream = self._pending_sync_bodies.pop(correlation_id, None)
            if stream is not None:
                stream._close()

        elif msg_type == MessageType.SYNC_CANCEL:
            stream = self._pending_sync_bodies.pop(correlation_id, None)
            if stream is not None:
                stream._close()

        else:
            logger.debug("Unhandled binary frame type: 0x%02x", msg_type)

    async def _dispatch_sync_request(self, ws: ClientConnection, req: SyncRequest) -> None:
        if self._sync_request_handler is None:
            logger.warning(
                "Received SyncRequest for %s but no handler registered. Returning 500.",
                req.correlation_id,
            )
            await self.send_sync_response_start(req.correlation_id, SyncResponse(status_code=500))
            await self.send_sync_response_end(req.correlation_id)
            return
        try:
            await self._sync_request_handler(self, req)
        except Exception as exc:
            logger.error("SyncRequest handler raised: %s", exc, exc_info=True)
            try:
                await self.send_sync_response_start(req.correlation_id, SyncResponse(status_code=500))
                await self.send_sync_response_end(req.correlation_id)
            except Exception:
                pass

    async def send_sync_response_start(self, correlation_id: str, response: SyncResponse) -> None:
        """Envoie le début de la réponse sync (status + headers)."""
        payload_json = json.dumps({
            "statusCode": response.status_code,
            "headers": response.headers,
        }).encode("utf-8")
        frame = BinaryFrame.encode(MessageType.SYNC_RESPONSE_START, correlation_id, payload_json)
        await self._send_binary(frame)

    async def send_sync_response_chunk(self, correlation_id: str, chunk: bytes) -> None:
        """Envoie un chunk du body de la réponse sync."""
        frame = BinaryFrame.encode(MessageType.SYNC_RESPONSE_CHUNK, correlation_id, chunk)
        await self._send_binary(frame)

    async def send_sync_response_end(self, correlation_id: str) -> None:
        """Signale la fin du body de la réponse sync."""
        frame = BinaryFrame.encode(MessageType.SYNC_RESPONSE_END, correlation_id, flags=BinaryFrame.FLAG_END_OF_STREAM)
        await self._send_binary(frame)

    async def send_sync_cancel(self, correlation_id: str) -> None:
        """Annule un stream sync en cours."""
        frame = BinaryFrame.encode(MessageType.SYNC_CANCEL, correlation_id)
        await self._send_binary(frame)

    # ------------------------------------------------------------------
    # Propriétés
    # ------------------------------------------------------------------

    @property
    def connection_id(self) -> Optional[str]:
        """Identifiant de connexion assigné par SlimFaas après enregistrement."""
        return self._connection_id

    @property
    def is_connected(self) -> bool:
        """True si le WebSocket est actuellement connecté et enregistré."""
        return self._ws is not None and self._connection_id is not None

