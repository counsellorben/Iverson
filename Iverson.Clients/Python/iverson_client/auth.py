"""OAuth2 client-credentials support for IversonClient."""

from __future__ import annotations

import json
import threading
import time
import urllib.parse
import urllib.request
from dataclasses import dataclass
from urllib.error import HTTPError

import grpc


@dataclass(frozen=True)
class IversonClientCredentials:
    client_id: str
    client_secret: str
    token_endpoint: str
    scope: str | None = None


class _CachedTokenProvider:
    """Fetches and caches an OAuth2 client-credentials access token, refreshing
    60 seconds before expiry."""

    def __init__(self, credentials: IversonClientCredentials) -> None:
        self._credentials = credentials
        self._lock = threading.Lock()
        self._token: str | None = None
        self._expires_at: float = 0.0

    def get_token(self) -> str:
        if self._token is not None and time.monotonic() < self._expires_at:
            return self._token

        with self._lock:
            if self._token is not None and time.monotonic() < self._expires_at:
                return self._token

            params = {
                "grant_type": "client_credentials",
                "client_id": self._credentials.client_id,
                "client_secret": self._credentials.client_secret,
            }
            if self._credentials.scope:
                params["scope"] = self._credentials.scope
            body = urllib.parse.urlencode(params).encode("utf-8")
            request = urllib.request.Request(
                self._credentials.token_endpoint,
                data=body,
                headers={"Content-Type": "application/x-www-form-urlencoded"},
                method="POST",
            )
            try:
                with urllib.request.urlopen(request) as response:
                    payload = json.loads(response.read())
            except HTTPError as e:
                raise RuntimeError(f"Failed to acquire Iverson client token: HTTP {e.code}") from e

            self._token = payload["access_token"]
            self._expires_at = time.monotonic() + payload["expires_in"] - 60
            return self._token


class _BearerTokenAuthPlugin(grpc.AuthMetadataPlugin):
    def __init__(self, provider: _CachedTokenProvider) -> None:
        self._provider = provider

    def __call__(self, context, callback) -> None:
        try:
            token = self._provider.get_token()
            callback((("authorization", f"Bearer {token}"),), None)
        except Exception as e:
            callback(None, e)


ACTING_USER_METADATA_KEY = "x-acting-user-authorization"


def acting_user_metadata(token: str) -> tuple[tuple[str, str], ...]:
    """Per-call metadata tuple carrying the acting-user's own Authentik-issued
    token. Pass via a stub call's `metadata=` kwarg alongside the service
    credential, e.g. `stub.Search(request, metadata=acting_user_metadata(token))`.
    """
    return ((ACTING_USER_METADATA_KEY, f"Bearer {token}"),)
