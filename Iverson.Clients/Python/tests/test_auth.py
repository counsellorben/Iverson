from unittest.mock import MagicMock

import grpc

from iverson_client import IversonClient, IversonClientCredentials
from iverson_client.annotations import iverson_entity, iverson_key
from iverson_client.generated import object_mapping_pb2 as mapping_pb
from iverson_client.generated import object_retrieval_pb2 as retrieval_pb


@iverson_entity
class CoordSchemaEntity:
    id: str = iverson_key()


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


def test_client_with_use_tls_and_credentials_uses_ssl_channel_credentials(monkeypatch):
    """Regression test for the whole-branch review finding: use_tls=True was silently
    ignored whenever the composite-channel-credentials branch was entered, because that
    branch unconditionally based its composite channel credentials on
    grpc.local_channel_credentials() (a "trusted local network" designation with NO
    encryption) instead of grpc.ssl_channel_credentials(). Confirms the base channel
    credentials actually fed into composite_channel_credentials() tracks use_tls. Originally
    entered via acting_user_token=; the acting-user-identity-parity initiative moved that
    token off channel credentials, so this branch is now entered via credentials= instead —
    the underlying use_tls bug it guards is unchanged and still fully reachable."""
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

    IversonClient(
        host="prod.example.com",
        port=5000,
        use_tls=True,
        credentials=IversonClientCredentials("id", "secret", "http://localhost:9000/application/o/token/"),
    )

    # use_tls=True must select ssl_channel_credentials as the base, not the
    # unencrypted local_channel_credentials.
    assert captured["base_creds"] is ssl_sentinel
    assert captured["base_creds"] is not local_sentinel


