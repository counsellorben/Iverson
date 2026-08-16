"""The Python conformance driver.

Mirrors the .NET driver's shape (``Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/``):
reports, never asserts. Every step's failure is data — ``ok: false`` with an error message — and
the process still exits 0. A non-zero exit means the driver itself broke (bad flags, unsupported
scenario, unwritable ``--out``).

Invoked as ``python3 conformance/driver.py <flags>`` with cwd ``Iverson.Clients/Python``.
"""
from __future__ import annotations

import hashlib
import json
import os
import sys
from dataclasses import dataclass
from typing import Any, Dict, List, Optional
from urllib.parse import urlsplit
import uuid

# Invoked directly as `python3 conformance/driver.py` (DriverRunner.cs:97-99), so Python puts
# only this file's own directory on sys.path, not the package root above it. Without this, neither
# `iverson_client` nor `conformance` (this package, for the models import below) is importable —
# there is no PYTHONPATH set by the orchestrator and the package is not pip-installed.
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import time
import urllib.parse
import urllib.request

import grpc
from google.protobuf.json_format import MessageToJson

from iverson_client.core import EntityCoordinator, SchemaRegistrar
from iverson_client.generated import object_mapping_pb2_grpc as mapping_grpc

from conformance.models import PyArticle, PyAuthor, PyBadArticle, PyTag, SharedArticle, SharedAuthor

LANGUAGE = "python"
# naming-rejected (S2) is register-phase-only: the orchestrator never invokes this driver for
# any other phase under it. interop (S4) is register-phase-NEVER for this driver: only .NET
# registers SharedAuthor/SharedArticle (register-once rule).
SCENARIOS = {"crud-roundtrip", "naming-rejected", "interop"}


# ── Argument parsing ──────────────────────────────────────────────────────────

class Args:
    """Minimal ``--flag value`` parser, mirroring the .NET driver's ``Args``."""

    def __init__(self, argv: List[str]) -> None:
        self._values: Dict[str, str] = {}
        i = 0
        while i < len(argv):
            flag = argv[i]
            if not flag.startswith("--"):
                i += 1
                continue
            # The next argument is the value whatever it looks like: the harness always emits
            # ``--flag <value>`` pairs (empty string included), and legitimate values — a base64
            # token, a JSON blob — can begin with "--". Treating a leading "--" as "no value"
            # would silently drop them.
            if i + 1 < len(argv):
                self._values[flag] = argv[i + 1]
                i += 2
            else:
                self._values[flag] = ""
                i += 1

    def require(self, flag: str) -> str:
        value = self._values.get(flag, "")
        if not value:
            raise ValueError(f"missing required flag {flag}")
        return value

    def optional(self, flag: str) -> Optional[str]:
        value = self._values.get(flag, "")
        return value if value else None


# ── Step result / phase document ─────────────────────────────────────────────

@dataclass
class StepResult:
    name: str
    ok: bool
    error: Optional[str] = None
    type_descriptor: Optional[Any] = None
    keys: Optional[Dict[str, str]] = None
    entity: Optional[Any] = None

    def to_json(self) -> dict:
        # All keys always present (null where absent), matching the .NET driver's
        # JsonSerializerDefaults.Web output, which does not omit nulls.
        return {
            "name": self.name,
            "ok": self.ok,
            "error": self.error,
            "typeDescriptor": self.type_descriptor,
            "keys": self.keys,
            "entity": self.entity,
        }


def entity_to_dict(entity: Any) -> Optional[dict]:
    """Serializes an entity with its declared attribute names (snake_case), mirroring the .NET
    driver's choice to report what the client library actually holds rather than re-casing it."""
    if entity is None:
        return None
    out: dict = {}
    for base in reversed(type(entity).__mro__):
        if base is object:
            continue
        for name in getattr(base, "__annotations__", {}):
            out[name] = _json_safe(getattr(entity, name, None))
    return out


