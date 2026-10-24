"""pytest suite for mint_acting_user_token.py's password source (CSR round-11 #19). Run with:

    python3 -m pytest Iverson.Server/deploy/scripts/test_mint_acting_user_token.py -q

No network and no cluster: the flow (`run`), the kubectl port-forward and the Secret lookup are
fakes. Only main()'s choice of password is under test."""
import os
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mint_acting_user_token as mint  # noqa: E402

ENV = "IVERSON_ACTING_USER_PASSWORD"


@pytest.fixture
def fakes(monkeypatch):
    """Records the password main() hands to the flow and every Secret it reads."""
    seen = {"secrets": []}

    class NoPortForward:
        def __init__(self, *args, **kwargs):
            seen["port_forward"] = kwargs

        def __enter__(self):
            return self

        def __exit__(self, *exc):
            return False

    def fake_secret(namespace, secret_name, key):
        seen["secrets"].append(secret_name)
        return f"from-{secret_name}"

    def fake_run(args, base_url, host_header, client_id, password, redirect_uri):
        seen["password"] = password
        return 0

    monkeypatch.setattr(mint, "PortForward", NoPortForward)
    monkeypatch.setattr(mint, "kubectl_get_secret_value", fake_secret)
    monkeypatch.setattr(mint, "run", fake_run)
    return seen


def main_with(monkeypatch, *argv):
    monkeypatch.setattr(sys, "argv", ["mint_acting_user_token.py", *argv])
    return mint.main()


def test_compose_reads_the_password_from_the_environment(monkeypatch, fakes):
    monkeypatch.setenv(ENV, "env-pw")
    assert main_with(monkeypatch, "--target", "compose") == 0
    assert fakes["password"] == "env-pw"


def test_compose_without_the_environment_variable_fails_naming_it(monkeypatch, fakes, capsys):
    monkeypatch.delenv(ENV, raising=False)
    assert main_with(monkeypatch, "--target", "compose") == 1
    assert "password" not in fakes
    assert ENV in capsys.readouterr().err


def test_kind_prefers_the_environment_over_the_secret(monkeypatch, fakes):
    monkeypatch.setenv(ENV, "env-pw")
    assert main_with(monkeypatch, "--target", "kind") == 0
    assert fakes["password"] == "env-pw"
    assert "iverson-authentik-smoke-test-user" not in fakes["secrets"]


def test_kind_without_the_environment_variable_reads_the_secret(monkeypatch, fakes):
    monkeypatch.delenv(ENV, raising=False)
    assert main_with(monkeypatch, "--target", "kind") == 0
    assert fakes["password"] == "from-iverson-authentik-smoke-test-user"


def test_kind_names_the_environment_as_the_password_source_without_the_value(monkeypatch, fakes, capsys):
    monkeypatch.setenv(ENV, "env-pw-value")
    assert main_with(monkeypatch, "--target", "kind") == 0
    err = capsys.readouterr().err
    assert f"password source: {ENV}" in err
    assert "env-pw-value" not in err


def test_kind_names_the_secret_as_the_password_source_without_the_value(monkeypatch, fakes, capsys):
    monkeypatch.delenv(ENV, raising=False)
    assert main_with(monkeypatch, "--target", "kind") == 0
    err = capsys.readouterr().err
    assert "password source: Secret iverson-authentik-smoke-test-user" in err
    assert "from-iverson-authentik-smoke-test-user" not in err


def test_kind_port_forwards_as_the_operators_group(monkeypatch, fakes):
    # CSR round 12 §3.1: the admission policy admits a port-forward into an Authentik pod only for the
    # operators group, so the kind target impersonates it in addition to the admin's own group.
    monkeypatch.setenv(ENV, "env-pw")
    assert main_with(monkeypatch, "--target", "kind") == 0
    assert fakes["port_forward"]["impersonation"] == [
        "--as=kubernetes-admin",
        "--as-group=kubeadm:cluster-admins",
        "--as-group=iverson-operators",
    ]


def test_the_port_forward_command_carries_the_impersonation_before_the_verb():
    pf = mint.PortForward("iverson", "iverson-authentik", 19000, impersonation=mint.KIND_OPERATOR_IMPERSONATION)
    assert pf.argv() == [
        "kubectl",
        "--as=kubernetes-admin",
        "--as-group=kubeadm:cluster-admins",
        "--as-group=iverson-operators",
        "port-forward",
        "-n",
        "iverson",
        "svc/iverson-authentik",
        "19000:9000",
    ]


def test_there_is_no_password_flag(monkeypatch, fakes):
    with pytest.raises(SystemExit) as exit_info:
        main_with(monkeypatch, "--target", "compose", "--password", "argv-pw")
    assert exit_info.value.code == 2
    assert "password" not in fakes
