"""pytest suite for ci/pins.py (CSR round 12 §4.5). Run with:

    python3 -m pytest ci/test_pins.py -q

No network: the sources read recorded responses (trimmed copies of the real ones, 2026-10-26), and gh, helm
and aws are fakes that record their arguments."""
import json
import os
import re
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pins  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def table(text):
    return pins.load_table(text)


def write(root, path, text):
    full = os.path.join(root, path)
    os.makedirs(os.path.dirname(full), exist_ok=True)
    with open(full, "w") as f:
        f.write(text)


# --- the table and the consistency check ---------------------------------------------------------------------

ROW = """- name: tool
  source: github-release owner/tool
  locations:
    - file: a.yml
      regex: 'tool@(v[0-9.]+)'
    - file: b.yml
      regex: 'tool:([0-9.]+)'
"""


def test_the_table_reads_rows_with_quoted_regexes():
    rows = table("# comment\n\n" + ROW)
    assert rows == [{"name": "tool", "source": "github-release owner/tool", "locations": [
        {"file": "a.yml", "regex": "tool@(v[0-9.]+)"}, {"file": "b.yml", "regex": "tool:([0-9.]+)"}]}]


@pytest.mark.parametrize("bad, message", [
    ("- name: x\n  locations:\n    - file: a\n      regex: '(a)'\n", "needs a source"),
    ("- name: x\n  source: dependabot\n  locations:\n    - file: a\n      regex: 'a'\n", "exactly one group"),
    ("- name: x\n  source: dependabot\n  locations:\n    - file: a\n", "has no regex"),
    ("- name: x\n  source: dependabot\n   oops: 1\n", "not in the table's format"),
    (ROW + ROW, "used twice"),
])
def test_the_table_refuses_a_malformed_row(bad, message):
    with pytest.raises(pins.PinError, match=message):
        table(bad)


def test_consistency_passes_when_every_match_agrees_ignoring_a_leading_v(tmp_path):
    write(tmp_path, "a.yml", "run: tool@v1.2.3\nagain: tool@v1.2.3\n")
    write(tmp_path, "b.yml", "image: tool:1.2.3\n")
    assert pins.consistency(table(ROW), str(tmp_path)) == []


def test_consistency_fails_on_a_second_match_in_one_file_that_differs(tmp_path):
    write(tmp_path, "a.yml", "run: tool@v1.2.3\nagain: tool@v1.2.4\n")
    write(tmp_path, "b.yml", "image: tool:1.2.3\n")
    (error,) = pins.consistency(table(ROW), str(tmp_path))
    assert "tool: the locations disagree" in error and "a.yml=v1.2.4" in error


def test_consistency_fails_when_a_location_matches_nothing(tmp_path):
    write(tmp_path, "a.yml", "run: tool@v1.2.3\n")
    write(tmp_path, "b.yml", "image: other:1.2.3\n")
    (error,) = pins.consistency(table(ROW), str(tmp_path))
    assert "b.yml has no match" in error


def test_the_repositorys_table_is_consistent():
    rows = pins.load_table(open(pins.TABLE).read())
    assert pins.consistency(rows, ROOT) == []


@pytest.mark.parametrize("row, path", [
    ("istio", "Iverson.Server/deploy/terraform/modules/operators/charts/istio/ztunnel/Chart.yaml"),
    ("gitlab-image-node", ".gitlab-ci.yml"),
    ("pip-audit", ".gitlab-ci.yml"),
])
def test_consistency_catches_a_one_location_change_in_the_repositorys_table(tmp_path, row, path):
    # The pinned value is read from the copied files, so a legitimate version bump cannot break this test.
    rows = [r for r in pins.load_table(open(pins.TABLE).read()) if r["name"] == row]
    for loc in rows[0]["locations"]:
        write(tmp_path, loc["file"], open(os.path.join(ROOT, loc["file"])).read())
    loc = next(l for l in rows[0]["locations"] if l["file"] == path)
    text = open(os.path.join(tmp_path, path)).read()
    m = re.search(loc["regex"], text, re.M)
    old = m[1]
    new = old[:-1] + ("0" if old[-1] != "0" else "1")
    write(tmp_path, path, text[:m.start(1)] + new + text[m.end(1):])
    (error,) = pins.consistency(rows, str(tmp_path))
    assert f"{row}: the locations disagree" in error


def synthetic_inputs(root):
    """The two repository files newest() reads, as stubs: the values the Fakes below expect (CNPG chart 0.29.0,
    EKS 1.35) live here, so the repository's own versions can move without breaking these tests."""
    write(root, pins.OPERATOR_STUB, "dependencies:\n  - name: cloudnative-pg\n    version: 0.29.0\n"
          "    repository: https://cloudnative-pg.github.io/charts\n")
    write(root, pins.EKS_VARIABLES, 'variable "kubernetes_version" {\n  type    = string\n  default = "1.35"\n}\n')