def _json_safe(value: Any) -> Any:
    """UUIDs are not JSON-serializable and appear both bare and inside a list (a many-to-many
    foreign key is a list of UUIDs), so the conversion has to recurse rather than test the top
    level only — `json.dump` fails the whole phase document on the first one it meets."""
    if isinstance(value, uuid.UUID):
        return str(value)
    if isinstance(value, (list, tuple)):
        return [_json_safe(item) for item in value]
    if isinstance(value, dict):
        return {key: _json_safe(item) for key, item in value.items()}
    return value


def describe(exc: Exception) -> str:
    return f"{type(exc).__name__}: {exc}"


def derive_key(id_prefix: str, logical_name: str) -> uuid.UUID:
    """Deterministic per-run key: distinct across runs because --id-prefix is. Only needs to be
    consistent within this driver's own fallback path — cross-language key equality is not
    required, since --keys is language-qualified (each language reads only its own slice)."""
    digest = hashlib.md5(f"{id_prefix}:{logical_name}".encode("utf-8")).digest()
    return uuid.UUID(bytes=digest)


def parse_keys(keys_json: Optional[str], language: str) -> Dict[str, str]:
    if not keys_json:
        return {}
    by_language = json.loads(keys_json)
    return by_language.get(language, {}) if isinstance(by_language, dict) else {}


def parse_keys_all(keys_json: Optional[str]) -> Dict[str, Dict[str, str]]:
    """The full language-qualified --keys map, unlike parse_keys which slices out one language.
    S4 interop's read phase needs every language's reported ``shared_article`` key, not just this
    driver's own."""
    if not keys_json:
        return {}
    by_language = json.loads(keys_json)
    return by_language if isinstance(by_language, dict) else {}


# ── Registration channel (public API only) ──────────────────────────────────

class _DriverBearerAuthPlugin(grpc.AuthMetadataPlugin):
    """Fetches and caches an OAuth2 client-credentials token, refreshing 60s before expiry.

    A small self-contained duplicate of the service-token machinery `IversonClient.__init__`
    builds internally (`iverson_client/auth.py`'s `_CachedTokenProvider`/`_BearerTokenAuthPlugin`,
    both underscore-prefixed and not part of the client's public surface). Kept local rather than
    importing those private names, the same way the .NET driver's `Auth.cs` builds its own
    `ServiceTokenProvider` instead of reaching into `IversonClient`/`AddIversonClient` internals.
    """

    def __init__(self, client_id: str, client_secret: str, token_endpoint: str) -> None:
        self._client_id = client_id
        self._client_secret = client_secret
        self._token_endpoint = token_endpoint
        self._token: Optional[str] = None
        self._expires_at: float = 0.0

    def __call__(self, context, callback) -> None:
        try:
            token = self._get_token()
            callback((("authorization", f"Bearer {token}"),), None)
        except Exception as exc:  # noqa: BLE001
            callback(None, exc)

    def _get_token(self) -> str:
        if self._token is not None and time.monotonic() < self._expires_at:
            return self._token
        params = {
            "grant_type": "client_credentials",
            "client_id": self._client_id,
            "client_secret": self._client_secret,
        }
        body = urllib.parse.urlencode(params).encode("utf-8")
        request = urllib.request.Request(
            self._token_endpoint,
            data=body,
            headers={"Content-Type": "application/x-www-form-urlencoded"},
            method="POST",
        )
        with urllib.request.urlopen(request) as response:
            payload = json.loads(response.read())
        self._token = payload["access_token"]
        self._expires_at = time.monotonic() + payload["expires_in"] - 60
        return self._token


