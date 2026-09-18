"""
HMAC-SHA256 request signing for Private SlimFaas functions (issue #408).

SlimFaas accepts the signature when ``SlimFaas:CallerAuthentication:Mode`` is
``Hybrid`` or ``Strict``; in ``Legacy`` mode the headers are ignored.

Canonical string (lines joined with ``\\n``)::

    SLIMFAAS-HMAC-SHA256
    <HTTP method, upper case>
    <decoded path>
    <canonical query: RFC 3986-encoded key=value pairs sorted, joined with &>
    <caller-id>
    <timestamp, Unix seconds>
    <nonce>
    <lower-case hex SHA-256 of the body, or UNSIGNED-PAYLOAD>

Signature = base64(HMAC-SHA256(key, canonical string)).
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import re
import secrets
import time
from dataclasses import dataclass
from typing import Iterable, Mapping, Optional, Tuple, Union
from urllib.parse import parse_qsl, quote, unquote, urlsplit

ALGORITHM = "SLIMFAAS-HMAC-SHA256"
CALLER_HEADER = "X-SlimFaas-Caller"
TIMESTAMP_HEADER = "X-SlimFaas-Timestamp"
NONCE_HEADER = "X-SlimFaas-Nonce"
CONTENT_SHA256_HEADER = "X-SlimFaas-Content-Sha256"
SIGNATURE_HEADER = "X-SlimFaas-Signature"
UNSIGNED_PAYLOAD = "UNSIGNED-PAYLOAD"

MINIMUM_KEY_BYTES = 16
_CALLER_ID = re.compile(r"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")

QueryPairs = Iterable[Tuple[str, str]]


@dataclass(frozen=True)
class CallerCredentials:
    """A ``caller-id`` and the shared key SlimFaas holds under ``<SecretsDirectory>/<caller-id>``."""

    caller_id: str
    key: bytes

    def __post_init__(self) -> None:
        if not _CALLER_ID.match(self.caller_id):
            raise ValueError(
                "caller_id must be 1 to 63 characters among a-z, 0-9 and '-', with no leading or trailing dash"
            )
        if len(self.key) < MINIMUM_KEY_BYTES:
            raise ValueError(f"key must be at least {MINIMUM_KEY_BYTES} bytes")

    @classmethod
    def from_file(cls, caller_id: str, key_file_path: str) -> "CallerCredentials":
        """Reads the key from a file (a mounted Kubernetes Secret). Trailing whitespace is ignored."""
        with open(key_file_path, "rb") as handle:
            content = handle.read()
        return cls(caller_id, content.rstrip(b"\r\n\t "))


def sha256_hex(body: bytes) -> str:
    """Lower-case hex SHA-256 of a body, as expected in ``X-SlimFaas-Content-Sha256``."""
    return hashlib.sha256(body).hexdigest()


def canonical_query(query: QueryPairs) -> str:
    pairs = [f"{quote(k, safe='-_.~')}={quote(v, safe='-_.~')}" for k, v in query]
    pairs.sort()
    return "&".join(pairs)


def canonical_string(
    method: str,
    path: str,
    query: QueryPairs,
    caller_id: str,
    timestamp: str,
    nonce: str,
    content_sha256: str,
) -> str:
    return "\n".join(
        [ALGORITHM, method.upper(), path, canonical_query(query), caller_id, timestamp, nonce, content_sha256]
    )


def sign_request(
    credentials: CallerCredentials,
    method: str,
    url: str,
    body: Union[bytes, str, None] = b"",
    *,
    sign_body: bool = True,
    timestamp: Optional[int] = None,
    nonce: Optional[str] = None,
) -> Mapping[str, str]:
    """
    Returns the five signature headers for a request.

    ``url`` is an absolute URL or a path with an optional query string. With
    ``sign_body=False`` the body is declared ``UNSIGNED-PAYLOAD`` (streamed or larger than
    the SlimFaas ``MaxSignedBodyBytes`` limit) and is not covered by the signature.
    """
    parts = urlsplit(url)
    path = unquote(parts.path) or "/"
    query = parse_qsl(parts.query, keep_blank_values=True)

    if not sign_body:
        content_sha256 = UNSIGNED_PAYLOAD
    else:
        if body is None:
            body = b""
        if isinstance(body, str):
            body = body.encode("utf-8")
        content_sha256 = sha256_hex(body)

    ts = str(int(time.time()) if timestamp is None else timestamp)
    n = nonce if nonce is not None else secrets.token_hex(16)
    canonical = canonical_string(method, path, query, credentials.caller_id, ts, n, content_sha256)
    signature = base64.b64encode(hmac.new(credentials.key, canonical.encode("utf-8"), hashlib.sha256).digest())

    return {
        CALLER_HEADER: credentials.caller_id,
        TIMESTAMP_HEADER: ts,
        NONCE_HEADER: n,
        CONTENT_SHA256_HEADER: content_sha256,
        SIGNATURE_HEADER: signature.decode("ascii"),
    }
