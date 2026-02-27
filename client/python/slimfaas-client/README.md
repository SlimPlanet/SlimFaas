# slimfaas-client

Client Python pour se connecter à **SlimFaas** via WebSocket. Permet à des Jobs ou fonctions virtuelles de recevoir des requêtes asynchrones et des évènements publish/subscribe sans exposer de port HTTP.

## Prérequis

- Python ≥ 3.10
- [UV](https://docs.astral.sh/uv/) comme gestionnaire de packages

## Installation

```bash
uv add slimfaas-client
# ou depuis les sources
uv pip install -e .
```

## Utilisation

### Connexion de base

```python
import asyncio
from slimfaas_client import SlimFaasClient, SlimFaasClientConfig, AsyncRequest, PublishEvent

async def handle_request(req: AsyncRequest) -> int:
    """
    Appelé quand SlimFaas envoie une requête async-function.
    Retourne le code HTTP (200 = succès, 500 = erreur, 202 = long processing).
    """
    print(f"{req.method} {req.path}{req.query}")
    print(f"Body: {req.body}")
    # ... traitement ...
    return 200

async def handle_event(evt: PublishEvent) -> None:
    """Appelé quand SlimFaas publie un évènement (publish-event)."""
    print(f"Event '{evt.event_name}': {evt.body}")

async def main():
    config = SlimFaasClientConfig(
        function_name="my-job",
        subscribe_events=["order-created", "order-updated"],
        default_visibility="Public",
        number_parallel_request=5,
    )

    async with SlimFaasClient("ws://slimfaas:5003/ws", config) as client:
        client.on_async_request(handle_request)
        client.on_publish_event(handle_event)
        await client.run_forever()

asyncio.run(main())
```

### Configuration complète

```python
config = SlimFaasClientConfig(
    function_name="my-job",

    # SlimFaas/DependsOn
    depends_on=["other-function"],

    # SlimFaas/SubscribeEvents
    subscribe_events=["my-event"],

    # SlimFaas/DefaultVisibility
    default_visibility="Public",  # ou "Private"

    # SlimFaas/PathsStartWithVisibility
    paths_start_with_visibility={"/admin": "Private"},

    # SlimFaas/Configuration
    configuration='{"key": "value"}',

    # SlimFaas/ReplicasStartAsSoonAsOneFunctionRetrieveARequest
    replicas_start_as_soon_as_one_function_retrieve_a_request=True,

    # SlimFaas/NumberParallelRequest
    number_parallel_request=10,

    # SlimFaas/NumberParallelRequestPerPod
    number_parallel_request_per_pod=5,

    # SlimFaas/DefaultTrust
    default_trust="Trusted",  # ou "Untrusted"
)
```

### Traitement long (status 202)

Si le traitement prend du temps, retournez `202` et envoyez le résultat plus tard :

```python
async def handle_long_request(req: AsyncRequest) -> int:
    asyncio.create_task(process_in_background(client, req))
    return 202  # "Je m'en occupe, je rappellerai"

async def process_in_background(client: SlimFaasClient, req: AsyncRequest) -> None:
    await asyncio.sleep(10)  # Traitement long...
    await client.send_callback(req.element_id, 200)
```

## Règles importantes

1. **Un client ne peut pas utiliser le même `function_name` qu'une fonction Kubernetes existante.** SlimFaas refusera l'enregistrement avec une `SlimFaasRegistrationError`.

2. **Tous les clients avec le même `function_name` doivent avoir la même configuration.** Si deux instances se connectent avec des configurations différentes, la deuxième sera refusée.

## Reconnexion automatique

Le client se reconnecte automatiquement après une déconnexion. Le délai entre reconnexions est configurable :

```python
client = SlimFaasClient("ws://...", config, reconnect_delay=10.0)
```

## Développement

```bash
uv sync --extra dev
uv run pytest
```

