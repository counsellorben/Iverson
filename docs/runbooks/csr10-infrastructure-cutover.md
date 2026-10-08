# CSR Round-10 Infrastructure and Supply Chain — Cutover Runbook

**Why:** this change tightens cluster network policy, pulls model weights from pre-built images
instead of at pod start, moves AKS and the Terraform state account to Entra-only access, and
pins build inputs. Several steps are one-way or need an ordering that a plain `helm upgrade` or
`terraform apply` will not give you. Work through the sections in order for the clouds you run.

**AKS: order.** On an existing AKS cluster, do not work through the sections in numbered order.
Use this order instead: §7 (state account, ending with the `terraform init -reconfigure` that the
backend change forces) → §6 (Terraform: the Entra and API Server VNet Integration apply) →
§2 and §3 → §4 → §5 → §9. Two dependencies drive it. The Azure overlay's `networkPolicy.apiServerCidrs`
(`10.1.17.0/28`) is the VNet Integration subnet that the §6 Terraform change creates, so a Helm
upgrade before that apply allows no API-server traffic. The Azure backend authenticates with Entra
(`use_azuread_auth = true`), which needs the data-plane role that §7 grants, so §6 cannot
initialise before §7. EKS and GKE keep the numbered order.

## 1. Prerequisites

- `kubelogin` on PATH for the Azure root (`azure/`). The kubeconfig that `az aks get-credentials`
  returns now calls it.
- The Entra admin group's object ID, for the Azure root variable `cluster_admin_group_object_ids`.
- The deployer's public IP as a CIDR, for the bootstrap variable `state_authorized_ip_ranges`.
- A container registry the cluster can pull from, for the model images.

## 2. Model images

The TEI and Ollama pods now start from images that already contain the model. Image tags:

- `iverson-tei-model:<slug>-<rev12>-<dockerfile12>`
- `iverson-ollama-model:<model>-<digest12>-<dockerfile12>`

The tag changes whenever a model pin or a model Dockerfile changes, so rebuild and push before
every `helm upgrade` that touches either.

Dockerfiles: `Iverson.Server/deploy/helm/iverson/charts/{tei,ollama}/model-image/Dockerfile`.

**kind:**

```bash
Iverson.Server/deploy/kind/build-and-load-image.sh 0.1.0 <cluster> --model-images --values <overlay>
# PowerShell: build-and-load-image.ps1 -ModelImages -ClusterName <cluster> -Values <overlay>
```

**Cloud:** set `global.modelImageRegistry` to the registry prefix **with its trailing `/`**
(for example `myregistry.azurecr.io/`) in the values you deploy with. The default is empty (local
images). Then build and push each image with the inputs the chart renders, read the same way
`build-and-load-image.sh --model-images` reads them, rather than copying them by hand:

```bash
C=Iverson.Server/deploy/helm/iverson
helm dependency build "$C"
helm template <release> "$C" -f <values-file> > render.yaml
# TEI, one line per embedding model: <image>|<MODEL_ID>|<REVISION>
awk '/image: "[^"]*iverson-tei-model:/ { match($0, /"[^"]+"/); img = substr($0, RSTART+1, RLENGTH-2) }
     /args: \["--model-id"/ { split($0, a, "\""); print img "|" a[4] "|" a[8] }' render.yaml
# Ollama: the image, then MODEL and DIGEST from the StatefulSet annotations
sed -n 's/.*image: "\([^"]*iverson-ollama-model:[^"]*\)".*/\1/p' render.yaml | head -1
sed -n 's/.*iverson.io\/model: "\(.*\)"/\1/p' render.yaml | head -1
sed -n 's/.*iverson.io\/model-digest: "\(.*\)"/\1/p' render.yaml | head -1
```

For example, Azure values (`values-azure.yaml` plus your real `ingressHost` and the other
placeholders it requires) with `global.modelImageRegistry: myregistry.azurecr.io/` render
the inputs below at the time of writing (the tags embed a hash of each Dockerfile, so take yours
from the commands above):

