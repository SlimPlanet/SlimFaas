"""
Exemple d'utilisation de slimfaas-client.

Ce script montre comment connecter un Job Python à SlimFaas via WebSocket
pour recevoir des requêtes async-function et des évènements publish-event.

Lancement :
    uv run examples/job_example.py
"""

from __future__ import annotations

import asyncio
import json
import logging

from slimfaas_client import SlimFaasClient, SlimFaasClientConfig, AsyncRequest, PublishEvent

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)-8s %(message)s")
logger = logging.getLogger(__name__)


# ---------------------------------------------------------------------------
# Handlers
# ---------------------------------------------------------------------------

async def handle_request(req: AsyncRequest) -> int:
    """
    Traite une requête asynchrone envoyée par SlimFaas.

    Retourner 200 = succès (SlimFaas acquittera la queue).
    Retourner 500 = erreur (SlimFaas retentera si configuré).
    Retourner 202 = traitement long en cours (le client appellera send_callback()).
    """
    logger.info(
        "AsyncRequest: %s %s%s | elementId=%s | body=%s bytes",
        req.method,
        req.path,
        req.query,
        req.element_id,
        len(req.body) if req.body else 0,
    )

    # Exemple : parser le corps JSON
    if req.body:
        try:
            data = json.loads(req.body)
            logger.info("Body payload: %s", data)
        except json.JSONDecodeError:
            logger.warning("Body is not JSON")

    # Traitement simulé
    await asyncio.sleep(0.1)

    return 200  # Succès


async def handle_event(evt: PublishEvent) -> None:
    """Traite un évènement publish/subscribe."""
    logger.info(
        "PublishEvent: '%s' | %s %s%s | body=%s bytes",
        evt.event_name,
        evt.method,
        evt.path,
        evt.query,
        len(evt.body) if evt.body else 0,
    )


# ---------------------------------------------------------------------------
# Point d'entrée
# ---------------------------------------------------------------------------

async def main() -> None:
    config = SlimFaasClientConfig(
        # Nom du job (ne doit PAS être le nom d'une fonction Kubernetes existante)
        function_name="my-python-job",

        # SlimFaas/SubscribeEvents : écoute ces évènements publish-event
        subscribe_events=["order-created", "order-updated"],

        # SlimFaas/DefaultVisibility
        default_visibility="Public",

        # SlimFaas/NumberParallelRequest
        number_parallel_request=5,

        # SlimFaas/DefaultTrust
        default_trust="Trusted",
    )

    # URL du port WebSocket de SlimFaas (par défaut 5003)
    ws_url = "ws://localhost:5003/ws"

    logger.info("Connecting to SlimFaas at %s with function '%s'…", ws_url, config.function_name)

    async with SlimFaasClient(ws_url, config, reconnect_delay=5.0) as client:
        client.on_async_request(handle_request)
        client.on_publish_event(handle_event)

        logger.info("Client ready. Waiting for messages…")
        await client.run_forever()


if __name__ == "__main__":
    asyncio.run(main())

