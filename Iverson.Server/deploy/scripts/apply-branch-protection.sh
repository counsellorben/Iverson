#!/usr/bin/env bash
set -euo pipefail

# CSR round-11 #19: sets main's branch protection so the security scans are required checks.
# The body restates every other setting as it was on 2026-10-12: no strict up-to-date rule, admin
# bypass kept (enforce_admins false), no required reviews, no push restrictions, every other
# option off. required_signatures has its own endpoint, so this PUT leaves it unchanged (off).
# Each check is pinned to the GitHub Actions app (app_id 15368), so only a check run from Actions
# satisfies it.
#
# Run it only once every required check below is green on main: a required check that is red or
# missing blocks every pull request. Try it first on a throwaway public repository you administer,
# with --repo (docs/runbooks/csr11-clients-tooling-ci.md).
#
# Usage: apply-branch-protection.sh [--repo owner/name] [--dry-run]
#   --repo     defaults to counsellorben/Iverson
#   --dry-run  prints the request body and makes no API call

usage() { echo "usage: ${0##*/} [--repo owner/name] [--dry-run]" >&2; }

repo=counsellorben/Iverson
dry_run=0
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

# The check-run names exactly as GitHub reports them (matrix jobs carry their matrix values).
checks=(
  "nuget-audit"
  "npm-audit-adminui"
  "npm-audit-ts-sdk"
  "Analyze (actions)"
  "Analyze (csharp)"
  "Analyze (go)"
  "Analyze (java-kotlin)"
  "Analyze (javascript-typescript)"
  "Analyze (python)"
  "dotnet-build-test"
  "typescript-sdk-build-test"
  "govulncheck"
  "pip-audit-python-sdk"
  "pip-audit-agents"
  "first-party (iverson-api, Iverson.Server/Iverson.Api/Dockerfile)"
  "first-party (iverson-admin-ui, Iverson.AdminUI/Dockerfile)"
  "third-party"
  "helm-lint"
  "helm-template-kubeconform"
  "terraform-validate (aws)"
  "terraform-validate (azure)"
  "terraform-validate (gcp)"
)

# The names hold no quote or backslash, so they go into the JSON as they are.
entries=""
for c in "${checks[@]}"; do
  entries+="${entries:+,
}      { \"context\": \"${c}\", \"app_id\": 15368 }"
done

body=$(cat <<JSON
{
  "required_status_checks": {
    "strict": false,
    "checks": [
${entries}
    ]
  },
  "enforce_admins": false,
  "required_pull_request_reviews": null,
  "restrictions": null,
  "required_linear_history": false,
  "allow_force_pushes": false,
  "allow_deletions": false,
  "block_creations": false,
  "required_conversation_resolution": false,
  "lock_branch": false,
  "allow_fork_syncing": false
}
JSON
)

if [[ $dry_run = 1 ]]; then
  echo "PUT repos/${repo}/branches/main/protection (dry run, nothing sent)" >&2
  printf '%s\n' "$body"
  exit 0
fi

printf '%s\n' "$body" | gh api -X PUT "repos/${repo}/branches/main/protection" --input -
