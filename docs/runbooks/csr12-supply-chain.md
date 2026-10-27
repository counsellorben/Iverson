# CSR Round-12 Supply Chain — Operator Runbook

**Why:** CSR round 12's supply-chain sub-project (`docs/specs/2026-10-26-csr-round12-supply-chain-design.md`)
keeps the NVD API key out of every job that runs repository or dependency code, makes release tags
admin-only, binds release signatures to their commit, has the cloud clusters check first-party images at
admission, locks the .NET and Java resolutions to recorded hashes, and gives every version pin outside
Dependabot a weekly "you are behind" signal. This runbook covers the steps after the merge, reading an
admission refusal, the Maven checksum re-record, and the pin table. Image verification by hand is
`csr11-clients-tooling-ci.md` §2; the GitLab key is its §5.

## 1. After the merge

Each step is a live change; make them in this order and confirm each before the next. Step 1 is the merge
itself.

### Step 2: the pipelines on `main`

1. **GitLab, only if a GitLab project runs this pipeline.** Define `NVD_API_KEY` once, as
   `csr11-clients-tooling-ci.md` §5 describes (Protected, Masked, Environment scope `nvd-update`), and
   delete every other definition: an unscoped project variable, a group variable, a pipeline-schedule
   variable. Before this branch no job declared `environment: nvd-update`, so a scoped variable reached
   nothing; that is why this comes after the merge. The next scheduled pipeline is the check: `nvd-update`
   pushes the cache, and `owasp-dependency-check-java` passes its first line (the key did not reach it) and
   finds the database. If the scan fails with `NoDataException`, its cache pull did not see `nvd-update`'s
   push: check the runner's cache configuration (spec §8.9).
2. **GitHub.** On the first push to `main`, `dependency-scan.yml`'s `nvd-update` runs and saves a cache
   (`nvd-<run id>`), then `owasp-dependency-check-java` runs keyless from it:

   ```bash
   gh run list --repo counsellorben/Iverson --workflow dependency-scan.yml --branch main --limit 1
   gh run view --repo counsellorben/Iverson <run id> --json jobs --jq '.jobs[] | "\(.conclusion) \(.name)"'
   ```

   Both jobs read `success`. A pull request's run then shows `nvd-update` skipped and the scan passing.