# --- sources, against recorded responses ---------------------------------------------------------------------

RECORDED = {
    "https://api.github.com/repos/kubernetes-sigs/kind/releases/latest":
        '{"tag_name": "v0.33.0", "name": "v0.33.0", "draft": false, "prerelease": false}',
    "https://pypi.org/pypi/pip-audit/json": '{"info": {"name": "pip-audit", "version": "2.10.1"}}',
    "https://hub.docker.com/v2/repositories/kindest/node/tags?page_size=100": json.dumps({
        "count": 179, "next": "https://hub.docker.com/v2/repositories/kindest/node/tags?page=2&page_size=100",
        "results": [{"name": n} for n in ["v1.37.0", "v1.36.4", "v1.35.8", "v1.34.11", "v1.37.0-rc.1", "v1.35.5"]]}),
    "https://hub.docker.com/v2/repositories/kindest/node/tags?page=2&page_size=100": json.dumps({
        # Page 2 is not recorded: its tags are chosen to test the series filter (a later patch, a pre-release
        # numbered above it, and a tag whose prefix only looks like the series).
        "count": 179, "next": None, "results": [{"name": n} for n in ["v1.35.10", "v1.35.11-rc.0", "v1.3.5"]]}),
    "https://proxy.golang.org/golang.org/x/vuln/@latest":
        '{"Version":"v1.9.0","Time":"2026-10-11T03:39:53Z","Origin":{"VCS":"git","Ref":"refs/tags/v1.9.0"}}',
    "https://repo1.maven.org/maven2/org/apache/maven/apache-maven/maven-metadata.xml": """<?xml version="1.0" encoding="UTF-8"?>
<metadata><groupId>org.apache.maven</groupId><artifactId>apache-maven</artifactId><versioning>
<latest>4.0.0-rc-7</latest><release>4.0.0-rc-7</release><versions>
<version>3.9.9</version><version>3.9.15</version><version>3.9.16</version><version>3.10.0</version>
<version>4.0.0-beta-5</version><version>4.0.0-rc-7</version></versions></versioning></metadata>""",
    "https://raw.githubusercontent.com/cloudnative-pg/cloudnative-pg/v1.30.0/pkg/versions/versions.go": """package versions

const (
\t// Version is the version of the operator
\tVersion = "1.30.0"

\t// DefaultImageName is the default image used by the operator to create pods
\tDefaultImageName = "ghcr.io/cloudnative-pg/postgresql:18.4-system-trixie"

\t// DefaultOperatorImageName is the default operator image used by the controller in the pods running PostgreSQL
\tDefaultOperatorImageName = "ghcr.io/cloudnative-pg/cloudnative-pg:1.30.0"
)
""",
}

HELM_SHOW_CHART = """apiVersion: v2
appVersion: 1.30.0
description: CloudNativePG Operator Helm Chart
name: cloudnative-pg
version: 0.29.0
"""

# The shape of `aws eks describe-addon-versions --output json` (AWS CLI reference).
EKS = json.dumps({"addons": [{"addonName": "vpc-cni", "addonVersions": [
    {"addonVersion": "v1.23.1-eksbuild.1", "compatibilities": [{"clusterVersion": "1.35", "defaultVersion": True}]},
    {"addonVersion": "v1.23.10-eksbuild.2", "compatibilities": [{"clusterVersion": "1.35", "defaultVersion": False}]},
    {"addonVersion": "v1.23.9-eksbuild.1", "compatibilities": [{"clusterVersion": "1.35", "defaultVersion": False}]}]}]})


class Fakes:
    """http() answers from RECORDED; run() answers helm, aws and gh and records every argv."""

    def __init__(self, issues=()):
        self.calls = []
        self.issues = list(issues)

    def http(self, url):
        return RECORDED[url]

    def run(self, argv):
        self.calls.append(argv)
        if argv[:3] == ["helm", "show", "chart"]:
            assert argv[3:] == ["cloudnative-pg", "--repo", "https://cloudnative-pg.github.io/charts", "--version", "0.29.0"]
            return HELM_SHOW_CHART
        if argv[:3] == ["aws", "eks", "describe-addon-versions"]:
            assert argv[argv.index("--kubernetes-version") + 1] == "1.35"
            return EKS
        if argv[:3] == ["gh", "issue", "list"]:
            return json.dumps(self.issues)
        return ""

    def gh(self):
        return [c for c in self.calls if c[0] == "gh" and c[2] != "list"]


