# CSR Round-11 Clients, Tooling and CI — Operator Runbook

**Why:** first-party images are now published to GHCR by GitHub Actions. Each pushed image is signed
keyless with cosign and carries a signed SPDX SBOM attestation, and the cloud profiles pull from
there. Branch protection on `main` requires the security scans. On GitLab, the NVD API key reaches
only protected pipelines. The acting-user mint script takes its password from the environment, not
the command line. kind and compose keep their locally built images.

The order after the merge:

1. Push `main`. `.github/workflows/publish-images.yml` publishes `iverson-api` and
   `iverson-admin-ui`. Run the workflow once by hand for the model images (§3).
2. Make the four packages public (§1), then verify them (§2).
3. Once every required check is green on `main`, apply branch protection: on a throwaway
   repository first, then on Iverson (§4).

## 1. Package visibility (one time)

GHCR creates every new package private, and GitHub has no REST endpoint that changes the
visibility of a user-owned package. Until a package is public, a cloud cluster's pull of it fails
with an authorization error, because the chart sets no `imagePullSecrets`.

After the first publish of each, make these four packages public: `iverson-api`,
`iverson-admin-ui`, `iverson-tei-model` and `iverson-ollama-model`. The model packages exist only
after the first tag or manual run. For each package, open your GitHub profile, then **Packages**,
then the package, then **Package settings**. Under **Danger Zone**, choose **Change visibility**,
then **Public**, and confirm with the package name.

The signatures and SBOM attestations are stored in the same package, so they become public with
it. The `org.opencontainers.image.source` label links each package to the Iverson repository.

## 2. Verifying an image

Use cosign 3.x, the version the workflow signs with. Both commands must succeed for every image
you deploy, from any machine with no GHCR login. A failure on a package that is still private is
an authorization error, not a verification failure.

```bash
cosign verify ghcr.io/counsellorben/iverson-api:<tag> \
  --certificate-identity-regexp '^https://github\.com/counsellorben/Iverson/\.github/workflows/publish-images\.yml@refs/(heads/main|tags/v.+)$' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com

cosign verify-attestation --type spdxjson ghcr.io/counsellorben/iverson-api:<tag> \
  --certificate-identity-regexp '^https://github\.com/counsellorben/Iverson/\.github/workflows/publish-images\.yml@refs/(heads/main|tags/v.+)$' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```

Run the same pair for `iverson-admin-ui:<tag>` and for the two model images under the tags the
chart renders:

```bash
helm dependency build Iverson.Server/deploy/helm/iverson
helm template iverson Iverson.Server/deploy/helm/iverson -f Iverson.Server/deploy/helm/iverson/values-aws.yaml \
  -f Iverson.Server/deploy/helm/iverson/values-aws.ci-override.yaml | sed -n 's/.*image: "\(ghcr\.io[^"]*-model:[^"]*\)".*/\1/p'
```

A pass means two things. The digest the tag names was signed by `publish-images.yml` running on
`main` or on a `v` tag of `counsellorben/Iverson`. The SBOM attestation for that digest came from
the same workflow. Signing writes an entry to the public Rekor transparency log, which records
the repository, workflow and ref.

The clusters do not check signatures at admission. Verification is the operator's check before a
deploy.

The publish workflow's first real run is unexercised before the push. Its acceptance check is
this section: run it against the `main` SHA tags after the first push, and against the model
images after the first manual run.

## 3. Deploying with image tags

`values-aws.yaml`, `values-gcp.yaml` and `values-azure.yaml` point `api.image.repository` and
`worker.image.repository` at `ghcr.io/counsellorben/iverson-api`, and `adminUi.image.repository` at
`ghcr.io/counsellorben/iverson-admin-ui`. They also set `global.modelImageRegistry` to
`ghcr.io/counsellorben/`.

The image tags stay operator-supplied. Set all three on every deploy, to the full commit SHA of a
`main` build or to `X.Y.Z` for a `vX.Y.Z` tag (the git ref is `vX.Y.Z`; the image tag is `X.Y.Z`). The chart's default tag `0.1.0` is never published,
and the workflow never publishes `latest`.

```bash
helm upgrade --install iverson Iverson.Server/deploy/helm/iverson -n iverson \
  -f Iverson.Server/deploy/helm/iverson/values-aws.yaml \
  --set api.image.tag=<tag>,worker.image.tag=<tag>,adminUi.image.tag=<tag>
```

The chart computes the model images' tags itself, from the model pins and each model Dockerfile.
Those images publish only on a `v` tag or a manual run, so after changing a model pin or a model
Dockerfile, run the workflow on that commit before deploying it:

```bash
gh workflow run publish-images.yml --ref main
```

A manual run also republishes `iverson-api` and `iverson-admin-ui` under the commit SHA. The
workflow refuses any ref other than `main` or a `vX.Y.Z` tag.

## 4. Branch protection

