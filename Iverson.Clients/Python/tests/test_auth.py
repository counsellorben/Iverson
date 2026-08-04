from unittest.mock import MagicMock

import grpc

from iverson_client import IversonClient, IversonClientCredentials
from iverson_client.generated import object_mapping_pb2 as mapping_pb


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


def test_client_with_use_tls_and_acting_user_token_uses_ssl_channel_credentials(monkeypatch):
    """Regression test for the whole-branch review finding: use_tls=True was silently
    ignored whenever credentials/acting_user_token was also set, because that branch
    unconditionally based its composite channel credentials on
    grpc.local_channel_credentials() (a "trusted local network" designation with NO
    encryption) instead of grpc.ssl_channel_credentials(). Confirms the base channel
    credentials actually fed into composite_channel_credentials() tracks use_tls."""
    captured = {}
    ssl_sentinel = object()
    local_sentinel = object()

    def fake_secure_channel(address, channel_creds):
        captured["address"] = address
        captured["channel_creds"] = channel_creds
        return object()

    def fake_composite_channel_credentials(base_creds, *call_creds):
        captured["base_creds"] = base_creds
        return object()

    monkeypatch.setattr("iverson_client.core.grpc.secure_channel", fake_secure_channel)
    monkeypatch.setattr("iverson_client.core.grpc.ssl_channel_credentials", lambda: ssl_sentinel)
    monkeypatch.setattr("iverson_client.core.grpc.local_channel_credentials", lambda: local_sentinel)
    monkeypatch.setattr(
        "iverson_client.core.grpc.composite_channel_credentials", fake_composite_channel_credentials
    )
    monkeypatch.setattr(
        "iverson_client.core.grpc.metadata_call_credentials", lambda plugin: plugin
    )
    monkeypatch.setattr(
        "iverson_client.core.mapping_grpc.ObjectMappingServiceStub", lambda channel: object()
    )

    IversonClient(host="prod.example.com", port=5000, use_tls=True, acting_user_token="user-token-123")

    # use_tls=True must select ssl_channel_credentials as the base, not the
    # unencrypted local_channel_credentials.
    assert captured["base_creds"] is ssl_sentinel
    assert captured["base_creds"] is not local_sentinel


def test_client_without_use_tls_and_acting_user_token_uses_local_channel_credentials(monkeypatch):
    """Preserves today's default behavior: use_tls=False (the default) with
    acting_user_token set must still use local_channel_credentials() as the base,
    since grpcio rejects CallCredentials on a bare insecure_channel."""
    captured = {}
    ssl_sentinel = object()
    local_sentinel = object()

    def fake_secure_channel(address, channel_creds):
        captured["address"] = address
        captured["channel_creds"] = channel_creds
        return object()

    def fake_composite_channel_credentials(base_creds, *call_creds):
        captured["base_creds"] = base_creds
        return object()

    monkeypatch.setattr("iverson_client.core.grpc.secure_channel", fake_secure_channel)
    monkeypatch.setattr("iverson_client.core.grpc.ssl_channel_credentials", lambda: ssl_sentinel)
    monkeypatch.setattr("iverson_client.core.grpc.local_channel_credentials", lambda: local_sentinel)
    monkeypatch.setattr(
        "iverson_client.core.grpc.composite_channel_credentials", fake_composite_channel_credentials
    )
    monkeypatch.setattr(
        "iverson_client.core.grpc.metadata_call_credentials", lambda plugin: plugin
    )
    monkeypatch.setattr(
        "iverson_client.core.mapping_grpc.ObjectMappingServiceStub", lambda channel: object()
    )

    IversonClient(host="localhost", port=5000, acting_user_token="user-token-123")

    assert captured["base_creds"] is local_sentinel
    assert captured["base_creds"] is not ssl_sentinel


def test_get_schema_builds_request_and_converts_response():
    """get_schema must forward trace_id verbatim in the request and return the
    response's types unmodified — not just echo whatever the mock happens to hold."""
    client = IversonClient(host="localhost", port=1)
    client._mapping_stub = MagicMock()

    field = mapping_pb.SchemaField(
        name="title",
        clr_type=mapping_pb.CLR_STRING,
        is_search_key=True,
        search_key_order=2,
    )
    schema_type = mapping_pb.SchemaType(name="Article", fields=[field])
    client._mapping_stub.GetSchema.return_value = mapping_pb.GetSchemaResponse(
        types=[schema_type]
    )

    result = client.get_schema(trace_id="trace-abc")

    # Request built correctly.
    client._mapping_stub.GetSchema.assert_called_once()
    sent_request = client._mapping_stub.GetSchema.call_args[0][0]
    assert isinstance(sent_request, mapping_pb.GetSchemaRequest)
    assert sent_request.trace_id == "trace-abc"

    # Response converted correctly, with concrete field-level assertions.
    assert len(result) == 1
    returned_type = result[0]
    assert returned_type.name == "Article"
    assert len(returned_type.fields) == 1
    returned_field = returned_type.fields[0]
    assert returned_field.name == "title"
    assert returned_field.clr_type == mapping_pb.CLR_STRING
    assert returned_field.is_search_key is True
    assert returned_field.search_key_order == 2
