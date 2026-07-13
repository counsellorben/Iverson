#!/usr/bin/env python3
"""Mint an Authentik access token for the acting-user (end-user identity
propagation) smoke test, by driving the flow-executor API as a real human
login would: identification -> password -> MFA (TOTP; enrolling on first
run, solving on every run after) -> Authorization Code + PKCE against the
`iverson-loadtest-human` OAuth2 client.

Prints the resulting access token to stdout on success. Everything else
(progress, diagnostics) goes to stderr, so this script's stdout can be
piped straight into a file/variable, e.g.:

    python3 deploy/scripts/mint_acting_user_token.py --target compose \
        > /tmp/acting-user-token.txt

Two deployment targets are supported, since they differ in base URL,
client_id, and both require a Host-header override so Authentik's
per-request issuer-claim derivation matches what the API validates against
(see docs/runbooks/kind-cluster-troubleshooting.md, section 5.1). Contrary
to this script's original design assumption, this was confirmed live to
affect docker-compose too, not just kind: Authentik derives the OIDC `iss`
claim from the *request's* Host header on every request, including
"issuer_mode: global" providers -- a token minted by curling
localhost:9000 from the host machine gets `iss: http://localhost:9000/`,
but iverson-api's own OIDC discovery fetch (using its configured Authority)
resolves the docker-network service hostname and gets a different `iss`.
Both targets force a matching Host header to avoid this.

  compose  Talks to http://localhost:9000 directly, with the Host header
           forced to `authentik-server:9000` (the docker-compose service
           name iverson-api's Authority is configured with). client_id
           defaults to the fixed dev value provisioned in
           charts/authentik/blueprints/compose-only/service-clients.yaml.

  kind     Opens a `kubectl port-forward` to the in-cluster
           `<release>-authentik` Service, then talks to it over
           localhost with every request's Host header forced to
           `<release>-authentik:9000` (the in-cluster DNS name the API's
           OIDC `Authority` is actually configured with). client_id and
           the smoke-test user's password are read from the Secrets Task 5
           provisions (`<release>-authentik-loadtest-human-client`,
           `<release>-authentik-smoke-test-user`) unless overridden.

TOTP enrollment is one-time per environment: the first run enrolls a new
TOTP device for the smoke-test user and caches the shared secret locally
(under ~/.cache/iverson/); every subsequent run against the same
environment reuses the cached secret to solve the existing device's
challenge instead of re-enrolling (Authentik never re-exposes an already
enrolled device's secret).
"""

import argparse
import base64
import hashlib
import hmac
import json
import os
import socket
import struct
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from http.cookiejar import CookieJar

DEFAULT_USERNAME = "iverson-acting-user-smoke-test"
DEFAULT_COMPOSE_CLIENT_ID = "dev-iverson-loadtest-human-client-id"
DEFAULT_COMPOSE_PASSWORD = "dev-only-not-for-production-smoke-test-password-0123456789"
DEFAULT_COMPOSE_BASE_URL = "http://localhost:9000"
DEFAULT_COMPOSE_REDIRECT_URI = "http://localhost/placeholder-callback"
DEFAULT_KIND_REDIRECT_URI = "https://iverson.local/placeholder-callback"
DEFAULT_FLOW_SLUG = "default-authentication-flow"
DEFAULT_RELEASE = "iverson"
DEFAULT_NAMESPACE = "iverson"
DEFAULT_KIND_LOCAL_PORT = 19000
MAX_FLOW_STAGES = 20
MAX_TOTP_ATTEMPTS = 4
CACHE_DIR = os.path.expanduser("~/.cache/iverson")


def log(msg):
    print(msg, file=sys.stderr, flush=True)


# --- TOTP (RFC 6238) --------------------------------------------------------
# No third-party dependency (pyotp isn't guaranteed to be installed on every
# dev machine this script runs on) -- HMAC-SHA1 TOTP is ~15 lines of stdlib.


