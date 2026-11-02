"""
Request signing tests. The vector is shared with the SlimFaas server tests
(RequestSignatureTests) and the .NET client tests: all three must agree.
"""

from __future__ import annotations

import pytest

from slimfaas_client import CallerCredentials, sign_request
from slimfaas_client._signing import (
    CALLER_HEADER,
    CONTENT_SHA256_HEADER,
    NONCE_HEADER,
    SIGNATURE_HEADER,
    TIMESTAMP_HEADER,
    UNSIGNED_PAYLOAD,
    canonical_query,
    canonical_string,
    sha256_hex,
)

KEY = b"0123456789abcdef0123456789abcdef"
TIMESTAMP = 1700000000
NONCE = "n0nce-123"
BODY_SHA256 = "3ff6698e101869f36e088516c6c0ca6495c40c0abdae72f6e4d124610dace7b0"
POST_SIGNATURE = "10+5lpxIbQ7lN9xIbfdF5o7/2h+R7I+4CFNuXX7VfTA="
GET_SIGNATURE = "I3pNGgh8gvq55dctrB99cJpJQoxQ1+rOVd7dR9lIWMc="
EMPTY_SHA256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"


def credentials() -> CallerCredentials:
    return CallerCredentials("billing-api", KEY)


def test_shared_post_vector():
    headers = sign_request(
        credentials(),
        "post",
        "http://slimfaas:5000/function/fibonacci/compute?b=2&a=1&a=%5B0%5D",
        b'{"n":10}',
        timestamp=TIMESTAMP,
        nonce=NONCE,
    )

    assert headers[CALLER_HEADER] == "billing-api"
    assert headers[TIMESTAMP_HEADER] == "1700000000"
    assert headers[NONCE_HEADER] == NONCE
    assert headers[CONTENT_SHA256_HEADER] == BODY_SHA256
    assert headers[SIGNATURE_HEADER] == POST_SIGNATURE


def test_shared_post_vector_from_path_only_and_str_body():
    headers = sign_request(
        credentials(),
        "POST",
        "/function/fibonacci/compute?b=2&a=1&a=%5B0%5D",
        '{"n":10}',
        timestamp=TIMESTAMP,
        nonce=NONCE,
    )

    assert headers[SIGNATURE_HEADER] == POST_SIGNATURE


def test_shared_get_vector():
    headers = sign_request(
        credentials(), "GET", "/function/fibonacci/health", None, timestamp=TIMESTAMP, nonce=NONCE
    )

    assert headers[CONTENT_SHA256_HEADER] == EMPTY_SHA256
    assert headers[SIGNATURE_HEADER] == GET_SIGNATURE


def test_canonical_string_layout():
    canonical = canonical_string(
        "post",
        "/function/fibonacci/compute",
        [("b", "2"), ("a", "1"), ("a", "[0]")],
        "billing-api",
        "1700000000",
        NONCE,
        sha256_hex(b'{"n":10}'),
    )

    assert canonical == (
        "SLIMFAAS-HMAC-SHA256\nPOST\n/function/fibonacci/compute\na=%5B0%5D&a=1&b=2\n"
        "billing-api\n1700000000\nn0nce-123\n" + BODY_SHA256
    )


def test_canonical_query_encodes_rfc3986_and_sorts():
    assert (
        canonical_query([("tilde", "~"), ("b", "2"), ("sp ace", "x y"), ("a", "1"), ("a", "[0]")])
        == "a=%5B0%5D&a=1&b=2&sp%20ace=x%20y&tilde=~"
    )


def test_unsigned_payload_skips_the_body():
    headers = sign_request(credentials(), "POST", "/function/upload", b"x" * 1024, sign_body=False)

    assert headers[CONTENT_SHA256_HEADER] == UNSIGNED_PAYLOAD


def test_generated_timestamp_and_nonce_are_fresh():
    first = sign_request(credentials(), "GET", "/function/x")
    second = sign_request(credentials(), "GET", "/function/x")

    assert first[NONCE_HEADER] != second[NONCE_HEADER]
    assert first[TIMESTAMP_HEADER].isdigit()


def test_from_file_strips_trailing_whitespace(tmp_path):
    key_file = tmp_path / "billing-api"
    key_file.write_bytes(KEY + b"\r\n")

    assert CallerCredentials.from_file("billing-api", str(key_file)).key == KEY


@pytest.mark.parametrize("caller_id", ["", "-api", "Billing", "api.next", "a" * 64])
def test_invalid_caller_ids_are_rejected(caller_id):
    with pytest.raises(ValueError):
        CallerCredentials(caller_id, KEY)


def test_short_keys_are_rejected():
    with pytest.raises(ValueError):
        CallerCredentials("billing-api", b"short")