class _DriverStaticBearerAuthPlugin(grpc.AuthMetadataPlugin):
    """Attaches an already-minted service token to every call.

    Preferred over `_DriverBearerAuthPlugin` whenever the orchestrator supplies one. Authentik
    stamps the JWT's `iss` from the request's Host header and grants scopes only when the token
    request asks for them; neither is expressible through `IversonClientCredentials`, so a token
    this driver minted for itself is rejected by the API on issuer validation (401) and carries
    no `schema_admin` scope (403 on RegisterSchema). The orchestrator mints one correctly and
    passes it via --service-token.
    """

    def __init__(self, token: str) -> None:
        self._token = token

    def __call__(self, context, callback) -> None:
        try:
            callback((("authorization", f"Bearer {self._token}"),), None)
        except Exception as exc:  # noqa: BLE001
            callback(None, exc)


class _DriverActingUserAuthPlugin(grpc.AuthMetadataPlugin):
    """Attaches a pre-minted acting-user token to every call on this channel."""

    def __init__(self, token: str) -> None:
        self._token = token

    def __call__(self, context, callback) -> None:
        try:
            callback((("x-acting-user-authorization", f"Bearer {self._token}"),), None)
        except Exception as exc:  # noqa: BLE001
            callback(None, exc)


def build_driver_channel(
    host: str,
    port: int,
    client_id: Optional[str],
    client_secret: Optional[str],
    token_endpoint: Optional[str],
    acting_token: Optional[str],
    service_token: Optional[str] = None,
) -> grpc.Channel:
    """Builds the driver's single channel, carrying both identities, from public `grpc` API only.

    The capture wrapper needs a real stub to forward to and `EntityCoordinator` takes a channel
    directly, so one channel serves both. It mirrors the
    composite-credentials-over-`local_channel_credentials()` pattern `IversonClient.__init__`
    uses for h2c (`core.py:655-682`): a bare insecure channel rejects `CallCredentials`
    outright, so some `ChannelCredentials` is always required as the base when either identity
    is present.
    """
    address = f"{host}:{port}"
    call_creds = []
    if service_token:
        call_creds.append(
            grpc.metadata_call_credentials(_DriverStaticBearerAuthPlugin(service_token))
        )
    elif client_id and client_secret and token_endpoint:
        call_creds.append(
            grpc.metadata_call_credentials(
                _DriverBearerAuthPlugin(client_id, client_secret, token_endpoint)
            )
        )
    if acting_token:
        call_creds.append(grpc.metadata_call_credentials(_DriverActingUserAuthPlugin(acting_token)))

    if not call_creds:
        return grpc.insecure_channel(address)

    channel_creds = grpc.composite_channel_credentials(
        grpc.local_channel_credentials(), *call_creds
    )
    return grpc.secure_channel(address, channel_creds)


# ── Descriptor capture ────────────────────────────────────────────────────────

class CapturingMappingStub:
    """Wraps the mapping stub passed into ``SchemaRegistrar`` — the sanctioned capture seam per
    the plan: ``SchemaRegistrar(mapping_stub, *entity_classes)`` takes the stub as a public
    constructor parameter. Records the outgoing ``SchemaRequest.root_type`` of every registration
    call (before forwarding, so it is captured even if the RPC itself fails) and forwards
    unchanged. Nothing is judged here — the JSON is reported verbatim."""

    def __init__(self, stub: mapping_grpc.ObjectMappingServiceStub) -> None:
        self._stub = stub
        self._captured: List[tuple] = []

    def RegisterSchema(self, request, *args, **kwargs):
        self._captured.append((
            request.root_type.type_name,
            MessageToJson(request.root_type, always_print_fields_with_no_presence=True),
        ))
        return self._stub.RegisterSchema(request, *args, **kwargs)

    def select(self, *preferred_type_names: Optional[str]) -> Optional[str]:
        """The descriptor for the first of ``preferred_type_names`` actually sent under that
        exact name, or None if none of them was. Never substitutes a different type's
        descriptor."""
        for preferred in preferred_type_names:
            if not preferred:
                continue
            for type_name, js in self._captured:
                if type_name.lower() == preferred.lower():
                    return js
        return None


