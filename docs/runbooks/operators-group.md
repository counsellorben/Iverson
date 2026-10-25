# The operators group (CSR round 12 §3.1)

The chart renders a ValidatingAdmissionPolicy, `<release>-<namespace>-sensitive-identities`
(`templates/admission-policy.yaml`), bound to the release namespace. It admits the actions below only
for a requester in one of the groups `admission.operatorGroups` names, and refuses everyone else,
cluster-admin included:

- creating a token for `<release>-api` or `<release>-admin-automation` (`kubectl create token`);
- exec, attach or port-forward into a pod named `<release>-api-*`, `<release>-worker-*`,
  `<release>-authentik-*` or `<release>-postgres-*`, and the API server's proxy (`pods/proxy`) to one;
- the API server's proxy (`services/proxy`) to any Service in the namespace, whatever its name: a
  Service's name says nothing about its backends, since anyone who can edit Services can point one at a
  protected pod;
- creating a pod that runs as either account; even for the group, only under a name starting
  `<release>-api-` (so `kubectl debug --copy-to` and `kubectl run --overrides` need such a name);
- creating or updating a Deployment, ReplicaSet, StatefulSet, DaemonSet, Job or CronJob whose pod
  template runs as either account: a `kubectl rollout restart` of the api, and a `helm upgrade` that
  changes the api's template (a chart-version or api template change does). Scaling through the
  `scale` subresource is not affected. Any other create or update of such an object needs the group,
  even one that changes nothing in its pod template; only an upgrade that leaves those workload objects
  entirely unchanged (Helm sends them no update) needs no group;
- adding an ephemeral container (`kubectl debug`) to one of those pods, or changing a container
  image of one. Under Pod Security `baseline`, `kubectl debug` also needs `--profile=baseline`.

The kubelet, and the Kubernetes controllers that create and maintain those workloads' pods, are
admitted as before. No Role or RoleBinding is added. Installing or upgrading the chart writes
cluster-scoped objects (the policy and its binding, and Authentik's TokenReview ClusterRole and
binding), so the installer needs cluster-scoped write on them: on AKS an Entra cluster-scope role.

## 1. Running as the operators group

Commands in the runbooks written `kubectl $OPS …` or `helm … $HOPS` must run as the group.

**kind and laptop.** The group is `iverson-operators`. kind's kubeconfig user, `kubernetes-admin` in
group `kubeadm:cluster-admins`, is not in it, so impersonate it together with your own group:

```bash
OPS='--as=kubernetes-admin --as-group=kubeadm:cluster-admins --as-group=iverson-operators'
HOPS='--kube-as-user=kubernetes-admin --kube-as-group=kubeadm:cluster-admins --kube-as-group=iverson-operators'
```

Helm needs `--kube-as-user` together with `--kube-as-group`; the group flag alone is refused.
`deploy/kind/setup.sh` prints the install command with these flags.

**AWS, Azure and GCP.** Set both empty (`OPS= HOPS=`): an operator's own identity carries the group.
- **AWS:** an IAM role in Terraform's `operator_role_arns` (`modules/cluster-aws`). Its access entry
  gives it the Kubernetes group `iverson-operators` and `AmazonEKSClusterAdminPolicy` at cluster scope.
  The Terraform caller's own entry is not one of them; an IAM principal can be in one access entry
  only.
- **Azure:** a member of an Entra group in `cluster_admin_group_object_ids`. `values-azure.yaml`'s
  `admission.operatorGroups` lists the same object IDs (Terraform output `admission_operator_groups`).
- **GCP:** a member of the Google Group that `values-gcp.yaml`'s `admission.operatorGroups` names. That
  group is a member of the `gke-security-groups@<domain>` group Terraform's `rbac_security_group`
  names (Google Groups for RBAC); its Kubernetes rights come from project IAM outside Terraform.

On a cloud the group plumbing is checked at first deploy, not before: a mismatch fails closed, as
refused commands and FailedCreate events.

## 2. Postgres

`kubectl cnpg psql` does not pass impersonation flags to the `kubectl exec` it starts. On kind, exec
into the primary instance's `postgres` container directly (add `-it` for an interactive session):

```bash
PRIMARY="$(kubectl -n <ns> get cluster.postgresql.cnpg.io <release>-postgres -o jsonpath='{.status.currentPrimary}')"
kubectl $OPS -n <ns> exec "$PRIMARY" -c postgres -- psql -U postgres -d iverson
```

On a cloud, `kubectl cnpg psql <release>-postgres -n <ns> -- -d iverson` works for an operator as
before.