3. **Dependabot's helm jobs.** In the repository's **Insights**, then **Dependency graph**, then
   **Dependabot**, the three `helm` entries' last runs succeed (they failed on every run before, `egress not
   allowlisted`), and open the outstanding stub bumps (cloudnative-pg, Strimzi and the StarRocks operator
   at the time of writing).
4. **Pin freshness.** Run it once by hand and review the issues it opens (§4):

   ```bash
   gh workflow run pin-freshness.yml --repo counsellorben/Iverson
   gh issue list --repo counsellorben/Iverson --search '"Pin behind:" in:title'
   ```

   Without the AWS role (step 4) its log reads `skipped: no AWS role` for `vpc-cni` and `aws-ebs-csi-driver`.

### Step 3: the repository's settings

1. **Delete the Dependabot secret `NVD_API_KEY`**, only after step 2's keyless scan passed. The Actions
   secret of the same name stays (`nvd-update` reads it).

   ```bash
   gh secret delete NVD_API_KEY --repo counsellorben/Iverson --app dependabot
   ```

2. **Push protection:**

   ```bash
   gh api -X PATCH repos/counsellorben/Iverson -f 'security_and_analysis[secret_scanning_push_protection][status]=enabled' \
     --jq .security_and_analysis.secret_scanning_push_protection.status
   ```

   It prints `enabled`.
3. **The tag ruleset** (`Iverson.Server/deploy/scripts/apply-tag-ruleset.sh`). GitHub does not document the
   admin role's numeric id, so the script creates the ruleset disabled and switches it on only once its
   read-back shows the bypass applies to you:

   ```bash
   Iverson.Server/deploy/scripts/apply-tag-ruleset.sh --dry-run
   Iverson.Server/deploy/scripts/apply-tag-ruleset.sh
   ```

   The second prints `read back: enforcement=disabled current_user_can_bypass=always
   bypass_actors=RepositoryRole:5:always`. Open the repository's **Settings**, then **Rules**, then
   **Rulesets**, then `release-tags`: the bypass list must read **Repository admin**, and the target must be
   tags matching `v*`. If either is wrong, delete the ruleset there and stop; do not activate it. Otherwise:

   ```bash
   Iverson.Server/deploy/scripts/apply-tag-ruleset.sh --activate
   ```

   It prints `enforcement=active`. From then on only the admin can create, move or delete a `v*` tag. If it
   refuses because the read-back does not show `current_user_can_bypass=always` (GitHub does not document
   that value for a disabled ruleset), leave the ruleset disabled and stop here: report the read-back.
4. **Remove the second writer:**

   ```bash
   gh api -X DELETE repos/counsellorben/Iverson/collaborators/vadim82
   gh api repos/counsellorben/Iverson/collaborators --jq '.[].login'
   ```

   The last command lists only `counsellorben`.
5. **Branch protection, last,** once all 22 required checks are green on `main`
   (`csr11-clients-tooling-ci.md` §4). At the time of writing the admin-ui image's scan is red on
   `brotli-libs` until the upstream `nginx-unprivileged` image is rebuilt (spec §8.6).

### Step 4: the AWS account (for the EKS freshness rows)

`Iverson.Server/deploy/terraform/bootstrap/aws` gains GitHub's OIDC provider and the role
`iverson-pin-freshness`, which only `main`'s runs can assume and which may only call
`eks:DescribeAddonVersions`. The module also declares the Terraform state bucket `iverson-terraform-state`
(with its versioning, encryption and public-access block) and the lock table `iverson-terraform-locks`, and no
state file for the module is kept in the repository, so those must be adopted before the apply rather than
created again. An account holds one OIDC provider per URL: if yours already has
`token.actions.githubusercontent.com`, import it too.

```bash
cd Iverson.Server/deploy/terraform/bootstrap/aws && terraform init
# Both must succeed: the bucket and the table are yours. If either fails (403 or 404 for the bucket), stop:
# the name is taken by another account, and renaming it changes every cloud root's backend.
aws s3api head-bucket --bucket iverson-terraform-state
aws dynamodb describe-table --table-name iverson-terraform-locks --query Table.TableName --output text
terraform import aws_s3_bucket.state iverson-terraform-state
terraform import aws_s3_bucket_versioning.state iverson-terraform-state
terraform import aws_s3_bucket_server_side_encryption_configuration.state iverson-terraform-state
terraform import aws_s3_bucket_public_access_block.state iverson-terraform-state
terraform import aws_dynamodb_table.locks iverson-terraform-locks
aws iam list-open-id-connect-providers --query 'OpenIDConnectProviderList[].Arn' --output text
# only if an ARN ending in /token.actions.githubusercontent.com is listed:
terraform import aws_iam_openid_connect_provider.github <that ARN>
terraform plan    # must add only the role, its policy and (unless imported) the OIDC provider, and change nothing else
terraform apply
gh variable set PIN_FRESHNESS_AWS_ROLE --repo counsellorben/Iverson --body "$(terraform output -raw pin_freshness_role_arn)"
```

If `terraform plan` would create, change or destroy anything else (the bucket's settings differ from the
module, for example), stop and report it rather than apply.

The next `pin-freshness` run checks the two add-ons.

## 2. Admission verification on the clouds

On AWS, Azure and GCP the `iverson` namespace carries `policy.sigstore.dev/include=true`, and Sigstore's
policy-controller (namespace `cosign-system`) checks every pod and workload created or changed there. A
first-party image (`iverson-api`, `iverson-admin-ui`, `iverson-ollama-model`, `iverson-tei-model`) is admitted
only if `publish-images.yml` attested its digest's SPDX SBOM, and the attestation names that same image. Every
other image is admitted unverified. Every image, by tag or not, is rewritten to `tag@digest`, so the stored
pods and workload templates read `tag@digest`. kind, local and compose do not check.

**Reading a refusal.** The API server returns the engine's reason:

| Message contains | Meaning |
|---|---|
| `no matching attestations` … `expected SAN value to match regex …, got …` | not signed by this repository's publish workflow |
| `no matching attestations with type https://cyclonedx.org/bom` (or another type) | the attestation is not an SPDX SBOM |
| `subject.0.name: conflicting values "…/iverson-admin-ui" and "…/iverson-api"` | another first-party image's digest pushed under this name |
| `spec.initContainers[0].image` | the init container's image failed, not the main one |
| `must be an image digest` | the registry could not resolve the tag (for example a local, unpublished image) |
| `failed calling webhook "policy.sigstore.dev"` … `connection refused` or `context deadline exceeded` | the engine is down or slow, not a verdict on the image |

**An engine outage, not a refusal.** With the engine down, every create in `iverson` is refused (the webhooks
fail closed) and other namespaces are unaffected:

```bash
kubectl -n cosign-system get pods
kubectl -n cosign-system logs deploy/policy-controller-webhook --tail=50
```

**The TUF residual (spec §8.3).** The engine fetches Sigstore's TUF root (`tuf-repo-cdn.sigstore.dev`) when
it starts. A running engine keeps verifying through a Sigstore outage, but an engine restarted while the TUF
CDN is unreachable crash-loops (`Failed to initialize TUF client`), and first-party creates in `iverson` are
refused until the CDN returns. Wait for it, then delete the engine's pods so they start again.

**The emergency lever.** To admit pods while the engine cannot run, remove the namespace label. It is an
operator act (namespace-scoped roles cannot edit a `Namespace`), and it switches verification off for the
whole namespace; put it back as soon as the engine runs:

