#!/usr/bin/env bash
set -euo pipefail

# CSR round 12 §2.2: a tag ruleset so release tags (v*) can be created, moved or deleted only by the
# repository admin role. Release signatures are bound to their tag (publish-images.yml), so whoever can
# create a v* tag can have the workflow sign a release.
#
# GitHub does not document the numeric id of the built-in admin role, so the ruleset is created with
# enforcement disabled, read back, and switched to active only with --activate, which refuses unless the
# read-back shows the bypass actor and that the admin running this script can bypass it
# (current_user_can_bypass = always). Check the bypass list in the repository's Settings, then Rules,
# then Rulesets, too: it must read "Repository admin" (docs/runbooks/csr12-supply-chain.md).
#
# Usage: apply-tag-ruleset.sh [--repo owner/name] [--dry-run] [--activate]
#   --repo      defaults to counsellorben/Iverson
#   --dry-run   prints the request body and makes no API call
#   --activate  switches the existing ruleset to active after checking its read-back

usage() { echo "usage: ${0##*/} [--repo owner/name] [--dry-run] [--activate]" >&2; }

repo=counsellorben/Iverson
dry_run=0
activate=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --repo)
      [[ $# -ge 2 ]] || { usage; exit 2; }
      repo=$2
      shift 2
      ;;
    --dry-run)
      dry_run=1
      shift
      ;;
    --activate)
      activate=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      usage
      exit 2
      ;;
  esac
done
[[ $repo =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ && $repo != *..* ]] || { echo "--repo must be owner/name, not '$repo'" >&2; exit 2; }

name=release-tags
# actor_id 5 is the id commonly given for the admin role. GitHub does not document it, which is why the
# ruleset starts disabled and --activate checks the read-back.
body=$(cat <<JSON
{
  "name": "${name}",
  "target": "tag",
  "enforcement": "disabled",
  "bypass_actors": [
    { "actor_id": 5, "actor_type": "RepositoryRole", "bypass_mode": "always" }
  ],
  "conditions": {
    "ref_name": { "include": ["refs/tags/v*"], "exclude": [] }
  },
  "rules": [
    { "type": "creation" },
    { "type": "update", "parameters": { "update_allows_fetch_and_merge": false } },
    { "type": "deletion" }
  ]
}
JSON
)

if [[ $dry_run = 1 ]]; then
  echo "POST repos/${repo}/rulesets (dry run, nothing sent; --activate later sends {\"enforcement\": \"active\"})" >&2
  printf '%s\n' "$body"
  exit 0
fi

# The id of the ruleset named $name, or nothing.
id=$(gh api "repos/${repo}/rulesets?per_page=100" --paginate --jq ".[] | select(.name == \"${name}\") | .id")
[[ $(printf '%s\n' "$id" | command grep -c .) -le 1 ]] || { echo "more than one ruleset is named ${name}: ${id//$'\n'/ }" >&2; exit 1; }

if [[ $activate = 0 ]]; then
  if [[ -z $id ]]; then
    id=$(printf '%s\n' "$body" | gh api -X POST "repos/${repo}/rulesets" --input - --jq .id)
    echo "created ruleset ${name} (id ${id}), enforcement disabled"
  else
    echo "ruleset ${name} exists (id ${id}); left unchanged"
  fi
else
  [[ -n $id ]] || { echo "no ruleset named ${name}: run without --activate first" >&2; exit 1; }
fi

# The read-back: enforcement, the bypass actors, and whether the caller can bypass the ruleset.
readback=$(gh api "repos/${repo}/rulesets/${id}" --jq '"enforcement=\(.enforcement) current_user_can_bypass=\(.current_user_can_bypass) bypass_actors=\([.bypass_actors[] | "\(.actor_type):\(.actor_id):\(.bypass_mode)"] | join(","))"')
echo "read back: ${readback}"

if [[ $activate = 1 ]]; then
  [[ $readback == *"current_user_can_bypass=always "* && $readback == *"bypass_actors=RepositoryRole:5:always"* ]] || {
    echo "not activating: the read-back must show bypass_actors=RepositoryRole:5:always and current_user_can_bypass=always for the admin running this script" >&2
    exit 1
  }
  gh api -X PUT "repos/${repo}/rulesets/${id}" -f enforcement=active --jq '"enforcement=\(.enforcement)"'
fi
