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
