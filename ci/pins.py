#!/usr/bin/env python3
"""The version pins outside Dependabot's reach (CSR round 12 §4.5), from ci/pins.yaml.

    python3 ci/pins.py consistency   # every row's locations hold the same version; exit 1 on a mismatch
    python3 ci/pins.py freshness     # one issue "Pin behind: <name>" per stale row (needs gh and GH_TOKEN)

Each row of ci/pins.yaml names a pin, its source (where the newest version is read) and its locations: a file
and a regular expression whose one group is the version. Every match of a location's expression in its file
counts, and every match of every location must agree. A leading "v" is ignored when versions are compared.

Sources:
    github-release <owner/repo>                  the latest release's tag
    pypi <package>                               the package's current version
    dockerhub-tag <repo> <series>                the newest <series>.N tag (kindest/node's patch releases)
    eks-addon <name>                             the newest add-on version compatible with the EKS cluster's
                                                 Kubernetes version (needs the AWS role; skipped without it)
    cnpg-default <chart>                         CNPG's DefaultImageName tag at the operators stub chart's appVersion
    chart-app-version <chart>                    the operators stub chart's appVersion
    maven-central <groupId:artifactId> <series>  the newest release in maven-metadata.xml within the series
    go-proxy <module>                            the Version of proxy.golang.org's @latest
    dependabot                                   Dependabot tracks it; only the consistency check reads the row

Standard library only. Stdout carries one line per row; nothing here prints a token."""
import json
import os
import re
import subprocess
import sys
import urllib.request
import xml.etree.ElementTree as ET

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TABLE = os.path.join(ROOT, "ci", "pins.yaml")
OPERATOR_STUB = "Iverson.Server/deploy/terraform/modules/operators/versions/Chart.yaml"
EKS_VARIABLES = "Iverson.Server/deploy/terraform/modules/cluster-aws/variables.tf"
ISSUE_PREFIX = "Pin behind: "


class PinError(Exception):
    pass


# --- the table ---------------------------------------------------------------------------------------------

def _scalar(value):
    value = value.strip()
    if value.startswith("'"):
        if len(value) < 2 or not value.endswith("'"):
            raise PinError(f"unterminated quoted value: {value}")
        return value[1:-1].replace("''", "'")
    return value


def load_table(text):
    """Reads the table's own YAML subset: a list of rows, each `- name:`, `  source:`, optional `  note:`,
    and `  locations:` with `    - file:` / `      regex:` pairs. Values are plain or single-quoted."""
    rows, row, location = [], None, None
    for number, line in enumerate(text.splitlines(), 1):
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        if m := re.fullmatch(r"- name: (.+)", line):
            row, location = {"name": _scalar(m[1]), "locations": []}, None
            rows.append(row)
        elif row is not None and (m := re.fullmatch(r"  (source|note): (.+)", line)):
            row[m[1]] = _scalar(m[2])
        elif row is not None and line == "  locations:":
            pass
        elif row is not None and (m := re.fullmatch(r"    - file: (.+)", line)):
            location = {"file": _scalar(m[1])}
            row["locations"].append(location)
        elif location is not None and (m := re.fullmatch(r"      regex: (.+)", line)):
            location["regex"] = _scalar(m[1])
        else:
            raise PinError(f"pins.yaml line {number} is not in the table's format: {line!r}")
    names = [r["name"] for r in rows]
    for r in rows:
        if "source" not in r or not r["locations"]:
            raise PinError(f"row {r['name']}: needs a source and at least one location")
        if names.count(r["name"]) > 1:
            raise PinError(f"row {r['name']}: the name is used twice")
        for loc in r["locations"]:
            if "regex" not in loc:
                raise PinError(f"row {r['name']}: location {loc['file']} has no regex")
            if re.compile(loc["regex"], re.M).groups != 1:
                raise PinError(f"row {r['name']}: the regex for {loc['file']} must have exactly one group")
    return rows


def norm(version):
    return version[1:] if version.startswith("v") else version


