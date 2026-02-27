"""
Modèles de données pour le protocole WebSocket SlimFaas.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import IntEnum
from typing import Optional


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

    subscribe_events: list[str] = field(default_factory=list)
    """Noms des évènements auxquels s'abonner (SlimFaas/SubscribeEvents)."""

    default_visibility: str = "Public"
    """Visibilité par défaut : "Public" ou "Private" (SlimFaas/DefaultVisibility)."""

    paths_start_with_visibility: dict[str, str] = field(default_factory=dict)
    """Visibilité par préfixe de chemin (SlimFaas/PathsStartWithVisibility)."""

    configuration: str = ""
    """Configuration JSON libre de la fonction (SlimFaas/Configuration)."""

    replicas_start_as_soon_as_one_function_retrieve_a_request: bool = False
    """SlimFaas/ReplicasStartAsSoonAsOneFunctionRetrieveARequest."""

    number_parallel_request: int = 10
    """Nombre maximum de requêtes parallèles (SlimFaas/NumberParallelRequest)."""

    number_parallel_request_per_pod: int = 10
    """Nombre maximum de requêtes parallèles par pod (SlimFaas/NumberParallelRequestPerPod)."""

    default_trust: str = "Trusted"
    """Niveau de confiance : "Trusted" ou "Untrusted" (SlimFaas/DefaultTrust)."""

    def to_register_payload(self) -> dict:
        return {
            "functionName": self.function_name,
            "configuration": {
                "dependsOn": self.depends_on,
                "subscribeEvents": self.subscribe_events,
                "defaultVisibility": self.default_visibility,
                "pathsStartWithVisibility": self.paths_start_with_visibility,
                "configuration": self.configuration,
                "replicasStartAsSoonAsOneFunctionRetrieveARequest": self.replicas_start_as_soon_as_one_function_retrieve_a_request,
                "numberParallelRequest": self.number_parallel_request,
                "numberParallelRequestPerPod": self.number_parallel_request_per_pod,
                "defaultTrust": self.default_trust,
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

