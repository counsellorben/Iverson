import grpc

from iverson_client import IversonClient, IversonClientCredentials


def test_client_with_credentials_uses_secure_channel(monkeypatch):
    captured = {}

    def fake_secure_channel(address, channel_creds):
        captured["address"] = address
        captured["channel_creds"] = channel_creds
        return object()

    monkeypatch.setattr("iverson_client.core.grpc.secure_channel", fake_secure_channel)
    monkeypatch.setattr(
        "iverson_client.core.mapping_grpc.ObjectMappingServiceStub", lambda channel: object()
    )

    IversonClient(
        host="localhost",
        port=5000,
        credentials=IversonClientCredentials("id", "secret", "http://localhost:9000/application/o/token/"),
    )

    assert captured["address"] == "localhost:5000"


def test_client_with_acting_user_token_only_uses_secure_channel_and_survives_first_call(monkeypatch):
    """Regression test for CIR round 1 finding §2.1: constructing IversonClient with only
    acting_user_token (no base credentials) must not (a) fall through to an insecure channel,
    dropping the token, or (b) build a _CachedTokenProvider(None) that crashes with
    AttributeError the first time the auth plugin actually runs (not at construction time —
    the plugin re-reads self._credentials.client_id on every call)."""
    captured = {}
    captured_call_creds_plugins = []
    real_metadata_call_credentials = grpc.metadata_call_credentials

    def fake_secure_channel(address, channel_creds):
        captured["address"] = address
        captured["channel_creds"] = channel_creds
        return object()

    def fake_metadata_call_credentials(plugin):
        captured_call_creds_plugins.append(plugin)
        # Still build a real CallCredentials so the subsequent (unmocked)
        # composite_channel_credentials() call downstream keeps working.
        return real_metadata_call_credentials(plugin)

    monkeypatch.setattr("iverson_client.core.grpc.secure_channel", fake_secure_channel)
    monkeypatch.setattr(
        "iverson_client.core.grpc.metadata_call_credentials", fake_metadata_call_credentials
    )
    monkeypatch.setattr(
        "iverson_client.core.mapping_grpc.ObjectMappingServiceStub", lambda channel: object()
    )

    IversonClient(host="localhost", port=5000, acting_user_token="user-token-123")

    # Branch-condition fix: secure_channel was used, not a bare insecure_channel.
    assert captured["address"] == "localhost:5000"

    # Only one call-credentials plugin should have been built: the acting-user one. If the
    # old unconditional code path were still present, a second _BearerTokenAuthPlugin
    # wrapping _CachedTokenProvider(None) would also have been constructed here.
    assert len(captured_call_creds_plugins) == 1
    plugin = captured_call_creds_plugins[0]

    # Simulate what grpc does on the first real RPC call: invoke the plugin. Before the fix,
    # this is exactly where _CachedTokenProvider(None).get_token() would raise AttributeError
    # on self._credentials.client_id — a failure that construction alone does not surface.
    result = {}

    def callback(metadata, error):
        result["metadata"] = metadata
        result["error"] = error

    plugin(None, callback)

    assert result["error"] is None
    assert result["metadata"] == (("x-acting-user-authorization", "Bearer user-token-123"),)
