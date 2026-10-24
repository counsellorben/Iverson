# CSR Round-11 Transport and Network — Operator Runbook

> **CSR round 12 §3.1.** Commands written `kubectl $OPS …` or `helm … $HOPS` run as the operators group: set `OPS` and `HOPS` as `docs/runbooks/operators-group.md` §1 says (both empty on a cloud).

**Why:** the hops that carried passwords, tokens and tenant data in plaintext inside the cluster are
now encrypted. The edge reaches the API and Authentik's public listener over TLS, StarRocks clients
verify the FE's certificate, and Authentik verifies Postgres's. Redis is gone (Authentik no longer
uses it). GCP no longer serves HTTP on port 80, and Azure redirects it. The `ingress-nginx`
NetworkPolicy peer exists only where an nginx Ingress fronts the pod, and an any-destination
fallback needs an explicit acknowledgement. Compose is unchanged apart from Redis.

## 1. What the upgrade does

One `helm upgrade` switches each hop. The API pods and the Authentik server pod restart with TLS
in the same release as the Ingress annotations. From the Ingress update until the new pods are
Ready, the API and Authentik's public login are unavailable at the edge: the Ingress already speaks
TLS to the backends while the old pods still serve plaintext, and Authentik runs a single server
pod. On profiles that deploy StarRocks, new API pods become Ready only once an FE serves TLS, so
the outage lasts through the first FE restart plus API startup. Plan a maintenance window.
The StarRocks FE pods restart with a keystore and a new `fe.conf`. Until they are back, the
create-user Job, the API and the worker treat the still-plaintext FE as not ready and retry. The
Job may fail and be retried by Kubernetes while FE pods restart; the upgrade completes once every
FE serves TLS (raise `--timeout` if the FE restarts take longer than its default 5 minutes).

This change removes a subchart and moves a template, so run `helm dependency build` in the chart
directory before the upgrade; a stale `charts/*.tgz` would render Redis and a duplicate prometheus
ConfigMap again.

After the upgrade, delete the orphaned Redis Secret. Its `resource-policy: keep` stops Helm from
deleting it:

```bash
kubectl -n <ns> delete secret <release>-redis
```

## 2. Azure: trusted roots (three steps)

The Application Gateway uses HTTPS to a pod only if it holds the CA that signed the pod's
certificate. The chart generates those CAs, so Azure takes three steps. Until step 3,
the Azure edge reaches the API and Authentik in plaintext, and the chart refuses an upgrade that
skips it.

1. `helm install` (or the first `helm upgrade` carrying this change). This creates
   `<release>-api-internal-tls`.
2. Copy both CAs into the Terraform variable `appgw_backend_trusted_roots` (azure root), and apply:

   ```bash
   kubectl -n <ns> get secret <release>-api-internal-tls -o jsonpath='{.data.ca\.crt}' | base64 -d > api-ca.pem
   kubectl -n <ns> get secret <release>-authentik-internal-tls -o jsonpath='{.data.ca\.crt}' | base64 -d > authentik-ca.pem
   ```

   A `.tfvars` file cannot call `file()`, so write the map as JSON beside the azure root, then
   run `terraform apply` there:

   ```bash
   python3 -c 'import json; json.dump({"appgw_backend_trusted_roots": {"iverson-api-ca": open("api-ca.pem").read(), "iverson-authentik-ca": open("authentik-ca.pem").read()}}, open("trusted-roots.auto.tfvars.json", "w"), indent=2)'
   ```
3. Set `api.ingress.appgwTrustedRootCertificate: "iverson-api-ca"` and
   `authentik.ingress.appgwTrustedRootCertificate: "iverson-authentik-ca"` in `values-azure.yaml`,
   then `helm upgrade`. Then restart Prometheus, which reads its scrape config only at start and
   would otherwise keep scraping the API over plain HTTP:

   ```bash
   kubectl -n <ns> rollout restart deployment/<release>-prometheus
   ```

## 3. Rotating a chart certificate

Each chart CA's key is discarded after it signs its one certificate. To rotate, delete the Secret,
upgrade, then restart its pods. `<release>-starrocks-tls` is the exception: its FE pods restart
while the upgrade is still running, as its row says.

| Secret | Restart |
|---|---|
| `<release>-api-internal-tls` | the API Deployment. Prometheus picks up the new CA file without a restart. |
| `<release>-authentik-internal-tls` | the Authentik server Deployment and the API Deployment |
| `<release>-starrocks-tls` | the FE pods while `helm upgrade` is still waiting on its create-user hook (`kubectl -n <ns> delete pod -l app.starrocks.ownerreference/name=<release>-starrocks-fe` once the upgrade has recreated the Secret). The hook waits until FE serves the new certificate, so the upgrade completes only after the FE pods restart; raise `--timeout` if they need more than the default 5 minutes. Then restart the API and worker Deployments. |

On Azure the gateway trusts only the CA in `appgw_backend_trusted_roots`. The new CA exists only
after the upgrade has regenerated the Secret. So, after the upgrade, copy its new `ca.crt` over the
matching PEM, as in §2 step 2, apply, and then restart the pods. The Azure edge to that pod fails
from the apply until the restarted pods are Ready.

The StarRocks operator does not restart the FE when only its `fe.conf` ConfigMap changes. After any
later edit to `charts/starrocks/templates/configmap-fe.yaml`, delete the FE pods as above.

## 4. Any-destination fallbacks

`networkPolicy.dnsAnyDestination` and `networkPolicy.apiServerAnyDestination` now render only
together with `networkPolicy.keepFallbacks: true`. Setting it records that the profile keeps the
fallback open, as `docs/runbooks/csr10-infrastructure-cutover.md` §4 requires. Remove all three
once the narrower rule works.

## 5. Reaching the API from a workstation

The API's ports 8080 (gRPC) and 8081 (admin, health, metrics) now serve only TLS, from a
certificate named `<release>-api`. Through a port-forward, verify that name:

```bash
kubectl $OPS -n <ns> port-forward svc/<release>-api 8081:8081 &
kubectl -n <ns> get secret <release>-api-internal-tls -o jsonpath='{.data.ca\.crt}' | base64 -d > api-ca.pem
curl --cacert api-ca.pem --connect-to <release>-api:8081:127.0.0.1:8081 https://<release>-api:8081/health
```

On Azure before step 3 of §2, the API still serves plain HTTP.
