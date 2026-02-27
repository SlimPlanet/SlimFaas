"""
slimfaas-client
===============

Librairie Python pour connecter des Jobs ou fonctions virtuelles à SlimFaas via WebSocket.

Chaque instance de `SlimFaasClient` se connecte à SlimFaas et se déclare comme un "replica virtuel"
d'une fonction donnée. SlimFaas peut ensuite lui envoyer des requêtes asynchrones et des évènements
publish/subscribe au lieu de faire des appels HTTP.

Exemple d'utilisation :

    import asyncio
    from slimfaas_client import SlimFaasClient, SlimFaasClientConfig, AsyncRequest, PublishEvent

    async def handle_request(req: AsyncRequest) -> int:
        print(f"Received async request: {req.method} {req.path}")
        # Traitement ...
        return 200  # code HTTP à renvoyer

    async def handle_event(evt: PublishEvent) -> None:
        print(f"Received event: {evt.event_name}")

    async def main():
        config = SlimFaasClientConfig(
            function_name="my-job",
            subscribe_events=["order-created"],
            default_visibility="Public",
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
    PublishEvent,
    SlimFaasClientConfig,
)

__all__ = [
    "SlimFaasClient",
    "SlimFaasClientConfig",
    "AsyncRequest",
    "AsyncCallback",
    "PublishEvent",
]

