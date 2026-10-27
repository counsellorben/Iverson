"""pytest suite for the stub-reading helpers of setup.sh (operator_version) and setup.ps1
(Get-OperatorVersion), CSR round 12 §4.2. Run with:

    python3 -m pytest Iverson.Server/deploy/kind/test_chart_pins.py -q

Each test runs the helper's own text, cut out of the script, against a throwaway stub chart; helm must be
on PATH. The PowerShell tests need pwsh on PATH and are skipped without it."""
import os
import re
import shutil
import subprocess

import pytest

HERE = os.path.dirname(os.path.abspath(__file__))

STUB = """apiVersion: v2
name: stub
version: 0.1.0
dependencies:
  - name: tigera-operator
    version: v3.32.2
    repository: https://docs.tigera.io/calico/charts
  - name: metrics-server
    version: 3.9.0
    repository: https://kubernetes-sigs.github.io/metrics-server/
"""


@pytest.fixture
def stub(tmp_path):
    (tmp_path / "Chart.yaml").write_text(STUB)
    return str(tmp_path)


def bash_helper():
    text = open(os.path.join(HERE, "setup.sh")).read()
    match = re.search(r"^operator_version\(\) \{.*?^\}\n", text, re.M | re.S)
    assert match, "setup.sh defines no operator_version()"
    return match.group(0)


def run_bash(stub_dir, chart):
    return subprocess.run(["bash", "-c", bash_helper() + 'operator_version "$1" "$2"', "-", stub_dir, chart],
                          capture_output=True, text=True)


def test_bash_prints_the_pinned_version_of_each_chart(stub):
    assert run_bash(stub, "tigera-operator").stdout == "v3.32.2\n"
    assert run_bash(stub, "metrics-server").stdout == "3.9.0\n"


def test_bash_fails_naming_the_stub_for_a_chart_it_does_not_pin(stub):
    result = run_bash(stub, "cert-manager")
    assert result.returncode != 0
    assert result.stdout == ""
    assert f"no cert-manager pin in {stub}/Chart.yaml" in result.stderr


def test_bash_reads_the_real_kind_stub():
    result = run_bash(os.path.join(HERE, "versions"), "tigera-operator")
    assert result.returncode == 0 and result.stdout.strip().startswith("v3.")


needs_pwsh = pytest.mark.skipif(shutil.which("pwsh") is None, reason="pwsh is not on PATH")


def pwsh_helper():
    text = open(os.path.join(HERE, "setup.ps1")).read()
    match = re.search(r"^function Get-OperatorVersion\(.*?^\}\n", text, re.M | re.S)
    assert match, "setup.ps1 defines no Get-OperatorVersion"
    return match.group(0)


def run_pwsh(stub_dir, chart):
    script = '$ErrorActionPreference = "Stop"\n' + pwsh_helper() + f"Get-OperatorVersion '{stub_dir}' '{chart}'\n"
    return subprocess.run(["pwsh", "-NoProfile", "-NonInteractive", "-Command", script],
                          capture_output=True, text=True)


@needs_pwsh
def test_pwsh_prints_the_pinned_version_of_each_chart(stub):
    assert run_pwsh(stub, "tigera-operator").stdout.strip() == "v3.32.2"
    assert run_pwsh(stub, "metrics-server").stdout.strip() == "3.9.0"


@needs_pwsh
def test_pwsh_fails_naming_the_stub_for_a_chart_it_does_not_pin(stub):
    result = run_pwsh(stub, "cert-manager")
    assert result.returncode != 0
    assert "no cert-manager pin in" in result.stdout + result.stderr