def found(row, root):
    """Every (file, version) the row's locations match, or a PinError naming a location that matches nothing."""
    out = []
    for loc in row["locations"]:
        try:
            text = open(os.path.join(root, loc["file"]), encoding="utf-8").read()
        except OSError as e:
            raise PinError(f"{row['name']}: cannot read {loc['file']}: {e.strerror}")
        values = re.findall(loc["regex"], text, re.M)
        if not values:
            raise PinError(f"{row['name']}: {loc['file']} has no match for {loc['regex']}")
        out += [(loc["file"], v) for v in values]
    return out


def consistency(rows, root):
    errors = []
    for row in rows:
        try:
            values = found(row, root)
        except PinError as e:
            errors.append(str(e))
            continue
        if len({norm(v) for _, v in values}) > 1:
            listing = ", ".join(f"{f}={v}" for f, v in values)
            errors.append(f"{row['name']}: the locations disagree: {listing}")
        else:
            print(f"ok {row['name']} {values[0][1] if values else '(no locations)'}")
    return errors


# --- sources -----------------------------------------------------------------------------------------------

def http_get(url):
    headers = {"User-Agent": "iverson-pins"}
    if url.startswith("https://api.github.com/") and os.environ.get("GITHUB_TOKEN"):
        headers["Authorization"] = "Bearer " + os.environ["GITHUB_TOKEN"]
    with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=60) as response:
        return response.read().decode("utf-8")


def run(argv):
    return subprocess.run(argv, check=True, capture_output=True, text=True).stdout


def version_key(version):
    return tuple(int(n) for n in re.findall(r"\d+", version))


def stub_chart(chart, root):
    """The operators stub's (version, repository) for a chart."""
    text = open(os.path.join(root, OPERATOR_STUB), encoding="utf-8").read()
    m = re.search(rf"- name: {re.escape(chart)}\n\s+version: (\S+)\n\s+repository: (\S+)", text)
    if not m:
        raise PinError(f"no {chart} entry in {OPERATOR_STUB}")
    return m[1], m[2]


def chart_app_version(chart, root, run):
    version, repository = stub_chart(chart, root)
    m = re.search(r"^appVersion: \"?([^\"\s]+)\"?$", run(["helm", "show", "chart", chart, "--repo", repository, "--version", version]), re.M)
    if not m:
        raise PinError(f"helm show chart {chart} {version} prints no appVersion")
    return m[1]


def eks_kubernetes_version(root):
    text = open(os.path.join(root, EKS_VARIABLES), encoding="utf-8").read()
    m = re.search(r'variable "kubernetes_version" \{[^}]*default\s*=\s*"([^"]+)"', text)
    if not m:
        raise PinError(f"no kubernetes_version default in {EKS_VARIABLES}")
    return m[1]


def newest(source, root, http=http_get, run=run):
    """The newest version the source offers, for comparison with the pinned one."""
    kind, *args = source.split()
    if kind == "github-release":
        return json.loads(http(f"https://api.github.com/repos/{args[0]}/releases/latest"))["tag_name"]
    if kind == "pypi":
        return json.loads(http(f"https://pypi.org/pypi/{args[0]}/json"))["info"]["version"]
    if kind == "dockerhub-tag":
        repo, series = args
        pattern = re.compile(re.escape(series) + r"\.(\d+)")
        url, tags = f"https://hub.docker.com/v2/repositories/{repo}/tags?page_size=100", []
        while url:
            page = json.loads(http(url))
            tags += [t["name"] for t in page["results"] if pattern.fullmatch(t["name"])]
            url = page.get("next")
        if not tags:
            raise PinError(f"Docker Hub lists no {repo} tag in the {series} series")
        return max(tags, key=version_key)
    if kind == "eks-addon":
        out = json.loads(run(["aws", "eks", "describe-addon-versions", "--addon-name", args[0],
                              "--kubernetes-version", eks_kubernetes_version(root), "--output", "json"]))
        versions = [v["addonVersion"] for a in out["addons"] for v in a["addonVersions"]]
        if not versions:
            raise PinError(f"AWS lists no {args[0]} version for this Kubernetes version")
        return max(versions, key=version_key)
    if kind == "chart-app-version":
        return chart_app_version(args[0], root, run)
    if kind == "cnpg-default":
        app = chart_app_version(args[0], root, run)
        text = http(f"https://raw.githubusercontent.com/cloudnative-pg/cloudnative-pg/v{norm(app)}/pkg/versions/versions.go")
        m = re.search(r'^\s*DefaultImageName = "[^":]+:([^"]+)"$', text, re.M)
        if not m:
            raise PinError(f"CNPG {app}'s versions.go has no DefaultImageName")
        return m[1]
    if kind == "maven-central":
        coordinates, series = args
        group, artifact = coordinates.split(":")
        root_el = ET.fromstring(http(f"https://repo1.maven.org/maven2/{group.replace('.', '/')}/{artifact}/maven-metadata.xml"))
        releases = [v.text for v in root_el.iter("version")
                    if re.fullmatch(re.escape(series) + r"(\.\d+)+", v.text or "")]
        if not releases:
            raise PinError(f"Maven Central lists no {coordinates} release in the {series} series")
        return max(releases, key=version_key)
    if kind == "go-proxy":
        return json.loads(http(f"https://proxy.golang.org/{args[0]}/@latest"))["Version"]
    raise PinError(f"unknown source {source!r}")