`Iverson.Server/deploy/scripts/apply-branch-protection.sh` replaces `main`'s protection in one
call. It adds the security scans to the eleven existing required checks, 22 in all, and restates
every other setting as it was. The admin bypass stays (`enforce_admins` false), and no reviews are
required. A maintainer's direct push to `main` therefore still skips the required checks (accepted).
`owasp-dependency-check-java` and kube-score's `security-audit` are not required.

Run it only when every required check is green on `main`'s latest commit. Once a check is
required, a pull request whose run of it is red or missing cannot merge. The `deploy-validate`
jobs report on `main` from this change on. The script lists the 22 checks; read their state with
the command below. A `null` conclusion means the run has not finished and is not green.

```bash
gh api 'repos/counsellorben/Iverson/commits/main/check-runs?per_page=100' --paginate \
  --jq '.check_runs[] | "\(.conclusion) \(.name)"' | sort
```

`--dry-run` prints the request body and sends nothing. Save today's protection before changing it
as a record of the previous settings. A GET response is not shaped like the PUT body, so the file
cannot be sent back to restore them.

```bash
Iverson.Server/deploy/scripts/apply-branch-protection.sh --dry-run
gh api repos/counsellorben/Iverson/branches/main/protection > "${TMPDIR:-/tmp}/protection-before.json"
```

Before the first change to Iverson, apply the body to a throwaway public repository you
administer, to confirm GitHub accepts it. The repository needs an initial commit so `main`
exists. `--add-readme` makes one on your account's default branch, which must be `main`.

```bash
gh repo create <you>/protection-trial --public --add-readme
Iverson.Server/deploy/scripts/apply-branch-protection.sh --repo <you>/protection-trial
gh api repos/<you>/protection-trial/branches/main/protection --jq '.required_status_checks.checks | length'
```

The last command prints `22`. If GitHub rejects the body, stop: the body changes before Iverson's
protection is touched. Delete the throwaway repository afterwards from its **Settings** page, or
with `gh repo delete <you>/protection-trial --yes` (this needs the `delete_repo` scope:
`gh auth refresh -s delete_repo`).

Then apply it to Iverson, the script's default repository, and read it back:

```bash
Iverson.Server/deploy/scripts/apply-branch-protection.sh
gh api repos/counsellorben/Iverson/branches/main/protection --jq '.required_status_checks.checks | length'
```

## 5. GitLab: `NVD_API_KEY`

The OWASP dependency-check job is the only GitLab job that uses `NVD_API_KEY`, and it no longer runs
in merge-request pipelines. Job rules do not limit a project-level CI/CD variable: such a variable
reaches every job of every pipeline, merge-request pipelines included. So define `NVD_API_KEY` in
one of two ways:

- as a **Protected** variable (project **Settings**, then **CI/CD**, then **Variables**; select
  **Protect variable**, and mask it), which only pipelines on protected branches and tags receive;
- or as a variable of a pipeline schedule (**Build**, then **Pipeline schedules**).

Remove any unprotected project-level `NVD_API_KEY`. GitLab's variable semantics are described at
https://docs.gitlab.com/ci/variables/.

## 6. `IVERSON_ACTING_USER_PASSWORD`

`Iverson.Server/deploy/scripts/mint_acting_user_token.py` no longer takes `--password`, because a
password on the command line is visible to other local users in the process list. It reads
`IVERSON_ACTING_USER_PASSWORD`, the variable LoadTest already uses for the same smoke-test user.

- `--target compose` requires it. Set it to the stack's smoke-test password, which is
  `IVERSON_SMOKE_TEST_PASSWORD` in `Iverson.Server/.env`.
- `--target kind` uses it when it is set. Otherwise the script reads the password from the
  `<release>-authentik-smoke-test-user` Secret, as before.

```bash
read -rs IVERSON_ACTING_USER_PASSWORD && export IVERSON_ACTING_USER_PASSWORD
python3 Iverson.Server/deploy/scripts/mint_acting_user_token.py --target compose
```

`Iverson.Server/Iverson.LoadTest/scripts/pattern_leg.py` passes the password to the script through this variable itself.

## 7. GitLab image pins

`.gitlab-ci.yml` pins every job image by digest. Dependabot does not read that file, so these pins
are bumped by hand, and GitLab's tool versions (for example govulncheck's Go toolchain) can lag
the GitHub workflows, which float.

## 8. SDK behaviour changes

- .NET: `SchemaCatalogClient(mapping, Func<...>)` is now `SchemaCatalogClient(mapping, ActingUserIdentity?)`.
  This is source-breaking for callers of the old constructor.
- .NET and Python: a hand-built coordinator refuses an ambient or per-call acting token unless it is
  told the transport is TLS or the plaintext opt-in is set. A hand-built `ActingUserIdentity` now
  defaults `refusesPlaintextTokens` to true. The DI and `IversonClient.coordinator()` paths are
  unchanged.
- Java: a TLS client no longer needs the insecure opt-in. On a plaintext channel, a call that carries
  credentials fails unless the opt-in is set, and `OAuth2ClientCredentials` needs its own
  `allowInsecureCredentials=true` on a plaintext channel.