```bash
kubectl label namespace iverson policy.sigstore.dev/include-
kubectl label namespace iverson policy.sigstore.dev/include=true    # afterwards
```

**The model images** have not been published yet: their policies apply from their first `v*` tag or manual
publish run.

**CNPG.** Because the engine rewrites every image, the PostgreSQL image (`charts/postgres` `imageName`) and
the CNPG operator image (`modules/operators/versions/cloudnative-pg-values.yaml`) are pinned by digest; a
tag-only target would roll the instances. A cluster that ran `main`'s chart before this change rolls its
PostgreSQL instances once when the pinned `imageName` first applies (kind included). When Dependabot raises
the cloudnative-pg chart, the `cnpg-operator` and `cnpg-postgresql` pin issues (§4) say which digests to
take. Strimzi's and the StarRocks operator's workloads are not pinned (spec §8.8).

## 3. Java: the trusted-checksum record

Every Java build runs Maven 3.9.16 through `Iverson.Clients/Java/mvnw`, and every artifact it resolves
must match `Iverson.Clients/Java/.mvn/checksums/checksums-central.sha256`; an artifact that is missing from
it fails with `Missing from summaryFile trusted checksum(s)`, and one that differs with `trusted checksum
mismatch`. Dependabot does not update the record, so **each Maven Dependabot pull request needs a
re-record** before it goes green. On its branch:

```bash
cd Iverson.Clients/Java
rm .mvn/checksums/checksums-central.sha256
./mvnw -B clean install -Daether.artifactResolver.postProcessor.trustedChecksums.record=true
./mvnw -B org.owasp:dependency-check-maven:<the pom's version>:check -DautoUpdate=false \
  -Daether.artifactResolver.postProcessor.trustedChecksums.record=true
rm .mvn/checksums/checksums-central.sha256.bak   # the second run's backup of the first record
git add .mvn/checksums/checksums-central.sha256
```

The tests run in the first command, so the test runner's own plugins are recorded; the second ends in
`NoDataException` (no NVD database here), which is expected. Commit the record to the pull request.

**The record is linux-x86_64 only.** It holds only the `linux-x86_64` protoc and protoc-gen-grpc-java
binaries (they resolve by `${os.detected.classifier}`, `Iverson.Clients/Java/client/pom.xml`), and the check
runs with `failIfMissing=true`, so every `./mvnw` build on macOS, Linux arm64 or Windows fails with `Missing
from summaryFile trusted checksum(s)`. A re-record from no file keeps only the recording host's platform, so
re-record on a linux-x86_64 host.

**A Maven or Maven Wrapper bump** (a `Pin behind: maven` or `maven-wrapper` issue) changes
`.mvn/wrapper/maven-wrapper.properties`. For a wrapper bump, first regenerate the scripts and that file with the
record's check off (the plugin is not in the record): `mvn -N org.apache.maven.plugins:maven-wrapper-plugin:<version>:wrapper -Dtype=source -Dmaven=<maven version> -Daether.artifactResolver.postProcessor.trustedChecksums=false`.
It rewrites the file without its two SHA-256 lines. For a Maven bump, edit the version in `distributionUrl`.
Then download the new zip (and, for a wrapper bump, jar) from Maven Central, take its SHA-256 (`sha256sum`),
check the zip against the `.sha512` and the jar against the `.sha1` Maven Central publishes beside each (it publishes no SHA-512 for the jar), and write `distributionSha256Sum` and
`wrapperSha256Sum` (after a regeneration, append both). Then re-record as above.

## 4. The pin table

`ci/pins.yaml` lists every version pin Dependabot does not raise: where its newest version is read
(`source`) and every place it is written (`locations`).

- **`Pin behind: <name>`** issues come from `pin-freshness.yml` (weekly). To answer one, raise every
  location the issue lists in one pull request; `python3 ci/pins.py consistency` (deploy-validate's
  "Pin table consistency" step) fails until they agree. If the pin must stay behind, add a `note:` line to
  the row saying why. The next run opens a new issue while the row is behind, so a pin that stays behind
  keeps an issue open as the reminder. The issue closes itself once the row is current.
- **GitLab images.** Dependabot raises `ci/gitlab-images/Dockerfile`, never `.gitlab-ci.yml`. Its pull request
  stays red on the consistency step until the same tag and digest are copied into every matching `image:`
  line of `.gitlab-ci.yml`.
  The same applies to a Dependabot `alpine/helm` or `hashicorp/terraform` stub bump: besides the
  `.gitlab-ci.yml` copy, it needs the six `setup-helm` `version:` inputs or `terraform_version` raised
  (the `helm` and `terraform` pin rows), or the consistency step stays red.
- **`dependency-check-maven`.** A Dependabot bump needs the two `:update-only` copies (`.github/workflows/dependency-scan.yml` and
  `.gitlab-ci.yml`) raised as well as the re-record in §3.
- **A new pin** gets a row before it merges: a `version:` input belongs to the action it configures, so
  identify the action first.