```bash
docker build --memory 3g -t myregistry.azurecr.io/iverson-tei-model:bge-base-a5beb1e3e68b-e8355217f84b \
  --build-arg MODEL_ID=BAAI/bge-base-en-v1.5 --build-arg REVISION=a5beb1e3e68b9ab74eb54cfd186867f64f240e1a \
  -f "$C/charts/tei/model-image/Dockerfile" "$C/charts/tei/model-image"
docker push myregistry.azurecr.io/iverson-tei-model:bge-base-a5beb1e3e68b-e8355217f84b

docker build --memory 3g -t myregistry.azurecr.io/iverson-ollama-model:qwen2.5-3b-357c53fb659c-e21831a0bbe1 \
  --build-arg MODEL=qwen2.5:3b --build-arg DIGEST=357c53fb659c5076de1d65ccb0b397446227b71a42be9d1603d46168015c9e4b \
  -f "$C/charts/ollama/model-image/Dockerfile" "$C/charts/ollama/model-image"
docker push myregistry.azurecr.io/iverson-ollama-model:qwen2.5-3b-357c53fb659c-e21831a0bbe1
```

Expected: `helm template` renders `image: "<registry>/iverson-tei-model:..."` and
`image: "<registry>/iverson-ollama-model:..."`, and each tag exists in the registry.

## 3. Upgrading an existing release

On AKS, do this section only after §6 and §7; see "AKS: order" at the top.

The model StatefulSets' `volumeClaimTemplates` changed, and that field is immutable. Delete the
StatefulSets and their PVCs, then upgrade. The model data is re-created from the images.

```bash
kubectl -n <ns> delete sts <release>-ollama <release>-tei-<slug>   # one TEI StatefulSet per embedding model slug
kubectl -n <ns> delete pvc -l app=<release>-ollama
kubectl -n <ns> delete pvc -l app=<release>-tei-<slug>
# If the label selectors match nothing, delete the claims by name (one per replica ordinal):
kubectl -n <ns> get pvc -o name \
  | grep -E '/(ollama-data-<release>-ollama|tei-data-<release>-tei-<slug>)-[0-9]+$' \
  | xargs -r kubectl -n <ns> delete
helm upgrade <release> Iverson.Server/deploy/helm/iverson -f <values-file> -n <ns>
```

Expected: the PVCs (`ollama-data-<release>-ollama-0`, `tei-data-<release>-tei-<slug>-0`) are
gone before the upgrade, and the new StatefulSets reach Ready. Embedding and generation are
unavailable between the delete and Ready.

Terraform (every cloud): the next `terraform apply` deletes the now-unused `iverson-ollama` and
`iverson-tei` StorageClasses and drops them from the PVC StorageClass allow-list. Already-bound
volumes are unaffected, and the model PVCs are removed by the commands above.

## 4. First-deploy gate (every cloud)

Network policy now scopes DNS and API-server egress. Before declaring the deploy good, on each
cloud:

```bash
kubectl -n <ns> exec deploy/<release>-api -- getent hosts kubernetes.default
kubectl -n <ns> get cluster.postgresql.cnpg.io <release>-postgres
kubectl -n <ns> get pods     # Kafka broker pods
kubectl get endpointslices -n default -l kubernetes.io/service-name=kubernetes
```

Expected:

- `kubernetes.default` resolves. If the image has no `getent`, run it from a debug container
  sharing the pod (`kubectl debug`).
- The CNPG cluster reports a healthy phase.
- The Kafka broker pods are Ready.
- The EndpointSlice address falls inside the profile's `networkPolicy.apiServerCidrs`. On GKE this
  confirms the inferred endpoint.

If a check fails, apply these fallbacks by values for that profile, in order:

1. Add the `kubernetes` Service ClusterIP as a `/32` to `networkPolicy.apiServerCidrs`
   (`kubectl get svc kubernetes -n default`).
2. If still blocked, set `networkPolicy.apiServerAnyDestination: true`.
3. For DNS failures, set `networkPolicy.dnsAnyDestination: true`.

A profile that keeps a fallback leaves that half of CSR round-10 finding #17 open there; record it.

## 5. AKS ingress readiness

After deploy, every pod must reach Ready. If the api, admin-ui or Authentik pods stay unready on
probe timeouts, Azure NPM is not exempting node traffic. Add the node subnet `10.1.0.0/20` to
`networkPolicy.clusterCidrs` in the Azure values and upgrade.

## 6. AKS Entra cutover

On an existing cluster, do §7 first and this section before §2 and §3; see "AKS: order" at the top.

The first apply of `azure/` after this change:

- swaps the control-plane identity (in place), and
- enables API Server VNet Integration. This is one-way; the API server IP changes, the hostname
  stays.

Set `cluster_admin_group_object_ids` to the admin group's object ID before applying.

If the operators module fails with 403 right after the role assignment, wait five minutes and
re-apply.

Afterwards, rotate cluster certificates once, so certificates copied from old state stop working:

```bash
az aks rotate-certs -g <rg> -n <cluster>
```

Expected: `az aks get-credentials` now returns a kubelogin-based kubeconfig, and `kubectl get nodes`
works for a member of the admin group.

## 7. Terraform state account (Azure)

In a single apply, Terraform updates the storage account (shared keys off, network default deny)
before it creates the data role `azurerm_role_assignment.deployer_state_data`. An **existing**
account therefore needs the role first, out of band. A **new** account needs none of this: the
single apply creates the account, the role, then the container.

For an existing account, in order:

1. Create the role assignment and note the assignment id it prints:

   ```bash
   az role assignment create --role "Storage Blob Data Contributor" \
     --assignee <deployer object id> --scope <state storage account id>
   ```

2. Wait about five minutes for the assignment to propagate.
3. Import it, in `bootstrap/azure`:

   ```bash
   terraform import azurerm_role_assignment.deployer_state_data <assignment id>
   ```

4. Apply `bootstrap/azure` with your IP in `state_authorized_ip_ranges`.

Then re-initialise the Azure root, whose backend now uses Entra auth (`use_azuread_auth = true`):

```bash
cd Iverson.Server/deploy/terraform/azure && terraform init -reconfigure
```

Expected: `terraform init` succeeds without a storage account key, from an IP in the allowlist.

## 8. EKS

The instance metadata hop-limit change replaces the node groups' launch template version, so
nodes roll. After the roll, check:

```bash
kubectl -n kube-system get pods     # aws-load-balancer-controller and ebs-csi-* are Running
kubectl -n <ns> get pvc             # a PVC binds
```

Expected: the load-balancer controller and EBS CSI pods are Running and a PVC reaches Bound.

## 9. Orchestrator workload identity (C2)

The API now authenticates to Authentik's admin API with its own Kubernetes service-account token, exchanged for a 5-minute access token, and the static orchestrator token is removed. A single `helm upgrade` switches it over. Until both blueprints below have applied in the new Authentik worker, `CreateTenant` and `InviteUser` can fail; no data is affected. If the old worker (which has no JWKS relay) took the apply, that can last until the next hourly blueprint discovery, which step 3 shortcuts. This section follows the §3 upgrade on every profile, kind and laptop included.

1. Do not provision tenants while the upgrade rolls out.
2. Wait until the new worker pod is the only one. Run `kubectl -n <ns> rollout status deploy/<release>-authentik-worker`, then repeat `kubectl -n <ns> get pods -l app=<release>-authentik-worker` until it lists exactly one pod and that pod shows `2/2` (the new pod carries the `jwks-relay` sidecar; the old one shows `1/1`). `helm --wait` and `rollout status` both return while the old pod is still Terminating, and `kubectl exec deploy/…` can pick that old pod, which has no relay and may still mount the previous blueprints.
3. Apply both blueprints in the new worker container:

   ```bash
   kubectl -n <ns> exec deploy/<release>-authentik-worker -c worker -- \
     ak apply_blueprint custom/service-clients/service-clients.yaml custom/orchestrator-federation.yaml
   ```

   It exits non-zero if either blueprint is invalid. A failure that names `oidc_jwks_url` means the relay could not read the cluster's keys; check the `jwks-relay` container's log and the worker's API-server egress (§4).
4. Delete the two orchestrator Secrets. They carry `helm.sh/resource-policy: keep`, so the upgrade leaves them behind. They exist only on clusters installed before this change; a fresh install never creates them, which is why the delete uses `--ignore-not-found`:

   ```bash
   kubectl -n <ns> delete secret --ignore-not-found <release>-authentik-admin-orchestrator-token <release>-authentik-admin-orchestrator-user
   ```

5. Confirm with one `InviteUser` (or `CreateTenant`) that a recovery link comes back.
6. Confirm both Secrets are gone: `kubectl -n <ns> get secret <release>-authentik-admin-orchestrator-token <release>-authentik-admin-orchestrator-user` reports `NotFound` for each.