# --- freshness ---------------------------------------------------------------------------------------------

def open_issue(name, run):
    title = ISSUE_PREFIX + name
    issues = json.loads(run(["gh", "issue", "list", "--state", "open", "--search", f'"{title}" in:title',
                             "--json", "number,title", "--limit", "100"]))
    numbers = [i["number"] for i in issues if i["title"] == title]
    return numbers[0] if numbers else None


def freshness(rows, root, http=http_get, run=run, aws_role=None):
    """Opens or updates one issue per stale row and closes it when the row is current. A row whose source
    cannot be read, and an EKS row without the AWS role, leave its issue as it is. Returns the error count."""
    errors = 0
    for row in rows:
        kind = row["source"].split()[0]
        if kind == "dependabot":
            continue
        if kind == "eks-addon" and not aws_role:
            print(f"skipped: no AWS role {row['name']}")
            continue
        try:
            pinned = found(row, root)[0][1]
            latest = newest(row["source"], root, http=http, run=run)
        except (PinError, OSError, ValueError, KeyError, subprocess.CalledProcessError) as e:
            print(f"error {row['name']}: {e}")
            errors += 1
            continue
        number = open_issue(row["name"], run)
        title = ISSUE_PREFIX + row["name"]
        if norm(pinned) != norm(latest):
            body = (f"`{row['name']}` is pinned at `{pinned}`; its source (`{row['source']}`) offers `{latest}`.\n\n"
                    "Locations (ci/pins.yaml):\n" + "".join(f"- `{l['file']}`\n" for l in row["locations"]) +
                    "\nRaise every location together (`python3 ci/pins.py consistency` checks them), or record "
                    "why the pin stays in the row's `note:`. docs/runbooks/csr12-supply-chain.md describes both.")
            if number is None:
                run(["gh", "issue", "create", "--title", title, "--body", body])
            else:
                run(["gh", "issue", "edit", str(number), "--body", body])
            print(f"stale {row['name']} {pinned} -> {latest}")
        else:
            if number is not None:
                run(["gh", "issue", "close", str(number), "--comment", f"Current: `{pinned}`."])
            print(f"current {row['name']} {pinned}")
    return errors


def main(argv):
    if len(argv) != 2 or argv[1] not in ("consistency", "freshness"):
        print(__doc__.split("\n\n")[1], file=sys.stderr)
        return 2
    try:
        rows = load_table(open(TABLE, encoding="utf-8").read())
    except PinError as e:
        print(f"error: {e}", file=sys.stderr)
        return 1
    if argv[1] == "consistency":
        errors = consistency(rows, ROOT)
        for e in errors:
            print(f"error: {e}", file=sys.stderr)
        return 1 if errors else 0
    return 1 if freshness(rows, ROOT, aws_role=os.environ.get("PIN_FRESHNESS_AWS_ROLE")) else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