def main(argv: List[str]) -> int:
    args = Args(argv)

    scenario = args.require("--scenario")
    if scenario not in SCENARIOS:
        print(
            f"unsupported scenario '{scenario}'; this driver implements {sorted(SCENARIOS)}",
            file=sys.stderr,
        )
        return 2

    phase = args.require("--phase")
    tenant = args.require("--tenant")
    owner_id = args.require("--owner-id")
    id_prefix = args.require("--id-prefix")
    out_path = args.require("--out")
    type_hint = args.optional("--type")

    grpc_addr = args.require("--grpc")
    parsed = urlsplit(grpc_addr if "//" in grpc_addr else f"//{grpc_addr}")
    host = parsed.hostname or "localhost"
    port = parsed.port or 5000

    client_id = args.optional("--client-id")
    client_secret = args.optional("--client-secret")
    token_endpoint = args.optional("--token-endpoint")
    acting_token = args.optional("--acting-token")

    service_token = args.optional("--service-token")

    # One channel, built entirely from public `grpc` API, carrying both identities and shared by
    # the registration stub and every coordinator. `IversonClient` is deliberately not used: it
    # accepts only an `IversonClientCredentials`, which can express neither the Host header
    # Authentik derives the token's issuer from nor a scope, so it cannot carry the
    # orchestrator's pre-minted service token. This mirrors the .NET driver, which likewise
    # builds its own invoker rather than going through `AddIversonClient`; `EntityCoordinator` —
    # the actual subject — is public and takes a channel directly, exactly as
    # `IversonClient.coordinator` constructs it.
    channel = build_driver_channel(
        host, port, client_id, client_secret, token_endpoint, acting_token, service_token,
    )

    def coordinator(entity_class: type) -> EntityCoordinator:
        return EntityCoordinator(entity_class, channel)

    capture = CapturingMappingStub(mapping_grpc.ObjectMappingServiceStub(channel))

    prior_keys = parse_keys(args.optional("--keys"), LANGUAGE)

    def key_for(logical_name: str) -> uuid.UUID:
        existing = prior_keys.get(logical_name)
        if existing:
            try:
                return uuid.UUID(existing)
            except ValueError:
                pass
        return derive_key(id_prefix, logical_name)

    steps: List[StepResult] = []

    if phase == "register" and scenario == "naming-rejected":
        # PyBadArticle's writer_id member fails SchemaRegistrar's naming check before any
        # RegisterSchema call is issued — the capture stub never sees a request to record, so
        # there is no type_descriptor to report either.
        error: Optional[str] = None
        try:
            registrar = SchemaRegistrar(capture, PyBadArticle)
            registrar.register_all()
        except Exception as exc:  # noqa: BLE001 - reported as data, not raised
            error = describe(exc)
        steps.append(StepResult(name="register", ok=error is None, error=error))

    elif phase == "register":
        # SchemaRegistrar.register_all() issues one RegisterSchema call per type, sequentially,
        # and raises on the first failure (RuntimeError on Success=false, or the underlying
        # RpcException on a transport failure) — so the sequence aborts at the first failing
        # type. All three steps share that aborted sequence's outcome; `typeDescriptor` presence
        # (recorded by CapturingMappingStub before each call is sent) is what tells the
        # orchestrator which types were actually sent.
        error: Optional[str] = None
        try:
            # Author, then tag, then article — the same order in all five drivers, so the types
            # the article's relations reference already exist when the article is sent.
            # Registration aborts at the first failure, so the order is observable.
            registrar = SchemaRegistrar(capture, PyAuthor, PyTag, PyArticle)
            registrar.register_all()
        except Exception as exc:  # noqa: BLE001 - reported as data, not raised
            error = describe(exc)

        def add_register_step(name: str, descriptor_json: Optional[str]) -> None:
            steps.append(StepResult(
                name=name,
                ok=error is None,
                error=error,
                type_descriptor=json.loads(descriptor_json) if descriptor_json else None,
            ))

        add_register_step("register", capture.select(type_hint, "PyArticle"))
        add_register_step("register_author", capture.select("PyAuthor"))
        add_register_step("register_tag", capture.select("PyTag"))

    elif phase == "write" and scenario == "interop":
        # S4 interop: writes SharedAuthor then SharedArticle, reporting keys "shared_author" and
        # "shared_article".
        shared_keys: dict = {"shared_author": None, "shared_article": None}

        def write_shared_author() -> StepResult:
            entity = SharedAuthor()
            entity.tenant_id = tenant
            entity.owner_id = owner_id
            entity.name = f"shared-author-{id_prefix}"
            shared_keys["shared_author"] = coordinator(SharedAuthor).persist(entity)
            return StepResult("write_shared_author", True, entity=entity_to_dict(entity))

        def write_shared_article() -> StepResult:
            entity = SharedArticle()
            entity.tenant_id = tenant
            entity.owner_id = owner_id
            entity.title = f"shared-title-{id_prefix}"
            if shared_keys["shared_author"] is not None:
                entity.shared_author_id = uuid.UUID(shared_keys["shared_author"])
            shared_keys["shared_article"] = coordinator(SharedArticle).persist(entity)
            return StepResult("write_shared_article", True, entity=entity_to_dict(entity))

        for name, body, key_name in (
            ("write_shared_author", write_shared_author, "shared_author"),
            ("write_shared_article", write_shared_article, "shared_article"),
        ):
            try:
                result = body()
            except Exception as exc:  # noqa: BLE001
                result = StepResult(name, False, error=describe(exc))
            if shared_keys[key_name] is not None:
                result.keys = {key_name: str(shared_keys[key_name])}
            steps.append(result)

    elif phase == "read" and scenario == "interop":
        # Iterates every language's reported "shared_article" key from the full --keys map (not
        # just this driver's own slice), so this one driver invocation reads all five languages'
        # rows — the fan-out that produces 25 reads across the five drivers.
        all_keys = parse_keys_all(args.optional("--keys"))
        for writer_language in sorted(all_keys):
            key = all_keys[writer_language].get("shared_article")
            if not key:
                continue
            name = f"read_shared_article_{writer_language}"
            try:
                article = coordinator(SharedArticle).get(key)
                steps.append(StepResult(name, True, entity=entity_to_dict(article)))
            except Exception as exc:  # noqa: BLE001
                steps.append(StepResult(name, False, error=describe(exc)))

    elif phase == "write":
        # Keys are server-assigned: create requests must omit id entirely, and each row's key is
        # only known — and only reported — once persist() returns it. author_key/tag_key are
        # populated by the closures below and read by write_article, which runs after them.
        keys: dict = {"author": None, "tag": None, "article": None}

        def write_author() -> StepResult:
            entity = PyAuthor()
            entity.tenant_id = tenant
            entity.owner_id = owner_id
            entity.name = f"author-{id_prefix}"
            keys["author"] = coordinator(PyAuthor).persist(entity)
            return StepResult("write_author", True, entity=entity_to_dict(entity))

        def write_tag() -> StepResult:
            entity = PyTag()
            entity.tenant_id = tenant
            entity.owner_id = owner_id
            entity.label = f"tag-{id_prefix}"
            keys["tag"] = coordinator(PyTag).persist(entity)
            return StepResult("write_tag", True, entity=entity_to_dict(entity))

        def write_article() -> StepResult:
            entity = PyArticle()
            entity.tenant_id = tenant
            entity.owner_id = owner_id
            entity.title = f"title-{id_prefix}"
            if keys["author"] is not None:
                entity.py_author_id = uuid.UUID(keys["author"])
            if keys["tag"] is not None:
                entity.py_tag_ids = [uuid.UUID(keys["tag"])]
                entity.py_tag_id = uuid.UUID(keys["tag"])
            keys["article"] = coordinator(PyArticle).persist(entity)
            return StepResult("write_article", True, entity=entity_to_dict(entity))

        # One step per row: a denied or failed write must not abort the others. A row's key is
        # reported only when its write actually returned one — there is no client-derived key to
        # fall back to any more.
        for name, body, key_name in (
            ("write_author", write_author, "author"),
            ("write_tag", write_tag, "tag"),
            ("write_article", write_article, "article"),
        ):
            try:
                result = body()
            except Exception as exc:  # noqa: BLE001
                result = StepResult(name, False, error=describe(exc))
            if keys[key_name] is not None:
                result.keys = {key_name: str(keys[key_name])}
            steps.append(result)

    elif phase == "read":
        # Two gets at depth 0 (EntityCoordinator.get performs no relation traversal), reported
        # separately so a failure on one is not conflated with the other.
        try:
            article = coordinator(PyArticle).get(str(key_for("article")))
            steps.append(StepResult("get", True, entity=entity_to_dict(article)))
        except Exception as exc:  # noqa: BLE001
            steps.append(StepResult("get", False, error=describe(exc)))

        try:
            author = coordinator(PyAuthor).get(str(key_for("author")))
            steps.append(StepResult("get_author", True, entity=entity_to_dict(author)))
        except Exception as exc:  # noqa: BLE001
            steps.append(StepResult("get_author", False, error=describe(exc)))

        # IVC-LIFE-006/IVC-LIFE-007: a depth-1 read through this driver's OWN client library,
        # reported as its own step — proves the CLIENT can express the request (LIFE-006) and
        # materialize the hydrated result (LIFE-007), distinct from the orchestrator's own
        # depth-1 MappingGet which only proves the SERVER hydrates.
        try:
            article_depth1 = coordinator(PyArticle).get_mapped(str(key_for("article")), depth=1)
            steps.append(StepResult("get_depth1", True, entity=entity_to_dict(article_depth1)))
        except Exception as exc:  # noqa: BLE001
            steps.append(StepResult("get_depth1", False, error=describe(exc)))

    elif phase == "update":
        try:
            entity = PyArticle()
            entity.id = key_for("article")
            entity.tenant_id = tenant
            entity.owner_id = owner_id
            entity.title = f"title-{id_prefix}-updated"
            entity.py_author_id = key_for("author")
            entity.py_tag_ids = [key_for("tag")]
            entity.py_tag_id = key_for("tag")
            # EntityCoordinator.update() returns nothing (unlike .NET's UpdateMappedAsync, which
            # returns the server's response entity) — the entity reported here is what the driver
            # sent, which is the only observable this API surface offers.
            coordinator(PyArticle).update(entity)
            steps.append(StepResult("update", True, entity=entity_to_dict(entity)))
        except Exception as exc:  # noqa: BLE001
            steps.append(StepResult("update", False, error=describe(exc)))

    elif phase == "delete":
        delete_key = str(key_for("article"))

        try:
            coordinator(PyArticle).delete(delete_key)
            steps.append(StepResult("delete", True))
        except Exception as exc:  # noqa: BLE001
            steps.append(StepResult("delete", False, error=describe(exc)))

        # The read-back is its own step, carrying `entity` (None when nothing came back) and the
        # client's own error text when the read itself fails — a null entity alone cannot
        # distinguish "gone" from "read denied" from a transport error.
        try:
            after = coordinator(PyArticle).get(delete_key)
            steps.append(StepResult("get_after_delete", True, entity=entity_to_dict(after)))
        except Exception as exc:  # noqa: BLE001
            steps.append(StepResult("get_after_delete", False, error=describe(exc)))

    else:
        channel.close()
        print(f"unknown phase '{phase}'", file=sys.stderr)
        return 2

    document = {"language": LANGUAGE, "phase": phase, "steps": [s.to_json() for s in steps]}
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(document, f)

    channel.close()
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