def test_client_without_use_tls_and_credentials_uses_local_channel_credentials(monkeypatch):
    """Preserves today's default behavior: use_tls=False (the default) while the composite-
    channel-credentials branch is entered (now via credentials=, see the sibling test above
    for why the entry point moved) must still use local_channel_credentials() as the base,
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

    IversonClient(
        host="localhost",
        port=5000,
        credentials=IversonClientCredentials("id", "secret", "http://localhost:9000/application/o/token/"),
    )

    assert captured["base_creds"] is local_sentinel
    assert captured["base_creds"] is not ssl_sentinel


def test_client_with_acting_user_token_only_uses_insecure_channel(monkeypatch):
    """As of the acting-user-identity-parity initiative, acting_user_token no longer rides
    channel credentials (it is now per-call metadata via _acting_user_metadata()), so
    constructing IversonClient with only acting_user_token (no base credentials) must fall
    through to the plain insecure_channel path, exactly like constructing with neither."""
    captured = {}

    def fake_insecure_channel(address):
        captured["address"] = address
        return object()

    def fail_secure_channel(address, channel_creds):
        raise AssertionError("secure_channel should not be used for acting_user_token alone")

    monkeypatch.setattr("iverson_client.core.grpc.insecure_channel", fake_insecure_channel)
    monkeypatch.setattr("iverson_client.core.grpc.secure_channel", fail_secure_channel)
    monkeypatch.setattr(
        "iverson_client.core.mapping_grpc.ObjectMappingServiceStub", lambda channel: object()
    )

    client = IversonClient(host="localhost", port=5000, acting_user_token="user-token-123")

    assert captured["address"] == "localhost:5000"
    assert client._acting_user_token == "user-token-123"


def _collect_acting_user_entries(per_call_metadata, captured_call_creds_plugins):
    """Combine the per-call metadata= entries with whatever a still-composed channel-level
    auth plugin would additionally inject, so a real re-added _ActingUserAuthPlugin
    composition is actually exercised rather than bypassed by mocking the stub away."""
    entries = [e for e in per_call_metadata if e[0] == "x-acting-user-authorization"]
    for plugin in captured_call_creds_plugins:
        result = {}

        def callback(metadata, error, _result=result):
            _result["metadata"] = metadata
            _result["error"] = error

        plugin(None, callback)
        if result.get("metadata"):
            entries.extend(e for e in result["metadata"] if e[0] == "x-acting-user-authorization")
    return entries


def test_get_schema_sends_exactly_one_acting_user_metadata_entry(monkeypatch):
    """Regression guard for the acting-user-identity-parity relocation: get_schema must
    carry the ambient acting-user identity via metadata=, and only via metadata= — not also
    via a channel-level auth plugin. This exercises the real channel construction (capturing
    any call-credentials plugin actually composed) rather than mocking the stub away, so a
    re-added _ActingUserAuthPlugin composition is not silently bypassed: with it still
    composed this would total two entries; a passing count of exactly one is proof metadata=
    is what carried it."""
    captured_call_creds_plugins = []
    real_metadata_call_credentials = grpc.metadata_call_credentials

    def fake_metadata_call_credentials(plugin):
        captured_call_creds_plugins.append(plugin)
        return real_metadata_call_credentials(plugin)

    monkeypatch.setattr(
        "iverson_client.core.grpc.metadata_call_credentials", fake_metadata_call_credentials
    )

    client = IversonClient(host="localhost", port=1, acting_user_token="user-token-123")
    client._mapping_stub = MagicMock()
    client._mapping_stub.GetSchema.return_value = mapping_pb.GetSchemaResponse(types=[])

    client.get_schema(trace_id="trace-abc")

    client._mapping_stub.GetSchema.assert_called_once()
    sent_metadata = client._mapping_stub.GetSchema.call_args.kwargs["metadata"]
    matching = _collect_acting_user_entries(sent_metadata, captured_call_creds_plugins)
    assert len(matching) == 1
    assert matching[0] == ("x-acting-user-authorization", "Bearer user-token-123")


def test_coordinator_call_sends_exactly_one_acting_user_metadata_entry(monkeypatch):
    """Permanent regression guard on the coordinator path (Step 4's 14 threaded call sites):
    a re-added _ActingUserAuthPlugin composition would again produce two entries here. Like
    the get_schema test above, this captures any channel-level call-credentials plugin
    actually composed rather than mocking it out of the picture."""
    captured_call_creds_plugins = []
    real_metadata_call_credentials = grpc.metadata_call_credentials

    def fake_metadata_call_credentials(plugin):
        captured_call_creds_plugins.append(plugin)
        return real_metadata_call_credentials(plugin)

    monkeypatch.setattr(
        "iverson_client.core.grpc.metadata_call_credentials", fake_metadata_call_credentials
    )

    client = IversonClient(host="localhost", port=1, acting_user_token="user-token-123")
    coordinator = client.coordinator(CoordSchemaEntity)
    coordinator._retrieval = MagicMock()
    coordinator._retrieval.Get.return_value = retrieval_pb.RetrievalResponse(found=False)

    coordinator.get("some-id")

    coordinator._retrieval.Get.assert_called_once()
    sent_metadata = coordinator._retrieval.Get.call_args.kwargs["metadata"]
    matching = _collect_acting_user_entries(sent_metadata, captured_call_creds_plugins)
    assert len(matching) == 1
    assert matching[0] == ("x-acting-user-authorization", "Bearer user-token-123")


def test_client_with_empty_string_acting_user_token_still_emits_the_header(monkeypatch):
    """An empty-string acting_user_token is a caller error, not "no identity": it must
    still produce a `Bearer ` header (with an empty token) so the server rejects the
    call with Unauthenticated, rather than being swallowed into rule 4 (no header,
    silent unauthenticated read)."""
    captured_call_creds_plugins = []
    real_metadata_call_credentials = grpc.metadata_call_credentials

    def fake_metadata_call_credentials(plugin):
        captured_call_creds_plugins.append(plugin)
        return real_metadata_call_credentials(plugin)

    monkeypatch.setattr(
        "iverson_client.core.grpc.metadata_call_credentials", fake_metadata_call_credentials
    )

    client = IversonClient(host="localhost", port=1, acting_user_token="")
    client._mapping_stub = MagicMock()
    client._mapping_stub.GetSchema.return_value = mapping_pb.GetSchemaResponse(types=[])

    client.get_schema(trace_id="trace-abc")

    client._mapping_stub.GetSchema.assert_called_once()
    sent_metadata = client._mapping_stub.GetSchema.call_args.kwargs["metadata"]
    matching = _collect_acting_user_entries(sent_metadata, captured_call_creds_plugins)
    assert len(matching) == 1
    assert matching[0] == ("x-acting-user-authorization", "Bearer ")


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