def totp(secret_b32: str, period: int = 30, digits: int = 6, at: float | None = None) -> str:
    padded = secret_b32.upper() + "=" * ((8 - len(secret_b32) % 8) % 8)
    key = base64.b32decode(padded)
    counter = int((at if at is not None else time.time()) // period)
    msg = struct.pack(">Q", counter)
    h = hmac.new(key, msg, hashlib.sha1).digest()
    offset = h[-1] & 0x0F
    code_int = (struct.unpack(">I", h[offset : offset + 4])[0] & 0x7FFFFFFF) % (10**digits)
    return str(code_int).zfill(digits)


def parse_totp_secret(config_url: str) -> str:
    """Extract the `secret` query param from an otpauth:// config_url."""
    parsed = urllib.parse.urlparse(config_url)
    qs = urllib.parse.parse_qs(parsed.query)
    secrets = qs.get("secret")
    if not secrets:
        raise RuntimeError(f"config_url has no 'secret' param: {config_url}")
    return secrets[0]


# --- PKCE --------------------------------------------------------------------


def generate_pkce():
    verifier = base64.urlsafe_b64encode(os.urandom(32)).rstrip(b"=").decode()
    challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
    return verifier, challenge


# --- Local TOTP-secret cache (dev-only; never printed to stdout) ------------


def totp_cache_path(target: str, username: str) -> str:
    return os.path.join(CACHE_DIR, f"acting-user-totp-secret-{target}-{username}.txt")


def load_cached_totp_secret(target: str, username: str) -> str | None:
    path = totp_cache_path(target, username)
    if not os.path.exists(path):
        return None
    with open(path) as f:
        return f.read().strip() or None


def save_cached_totp_secret(target: str, username: str, secret: str) -> None:
    os.makedirs(CACHE_DIR, exist_ok=True)
    path = totp_cache_path(target, username)
    with open(path, "w") as f:
        f.write(secret + "\n")
    os.chmod(path, 0o600)
    log(f"Cached new TOTP secret for future runs at {path}")


# --- HTTP plumbing -------------------------------------------------------
# The flow-executor's own convention (confirmed live): a successful POST to
# a stage returns an HTTP 302 redirecting back to the *same* executor URL --
# the caller is expected to follow up with a fresh GET to see the next
# stage's challenge. We disable automatic redirect-following entirely so we
# can also inspect the one other 302 that matters (the final
# /application/o/authorize/ redirect to redirect_uri?code=...), which must
# NOT be followed (redirect_uri is a placeholder that doesn't resolve).


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


class HttpClient:
    def __init__(self, host_header: str | None):
        self.host_header = host_header
        self.cookiejar = CookieJar()
        self.opener = urllib.request.build_opener(
            NoRedirect(), urllib.request.HTTPCookieProcessor(self.cookiejar)
        )

    def request(self, method: str, url: str, data: bytes | None = None, content_type: str | None = None):
        headers = {"Accept": "application/json"}
        if content_type:
            headers["Content-Type"] = content_type
        if self.host_header:
            headers["Host"] = self.host_header
        req = urllib.request.Request(url, data=data, headers=headers, method=method)
        try:
            resp = self.opener.open(req)
            return resp.status, dict(resp.getheaders()), resp.read()
        except urllib.error.HTTPError as e:
            # 302s (and real 4xx/5xx) land here since NoRedirect stops
            # HTTPRedirectHandler from swallowing them, and urllib always
            # raises HTTPError for any non-2xx status regardless.
            return e.code, dict(e.headers.items()), e.read()

    def get_json(self, url: str) -> dict:
        status, _headers, body = self.request("GET", url)
        if status != 200:
            raise RuntimeError(f"GET {url} -> {status}: {body!r}")
        return json.loads(body)

    def post_json(self, url: str, payload: dict):
        data = json.dumps(payload).encode()
        status, headers, body = self.request("POST", url, data=data, content_type="application/json")
        return status, headers, body

    def get_raw(self, url: str):
        return self.request("GET", url)


# --- Flow executor driver ----------------------------------------------------


class TotpAttemptState:
    """Tracks TOTP time-steps already attempted against the flow-executor in
    this run. Confirmed live: Authentik records a code as "used" (via the
    device's `last_used`/`last_t` bookkeeping) on *any* submission attempt
    against a given 30s time-step, success or failure -- so submitting twice
    within the same window always fails the second time with a generic
    "Invalid Token" error, indistinguishable from a genuinely wrong code.
    Rather than misreport that as a real validation failure, wait out the
    rest of the current window and try again with a fresh code.
    """

    def __init__(self):
        self.last_counter = None
        self.attempts = 0


def submit_totp_code(client: HttpClient, flow_url: str, secret: str, state: "TotpAttemptState"):
    if state.attempts >= MAX_TOTP_ATTEMPTS:
        raise RuntimeError(
            f"TOTP code was rejected {MAX_TOTP_ATTEMPTS} times in a row; giving up. "
            "If this isn't just window-reuse, the cached secret is likely stale -- see "
            "the 'already enrolled' error message for how to reset it."
        )
    now = time.time()
    counter = int(now // 30)
    if state.last_counter is not None and counter == state.last_counter:
        wait = (counter + 1) * 30 - now + 0.5
        log(f"waiting {wait:.1f}s for a fresh TOTP time-step (server rejects reusing one within the same 30s window)...")
        time.sleep(wait)
    code = totp(secret)
    state.last_counter = int(time.time() // 30)
    state.attempts += 1
    client.post_json(flow_url, {"code": code})


def drive_authentication_flow(client: HttpClient, flow_url: str, target: str, username: str, password: str) -> None:
    """Walk the flow-executor's stage machine to completion (authenticated
    session cookie set), handling both first-run TOTP enrollment and
    subsequent-run TOTP validation transparently based on live server state.
    """
    totp_state = TotpAttemptState()
    for _ in range(MAX_FLOW_STAGES):
        challenge = client.get_json(flow_url)
        component = challenge.get("component")
        log(f"flow stage: {component}")

        if component == "xak-flow-redirect":
            log("authentication flow complete (session is now authenticated)")
            return

        if component == "ak-stage-identification":
            client.post_json(flow_url, {"uid_field": username})
            continue

        if component == "ak-stage-password":
            client.post_json(flow_url, {"password": password})
            continue

        if component == "ak-stage-authenticator-validate":
            device_challenges = challenge.get("device_challenges") or []
            if device_challenges:
                secret = load_cached_totp_secret(target, username)
                if secret is None:
                    raise RuntimeError(
                        "This user already has an enrolled TOTP device on the server, but "
                        f"no locally cached secret exists at {totp_cache_path(target, username)}. "
                        "Authentik never re-exposes an enrolled device's secret. Either restore "
                        "the cached secret file from wherever it was first generated, or delete "
                        f"the '{username}' user's TOTP device in Authentik's admin UI (or reset "
                        "the environment's Authentik data) to force re-enrollment."
                    )
                submit_totp_code(client, flow_url, secret, totp_state)
                continue

            configuration_stages = challenge.get("configuration_stages") or []
            totp_stage = next(
                (s for s in configuration_stages if s.get("meta_model_name", "").endswith("authenticatortotpstage")),
                None,
            )
            if totp_stage is None:
                raise RuntimeError(
                    "No TOTP configuration stage is offered by the authenticator-validate "
                    f"stage; available: {configuration_stages}"
                )
            log(f"no MFA device enrolled yet -- enrolling TOTP (stage pk={totp_stage['pk']})")
            client.post_json(flow_url, {"selected_stage": totp_stage["pk"]})
            continue

        if component == "ak-stage-authenticator-totp":
            secret = parse_totp_secret(challenge["config_url"])
            save_cached_totp_secret(target, username, secret)
            submit_totp_code(client, flow_url, secret, totp_state)
            continue

        raise RuntimeError(f"Unhandled flow-executor component {component!r}: {challenge!r}")

    raise RuntimeError(f"Authentication flow did not complete after {MAX_FLOW_STAGES} stages")


def authorize_and_get_code(
    client: HttpClient, base_url: str, client_id: str, redirect_uri: str, code_challenge: str, state: str
) -> str:
    query = urllib.parse.urlencode(
        {
            "client_id": client_id,
            "redirect_uri": redirect_uri,
            "response_type": "code",
            "scope": "openid",
            "code_challenge": code_challenge,
            "code_challenge_method": "S256",
            "state": state,
        }
    )
    url = f"{base_url}/application/o/authorize/?{query}"
    status, headers, body = client.get_raw(url)
    if status != 302:
        raise RuntimeError(f"Expected a 302 from /application/o/authorize/, got {status}: {body!r}")
    location = headers.get("Location") or headers.get("location")
    if not location:
        raise RuntimeError(f"302 from /application/o/authorize/ had no Location header: {headers!r}")
    parsed = urllib.parse.urlparse(location)
    qs = urllib.parse.parse_qs(parsed.query)
    if "error" in qs:
        raise RuntimeError(
            f"/application/o/authorize/ rejected the request: {qs.get('error')} "
            f"{qs.get('error_description')}"
        )
    codes = qs.get("code")
    if not codes:
        raise RuntimeError(f"302 Location had neither 'code' nor 'error': {location}")
    return codes[0]


def exchange_code_for_token(
    client: HttpClient, base_url: str, client_id: str, code: str, redirect_uri: str, code_verifier: str
) -> str:
    data = urllib.parse.urlencode(
        {
            "grant_type": "authorization_code",
            "code": code,
            "redirect_uri": redirect_uri,
            "client_id": client_id,
            "code_verifier": code_verifier,
        }
    ).encode()
    url = f"{base_url}/application/o/token/"
    status, _headers, body = client.request(
        "POST", url, data=data, content_type="application/x-www-form-urlencoded"
    )
    if status != 200:
        raise RuntimeError(f"Token exchange failed ({status}): {body!r}")
    payload = json.loads(body)
    if "access_token" not in payload:
        raise RuntimeError(f"Token response had no access_token: {payload!r}")
    return payload["access_token"]


# --- kind-specific plumbing: port-forward + Secret lookups ------------------


def kubectl_get_secret_value(namespace: str, secret_name: str, key: str) -> str:
    result = subprocess.run(
        ["kubectl", "get", "secret", "-n", namespace, secret_name, "-o", f"jsonpath={{.data.{key}}}"],
        capture_output=True,
        text=True,
        check=True,
    )
    encoded = result.stdout.strip()
    if not encoded:
        raise RuntimeError(f"Secret {secret_name}/{key} in namespace {namespace} is empty or missing")
    return base64.b64decode(encoded).decode()


class PortForward:
    """Manages a `kubectl port-forward` subprocess for the lifetime of a
    `with` block, waiting for the local port to accept connections before
    yielding control."""

    def __init__(self, namespace: str, service: str, local_port: int, remote_port: int = 9000):
        self.namespace = namespace
        self.service = service
        self.local_port = local_port
        self.remote_port = remote_port
        self.proc = None

    def __enter__(self):
        log(f"starting: kubectl port-forward -n {self.namespace} svc/{self.service} {self.local_port}:{self.remote_port}")
        self.proc = subprocess.Popen(
            [
                "kubectl",
                "port-forward",
                "-n",
                self.namespace,
                f"svc/{self.service}",
                f"{self.local_port}:{self.remote_port}",
            ],
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
        )
        deadline = time.time() + 30
        while time.time() < deadline:
            if self.proc.poll() is not None:
                out = self.proc.stdout.read() if self.proc.stdout else ""
                raise RuntimeError(f"kubectl port-forward exited early:\n{out}")
            try:
                with socket.create_connection(("localhost", self.local_port), timeout=0.5):
                    log(f"port-forward ready on localhost:{self.local_port}")
                    return self
            except OSError:
                time.sleep(0.5)
        raise RuntimeError(f"kubectl port-forward did not become ready within 30s on port {self.local_port}")

    def __exit__(self, exc_type, exc_val, exc_tb):
        if self.proc is not None:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.proc.kill()


# --- main --------------------------------------------------------------------


def parse_args():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--target", required=True, choices=["compose", "kind"])
    p.add_argument("--username", default=DEFAULT_USERNAME)
    p.add_argument("--password", default=None, help="Defaults per-target (fixed dev value for compose, read from the kind Secret otherwise)")
    p.add_argument("--client-id", default=None, help="Defaults per-target (fixed dev value for compose, read from the kind Secret otherwise)")
    p.add_argument("--redirect-uri", default=None, help="Defaults per-target to match the provisioned OAuth2 client's redirect_uris")
    p.add_argument("--base-url", default=None, help="Override the computed base URL entirely")
    p.add_argument("--host-header", default=None, help="Override the computed Host header entirely (empty string forces no override)")
    p.add_argument("--flow-slug", default=DEFAULT_FLOW_SLUG)
    p.add_argument("--release", default=DEFAULT_RELEASE, help="Helm release name (kind target only)")
    p.add_argument("--namespace", default=DEFAULT_NAMESPACE, help="Kubernetes namespace (kind target only)")
    p.add_argument("--kind-local-port", type=int, default=DEFAULT_KIND_LOCAL_PORT, help="Local port for the kubectl port-forward (kind target only)")
    return p.parse_args()


def main() -> int:
    args = parse_args()

    if args.target == "compose":
        base_url = args.base_url or DEFAULT_COMPOSE_BASE_URL
        # Confirmed live: even docker-compose needs this. Authentik derives the OIDC `iss`
        # claim from the *request's* Host header (see kind-cluster-troubleshooting.md section
        # 5.1) -- a token minted by curling localhost:9000 from the host machine gets
        # `iss: http://localhost:9000/`, but iverson-api's own OIDC discovery fetch (using its
        # configured Authority, http://authentik-server:9000/...) resolves the in-container
        # service hostname and gets `iss: http://authentik-server:9000/` instead. Without this
        # override the mismatch causes a bare 401 with no useful server-side log line.
        host_header = args.host_header if args.host_header is not None else "authentik-server:9000"
        client_id = args.client_id or DEFAULT_COMPOSE_CLIENT_ID
        password = args.password or DEFAULT_COMPOSE_PASSWORD
        redirect_uri = args.redirect_uri or DEFAULT_COMPOSE_REDIRECT_URI
        return run(args, base_url, host_header, client_id, password, redirect_uri)

    # target == kind
    authentik_service = f"{args.release}-authentik"
    with PortForward(args.namespace, authentik_service, args.kind_local_port):
        base_url = args.base_url or f"http://localhost:{args.kind_local_port}"
        host_header = args.host_header if args.host_header is not None else f"{authentik_service}:9000"
        client_id = args.client_id or kubectl_get_secret_value(
            args.namespace, f"{args.release}-authentik-loadtest-human-client", "client-id"
        )
        password = args.password or kubectl_get_secret_value(
            args.namespace, f"{args.release}-authentik-smoke-test-user", "password"
        )
        redirect_uri = args.redirect_uri or DEFAULT_KIND_REDIRECT_URI
        return run(args, base_url, host_header, client_id, password, redirect_uri)


def run(args, base_url: str, host_header: str | None, client_id: str, password: str, redirect_uri: str) -> int:
    log(f"target={args.target} base_url={base_url} host_header={host_header!r} client_id={client_id}")

    client = HttpClient(host_header)
    flow_url = f"{base_url}/api/v3/flows/executor/{args.flow_slug}/"

    try:
        drive_authentication_flow(client, flow_url, args.target, args.username, password)

        code_verifier, code_challenge = generate_pkce()
        state = base64.urlsafe_b64encode(os.urandom(16)).rstrip(b"=").decode()
        code = authorize_and_get_code(client, base_url, client_id, redirect_uri, code_challenge, state)
        log("got authorization code, exchanging for token...")
        access_token = exchange_code_for_token(client, base_url, client_id, code, redirect_uri, code_verifier)
    except Exception as e:  # noqa: BLE001 -- top-level CLI error reporting
        log(f"ERROR: {e}")
        return 1

    log("success -- printing access token to stdout")
    print(access_token)
    return 0


if __name__ == "__main__":
    sys.exit(main())