@pytest.mark.parametrize("source, expected", [
    ("github-release kubernetes-sigs/kind", "v0.33.0"),
    ("pypi pip-audit", "2.10.1"),
    ("go-proxy golang.org/x/vuln", "v1.9.0"),
    ("maven-central org.apache.maven:apache-maven 3.9", "3.9.16"),
    ("chart-app-version cloudnative-pg", "1.30.0"),
    ("cnpg-default cloudnative-pg", "18.4-system-trixie"),
    ("eks-addon vpc-cni", "v1.23.10-eksbuild.2"),
])
def test_each_source_reads_the_newest_version(source, expected, tmp_path):
    synthetic_inputs(tmp_path)
    fakes = Fakes()
    assert pins.newest(source, str(tmp_path), http=fakes.http, run=fakes.run) == expected


def test_the_kindest_node_series_keeps_the_newest_patch_of_its_own_minor_across_pages():
    fakes = Fakes()
    assert pins.newest("dockerhub-tag kindest/node v1.35", ROOT, http=fakes.http, run=fakes.run) == "v1.35.10"


def test_maven_central_keeps_its_series_and_ignores_pre_releases():
    fakes = Fakes()
    newest = lambda series: pins.newest(f"maven-central org.apache.maven:apache-maven {series}", ROOT,
                                        http=fakes.http, run=fakes.run)
    assert newest("3.9") == "3.9.16"
    assert newest("3") == "3.10.0"
    with pytest.raises(pins.PinError, match="no org.apache.maven:apache-maven release in the 4 series"):
        newest("4")


# --- freshness and its issues --------------------------------------------------------------------------------

FRESH_TABLE = """- name: kind
  source: github-release kubernetes-sigs/kind
  locations:
    - file: kind.txt
      regex: 'kind (v[0-9.]+)'
- name: vpc-cni
  source: eks-addon vpc-cni
  locations:
    - file: eks.txt
      regex: 'vpc-cni ([^ ]+)$'
- name: dependency-check-maven
  source: dependabot
  locations:
    - file: pom.txt
      regex: 'dc ([0-9.]+)'
"""


@pytest.fixture
def fresh_root(tmp_path):
    write(tmp_path, "pom.txt", "dc 13.0.0\n")
    write(tmp_path, "eks.txt", "vpc-cni v1.23.1-eksbuild.1\n")
    synthetic_inputs(tmp_path)
    return tmp_path


def run_freshness(root, kind_version, issues=(), aws_role=None):
    write(root, "kind.txt", f"kind {kind_version}\n")
    fakes = Fakes(issues)
    errors = pins.freshness(table(FRESH_TABLE), str(root), http=fakes.http, run=fakes.run, aws_role=aws_role)
    return errors, fakes


def test_a_stale_row_without_an_issue_opens_one(fresh_root):
    errors, fakes = run_freshness(fresh_root, "v0.32.0")
    assert errors == 0
    (call,) = fakes.gh()
    assert call[:5] == ["gh", "issue", "create", "--title", "Pin behind: kind"]
    assert "pinned at `v0.32.0`" in call[6] and "offers `v0.33.0`" in call[6]


def test_a_stale_row_with_an_open_issue_updates_it(fresh_root):
    errors, fakes = run_freshness(fresh_root, "v0.32.0", issues=[{"number": 7, "title": "Pin behind: kind"}])
    (call,) = fakes.gh()
    assert call[:5] == ["gh", "issue", "edit", "7", "--body"]


def test_a_current_row_closes_its_open_issue(fresh_root):
    errors, fakes = run_freshness(fresh_root, "v0.33.0", issues=[{"number": 7, "title": "Pin behind: kind"}])
    (call,) = fakes.gh()
    assert call[:4] == ["gh", "issue", "close", "7"]


def test_a_current_row_with_no_issue_touches_nothing(fresh_root):
    errors, fakes = run_freshness(fresh_root, "v0.33.0", issues=[{"number": 8, "title": "Pin behind: kind-extra"}])
    assert fakes.gh() == []


def test_without_the_aws_role_the_eks_rows_are_skipped_and_the_rest_run(fresh_root, capsys):
    errors, fakes = run_freshness(fresh_root, "v0.32.0")
    assert not any(c[0] == "aws" for c in fakes.calls)
    assert [c[4] for c in fakes.gh()] == ["Pin behind: kind"]
    assert "skipped: no AWS role vpc-cni" in capsys.readouterr().out


def test_with_the_aws_role_the_eks_rows_are_checked(fresh_root):
    errors, fakes = run_freshness(fresh_root, "v0.33.0", aws_role="arn:aws:iam::1:role/r")
    assert [c[4] for c in fakes.gh()] == ["Pin behind: vpc-cni"]


def test_dependabot_rows_never_reach_a_source_or_an_issue(fresh_root):
    errors, fakes = run_freshness(fresh_root, "v0.33.0")
    assert not any("dependency-check-maven" in " ".join(c) for c in fakes.calls)
    assert errors == 0  # a row that reached newest() would raise, be counted and be swallowed
