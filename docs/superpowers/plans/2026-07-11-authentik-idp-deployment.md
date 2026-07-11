# Authentik IdP Deployment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up Authentik as infrastructure — a reachable, TOTP+WebAuthn-MFA-enforced identity
provider exposing SAML, OAuth2/OIDC (with PKCE), deployed everywhere Iverson already deploys
(docker-compose, kind, AWS/Azure/GCP) — with zero changes to Iverson's own application code.

**Architecture:** Two new hand-rolled Helm subcharts (`redis`, `authentik`), following this repo's
established pattern (Jaeger/Prometheus): stateless Deployments + Services + ConfigMaps, no
upstream operator. Authentik's Postgres database and login role are provisioned declaratively on
the existing single CloudNativePG cluster via CNPG's `Database` CRD and `spec.managed.roles` (no
new Postgres cluster). Authentik's own configuration (MFA-enforcement flow, OAuth2/PKCE provider,
SAML provider) is captured as versioned Blueprint YAML — configured once via the Admin UI, then
exported and committed, so every future deploy reproduces it with zero manual steps.

**Tech Stack:** Helm, CloudNativePG (`postgresql.cnpg.io/v1`), Authentik
(`ghcr.io/goauthentik/server:2026.5.3`), Redis (`redis:7.4-alpine`), docker-compose.

## Global Constraints

- Chart/subchart versioning: every new subchart's `Chart.yaml` uses `version: 0.1.0`, matching
  every existing subchart in `deploy/helm/iverson/charts/`.
- Image pins: Authentik `2026.5.3`, Redis `7.4-alpine` — exact tags, no `:latest`.
- No `PersistentVolumeClaim` for Authentik or Redis — Authentik's durable state lives entirely in
  the shared CNPG Postgres cluster; Redis holds only cache/session data, cache-only, ephemeral by
  design.
- No changes to any file under `Iverson.Server/Iverson.Api/`, `Iverson.Server/Iverson.*` (any C#
  project), any `.proto` file, or any client SDK — this plan is infrastructure-only.
- No `values-aws.yaml`/`values-azure.yaml`/`values-gcp.yaml` changes — neither new component has a
  cloud-specific `StorageClassName` to override.
- Run all Helm validation from `Iverson.Server/deploy/helm/iverson/` (this repo's established
  location for the umbrella chart).
- Run all docker-compose validation from `Iverson.Server/` (this repo's established location for
  `docker-compose.yml`).
- Per this repo's established convention (`feedback-helm-subchart-tgz-cache`): re-run
  `helm dependency build` after every edit to a subchart's templates, before validating with
  `helm template` — the built `charts/*.tgz` silently shadows live template edits otherwise.

---

## Task 1: Redis subchart

**Files:**
- Create: `deploy/helm/iverson/charts/redis/Chart.yaml`
- Create: `deploy/helm/iverson/charts/redis/values.yaml`
- Create: `deploy/helm/iverson/charts/redis/templates/deployment.yaml`
- Create: `deploy/helm/iverson/charts/redis/templates/service.yaml`

**Interfaces:**
- Produces: a `Service` named `{{ .Release.Name }}-redis` on port `6379`, consumed by Task 3's
  Authentik Deployments via `AUTHENTIK_REDIS__HOST`.

- [ ] **Step 1: Create `charts/redis/Chart.yaml`**

```yaml
apiVersion: v2
name: redis
description: Redis cache/session broker for Authentik (Iverson identity provider)
type: application
version: 0.1.0
appVersion: "7.4"
```

- [ ] **Step 2: Create `charts/redis/values.yaml`**

```yaml
imageTag: "7.4-alpine"
resources:
  requests: { cpu: "100m", memory: "128Mi" }
  limits: { cpu: "250m", memory: "256Mi" }
nodeSelector: {}
```

- [ ] **Step 3: Create `charts/redis/templates/deployment.yaml`**

```yaml
apiVersion: v1
kind: ServiceAccount
metadata:
  name: {{ .Release.Name }}-redis
automountServiceAccountToken: false
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: {{ .Release.Name }}-redis
  annotations:
    # single-replica, no clustering: cache-only with no durability requirement, same reasoning
    # as charts/prometheus's kube-score ignore.
    kube-score/ignore: deployment-replicas
spec:
  replicas: 1
  selector:
    matchLabels:
      app: {{ .Release.Name }}-redis
  template:
    metadata:
      labels:
        app: {{ .Release.Name }}-redis
    spec:
      serviceAccountName: {{ .Release.Name }}-redis
      automountServiceAccountToken: false
      nodeSelector:
        {{- toYaml .Values.nodeSelector | nindent 8 }}
      securityContext:
        runAsNonRoot: true
        runAsUser: 999
        runAsGroup: 999
        fsGroup: 999
        seccompProfile: { type: RuntimeDefault }
      containers:
        - name: redis
          image: "redis:{{ .Values.imageTag }}"
          # Disables RDB snapshotting entirely: this deployment mounts no volume at /data (no
          # PVC, cache-only by design), so the image's default periodic `save` directives would
          # otherwise fail every snapshot attempt against a read-only root filesystem with no
          # writable /data — harmless to correctness (Redis keeps serving from memory) but noisy,
          # recurring error logs. Explicit `--save ""` matches the "cache-only, no durability"
          # decision instead of silently working around it.
          args: ["--save", ""]
          securityContext:
            allowPrivilegeEscalation: false
            readOnlyRootFilesystem: true
            capabilities: { drop: ["ALL"] }
          ports:
            - containerPort: 6379
              name: redis
          resources:
            requests:
              cpu: {{ .Values.resources.requests.cpu | quote }}
              memory: {{ .Values.resources.requests.memory | quote }}
            limits:
              cpu: {{ .Values.resources.limits.cpu | quote }}
              memory: {{ .Values.resources.limits.memory | quote }}
          readinessProbe:
            exec: { command: ["redis-cli", "ping"] }
            initialDelaySeconds: 5
            periodSeconds: 10
          livenessProbe:
            exec: { command: ["redis-cli", "ping"] }
            initialDelaySeconds: 10
            periodSeconds: 15
          volumeMounts:
            - name: tmp
              mountPath: /tmp
      volumes:
        - name: tmp
          emptyDir: {}
```

(`readOnlyRootFilesystem: true` is safe here — stock `redis:*-alpine` writes only to `/data` and
`/tmp`; since this deployment has no volume mounted at `/data` at all — cache-only, no
persistence — Redis runs entirely in-memory and never touches disk except `/tmp`, which the
`emptyDir` above covers.)

- [ ] **Step 4: Create `charts/redis/templates/service.yaml`**

```yaml
apiVersion: v1
kind: Service
metadata:
  name: {{ .Release.Name }}-redis
spec:
  selector:
    app: {{ .Release.Name }}-redis
  ports:
    - name: redis
      port: 6379
```

- [ ] **Step 5: Validate the subchart renders**

```bash
cd deploy/helm/iverson
helm template redis-test charts/redis/
```

Expected: renders without error; output contains `kind: ServiceAccount`, `kind: Deployment`,
`kind: Service`.

- [ ] **Step 6: Commit**

```bash
git add deploy/helm/iverson/charts/redis/
git commit -m "feat(helm): add hand-rolled Redis subchart (cache/session broker for Authentik)"
```

---

## Task 2: Postgres role + database for Authentik

**Files:**
- Modify: `deploy/helm/iverson/charts/postgres/templates/cluster.yaml`
- Create: `deploy/helm/iverson/charts/authentik/Chart.yaml`
- Create: `deploy/helm/iverson/charts/authentik/templates/secret-postgres.yaml`
- Create: `deploy/helm/iverson/charts/authentik/templates/database.yaml`

**Interfaces:**
- Consumes: the existing CNPG `Cluster` resource named `{{ .Release.Name }}-postgres`
  (`charts/postgres/templates/cluster.yaml`).
- Produces: a `Secret` named `{{ .Release.Name }}-authentik-postgres` (keys `username`,
  `password`), a CNPG-managed login role `authentik`, and a database `authentik` on the existing
  cluster — consumed by Task 3's Authentik Deployments.

This task creates the `authentik` subchart directory for the first time — later tasks add more
files to it, this one only adds the Postgres-wiring pieces.

- [ ] **Step 1: Add the declarative `authentik` role to the existing Postgres cluster**

In `deploy/helm/iverson/charts/postgres/templates/cluster.yaml`, add a `managed:` block after the
existing `bootstrap:` block (do not modify `bootstrap:` itself — it only runs once, at cluster
creation, and this is an existing, already-created cluster):

```yaml
  bootstrap:
    initdb:
      database: iverson
      owner: iverson
  managed:
    roles:
      - name: authentik
        ensure: present
        login: true
        passwordSecret:
          name: {{ .Release.Name }}-authentik-postgres
  postgresql:
```

(The full file's `postgresql:` block and everything below it is unchanged — only the new
`managed:` block is inserted between `bootstrap:` and `postgresql:`.)

- [ ] **Step 2: Create `charts/authentik/Chart.yaml`**

```yaml
apiVersion: v2
name: authentik
description: Authentik identity provider for Iverson (SAML/OAuth2+PKCE/MFA)
type: application
version: 0.1.0
appVersion: "2026.5.3"
```

- [ ] **Step 3: Create the role's password Secret**

`deploy/helm/iverson/charts/authentik/templates/secret-postgres.yaml`:

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: {{ .Release.Name }}-authentik-postgres
  annotations:
    "helm.sh/resource-policy": keep
type: kubernetes.io/basic-auth
data:
{{- $existing := lookup "v1" "Secret" .Release.Namespace (printf "%s-authentik-postgres" .Release.Name) }}
{{- if $existing }}
  username: {{ index $existing.data "username" }}
  password: {{ index $existing.data "password" }}
{{- else }}
  username: {{ "authentik" | b64enc }}
  password: {{ randAlphaNum 32 | b64enc }}
{{- end }}
```

(Mirrors the `lookup`-guarded pattern in `charts/starrocks/templates/secret.yaml` exactly — the
password is generated once and preserved across every future `helm upgrade`, never rotated
silently. `type: kubernetes.io/basic-auth` is the standard Kubernetes secret type for exactly this
`username`+`password` shape, which is what CNPG's `passwordSecret` expects.)

- [ ] **Step 4: Create the declarative `Database`**

`deploy/helm/iverson/charts/authentik/templates/database.yaml`:

```yaml
apiVersion: postgresql.cnpg.io/v1
kind: Database
metadata:
  name: {{ .Release.Name }}-authentik
spec:
  name: authentik
  owner: authentik
  cluster:
    name: {{ .Release.Name }}-postgres
```

- [ ] **Step 5: Validate rendering**

```bash
cd deploy/helm/iverson
helm dependency build
helm template iverson . -f values.yaml -f values-local.yaml | grep -A5 "kind: Database"
helm template iverson . -f values.yaml -f values-local.yaml | grep -A8 "managed:"
```

Expected: the `Database` object renders with `owner: authentik`, `cluster.name: iverson-postgres`;
the `Cluster` object's `managed.roles` block renders with `name: authentik`, `login: true`.

- [ ] **Step 6: If a live cluster is available, verify the role/database actually provision**

```bash
kubectl get databases.postgresql.cnpg.io -n <namespace>
kubectl describe cluster <release>-postgres -n <namespace> | grep -A5 "Roles Status"
```

Expected: the `Database` object reaches `Ready` (may take up to ~30s for CNPG to reconcile); the
`Cluster`'s roles status shows `authentik` as `reconciled`. If no live cluster is available in this
environment, skip this step and note it as deferred to Task 7's kind smoke test.

- [ ] **Step 7: Commit**

```bash
git add deploy/helm/iverson/charts/postgres/templates/cluster.yaml \
        deploy/helm/iverson/charts/authentik/Chart.yaml \
        deploy/helm/iverson/charts/authentik/templates/secret-postgres.yaml \
        deploy/helm/iverson/charts/authentik/templates/database.yaml
git commit -m "feat(helm): provision the authentik Postgres role and database on the existing CNPG cluster"
```

---

## Task 3: Authentik subchart — server + worker Deployments

**Files:**
- Create: `deploy/helm/iverson/charts/authentik/values.yaml`
- Create: `deploy/helm/iverson/charts/authentik/templates/secret.yaml`
- Create: `deploy/helm/iverson/charts/authentik/templates/configmap.yaml`
- Create: `deploy/helm/iverson/charts/authentik/templates/blueprints-configmap.yaml`
- Create: `deploy/helm/iverson/charts/authentik/templates/deployment-server.yaml`
- Create: `deploy/helm/iverson/charts/authentik/templates/deployment-worker.yaml`
- Create: `deploy/helm/iverson/charts/authentik/templates/service.yaml`
- Create: `deploy/helm/iverson/charts/authentik/blueprints/.gitkeep`

**Interfaces:**
- Consumes: `{{ .Release.Name }}-redis` Service (Task 1), `{{ .Release.Name }}-authentik-postgres`
  Secret + `authentik` database (Task 2).
- Produces: a `Service` named `{{ .Release.Name }}-authentik` on port `9000`; pod labels
  `app: {{ .Release.Name }}-authentik-server` and `app: {{ .Release.Name }}-authentik-worker`
  (used by Task 4's NetworkPolicy); a `ConfigMap` named `{{ .Release.Name }}-authentik-blueprints`
  mounted at `/blueprints/custom` in both containers (populated by Task 6).

- [ ] **Step 1: Create `charts/authentik/values.yaml`**

```yaml
imageTag: "2026.5.3"
resources:
  server:
    requests: { cpu: "250m", memory: "512Mi" }
    limits: { cpu: "500m", memory: "1Gi" }
  worker:
    requests: { cpu: "250m", memory: "512Mi" }
    limits: { cpu: "500m", memory: "1Gi" }
nodeSelector: {}
bootstrapEmail: "admin@iverson.local"
```

- [ ] **Step 2: Create the app Secret (signing key + bootstrap credentials)**

`deploy/helm/iverson/charts/authentik/templates/secret.yaml`:

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: {{ .Release.Name }}-authentik-app
  annotations:
    "helm.sh/resource-policy": keep
type: Opaque
data:
{{- $existing := lookup "v1" "Secret" .Release.Namespace (printf "%s-authentik-app" .Release.Name) }}
{{- if $existing }}
  secret-key: {{ index $existing.data "secret-key" }}
  bootstrap-password: {{ index $existing.data "bootstrap-password" }}
  bootstrap-token: {{ index $existing.data "bootstrap-token" }}
{{- else }}
  secret-key: {{ randAlphaNum 50 | b64enc }}
  bootstrap-password: {{ randAlphaNum 32 | b64enc }}
  bootstrap-token: {{ randAlphaNum 60 | b64enc }}
{{- end }}
```

(`AUTHENTIK_SECRET_KEY` must be stable across upgrades — rotating it invalidates every active
session and signed token, same `lookup`-guard reasoning as every other generated secret in this
chart. `bootstrap-token` is what Task 6 uses to authenticate to Authentik's own API without
needing interactive login.)

- [ ] **Step 3: Create the non-secret env ConfigMap**

`deploy/helm/iverson/charts/authentik/templates/configmap.yaml`:

```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: {{ .Release.Name }}-authentik-config
data:
  AUTHENTIK_REDIS__HOST: "{{ .Release.Name }}-redis"
  AUTHENTIK_POSTGRESQL__HOST: "{{ .Release.Name }}-postgres-rw"
  AUTHENTIK_POSTGRESQL__NAME: "authentik"
  AUTHENTIK_POSTGRESQL__USER: "authentik"
  AUTHENTIK_LOG_LEVEL: "info"
  AUTHENTIK_BOOTSTRAP_EMAIL: "{{ .Values.bootstrapEmail }}"
```

(`{{ .Release.Name }}-postgres-rw` matches the exact hostname `charts/api/templates/deployment.yaml`
already uses to reach the same CNPG cluster's read-write service.)

- [ ] **Step 4: Create the blueprints ConfigMap (empty until Task 6)**

`deploy/helm/iverson/charts/authentik/templates/blueprints-configmap.yaml`:

```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: {{ .Release.Name }}-authentik-blueprints
data:
{{- range $path, $bytes := .Files.Glob "blueprints/*.yaml" }}
  {{ base $path }}: |
{{ $.Files.Get $path | indent 4 }}
{{- end }}
```

- [ ] **Step 5: Create a placeholder so the (currently empty) blueprints directory is tracked by git**

```bash
mkdir -p deploy/helm/iverson/charts/authentik/blueprints
touch deploy/helm/iverson/charts/authentik/blueprints/.gitkeep
```

- [ ] **Step 6: Create the server Deployment + Service**

`deploy/helm/iverson/charts/authentik/templates/deployment-server.yaml`:

```yaml
apiVersion: v1
kind: ServiceAccount
metadata:
  name: {{ .Release.Name }}-authentik
automountServiceAccountToken: false
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: {{ .Release.Name }}-authentik-server
spec:
  replicas: 1
  selector:
    matchLabels:
      app: {{ .Release.Name }}-authentik-server
  template:
    metadata:
      labels:
        app: {{ .Release.Name }}-authentik-server
    spec:
      serviceAccountName: {{ .Release.Name }}-authentik
      automountServiceAccountToken: false
      nodeSelector:
        {{- toYaml .Values.nodeSelector | nindent 8 }}
      securityContext:
        runAsNonRoot: true
        runAsUser: 1000
        runAsGroup: 1000
        fsGroup: 1000
        seccompProfile: { type: RuntimeDefault }
      containers:
        - name: server
          image: "ghcr.io/goauthentik/server:{{ .Values.imageTag }}"
          # `args`, not `command`: the image's own ENTRYPOINT already wraps every invocation in
          # its `ak` lifecycle script — `args: ["server"]` overrides only the CMD, matching the
          # official `command: server` docker-compose shorthand. Setting `command:` here instead
          # would replace the ENTRYPOINT entirely and break startup.
          args: ["server"]
          securityContext:
            allowPrivilegeEscalation: false
            # Not read-only: Authentik writes to its own working directories at runtime and
            # upstream doesn't document read-only-root-filesystem compatibility (unlike
            # Prometheus/Redis above, where this was independently confirmed safe).
            readOnlyRootFilesystem: false
            capabilities: { drop: ["ALL"] }
          envFrom:
            - configMapRef:
                name: {{ .Release.Name }}-authentik-config
          env:
            - name: AUTHENTIK_SECRET_KEY
              valueFrom:
                secretKeyRef: { name: {{ .Release.Name }}-authentik-app, key: secret-key }
            - name: AUTHENTIK_POSTGRESQL__PASSWORD
              valueFrom:
                secretKeyRef: { name: {{ .Release.Name }}-authentik-postgres, key: password }
            - name: AUTHENTIK_BOOTSTRAP_PASSWORD
              valueFrom:
                secretKeyRef: { name: {{ .Release.Name }}-authentik-app, key: bootstrap-password }
            - name: AUTHENTIK_BOOTSTRAP_TOKEN
              valueFrom:
                secretKeyRef: { name: {{ .Release.Name }}-authentik-app, key: bootstrap-token }
          ports:
            - containerPort: 9000
              name: http
          resources:
            requests:
              cpu: {{ .Values.resources.server.requests.cpu | quote }}
              memory: {{ .Values.resources.server.requests.memory | quote }}
            limits:
              cpu: {{ .Values.resources.server.limits.cpu | quote }}
              memory: {{ .Values.resources.server.limits.memory | quote }}
          readinessProbe:
            httpGet: { path: /-/health/ready/, port: 9000 }
            initialDelaySeconds: 15
            periodSeconds: 10
          livenessProbe:
            httpGet: { path: /-/health/live/, port: 9000 }
            initialDelaySeconds: 30
            periodSeconds: 30
          volumeMounts:
            - name: blueprints
              mountPath: /blueprints/custom
              readOnly: true
      volumes:
        - name: blueprints
          configMap:
            name: {{ .Release.Name }}-authentik-blueprints
```

`deploy/helm/iverson/charts/authentik/templates/service.yaml`:

```yaml
apiVersion: v1
kind: Service
metadata:
  name: {{ .Release.Name }}-authentik
spec:
  selector:
    app: {{ .Release.Name }}-authentik-server
  ports:
    - name: http
      port: 9000
```

- [ ] **Step 7: Create the worker Deployment**

`deploy/helm/iverson/charts/authentik/templates/deployment-worker.yaml`:

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: {{ .Release.Name }}-authentik-worker
spec:
  replicas: 1
  selector:
    matchLabels:
      app: {{ .Release.Name }}-authentik-worker
  template:
    metadata:
      labels:
        app: {{ .Release.Name }}-authentik-worker
    spec:
      serviceAccountName: {{ .Release.Name }}-authentik
      automountServiceAccountToken: false
      nodeSelector:
        {{- toYaml .Values.nodeSelector | nindent 8 }}
      securityContext:
        runAsNonRoot: true
        runAsUser: 1000
        runAsGroup: 1000
        fsGroup: 1000
        seccompProfile: { type: RuntimeDefault }
      containers:
        - name: worker
          image: "ghcr.io/goauthentik/server:{{ .Values.imageTag }}"
          args: ["worker"]
          securityContext:
            allowPrivilegeEscalation: false
            readOnlyRootFilesystem: false
            capabilities: { drop: ["ALL"] }
          envFrom:
            - configMapRef:
                name: {{ .Release.Name }}-authentik-config
          env:
            - name: AUTHENTIK_SECRET_KEY
              valueFrom:
                secretKeyRef: { name: {{ .Release.Name }}-authentik-app, key: secret-key }
            - name: AUTHENTIK_POSTGRESQL__PASSWORD
              valueFrom:
                secretKeyRef: { name: {{ .Release.Name }}-authentik-postgres, key: password }
            - name: AUTHENTIK_BOOTSTRAP_PASSWORD
              valueFrom:
                secretKeyRef: { name: {{ .Release.Name }}-authentik-app, key: bootstrap-password }
            - name: AUTHENTIK_BOOTSTRAP_TOKEN
              valueFrom:
                secretKeyRef: { name: {{ .Release.Name }}-authentik-app, key: bootstrap-token }
          resources:
            requests:
              cpu: {{ .Values.resources.worker.requests.cpu | quote }}
              memory: {{ .Values.resources.worker.requests.memory | quote }}
            limits:
              cpu: {{ .Values.resources.worker.limits.cpu | quote }}
              memory: {{ .Values.resources.worker.limits.memory | quote }}
          readinessProbe:
            exec: { command: ["ak", "healthcheck"] }
            initialDelaySeconds: 15
            periodSeconds: 15
          livenessProbe:
            exec: { command: ["ak", "healthcheck"] }
            initialDelaySeconds: 30
            periodSeconds: 30
          volumeMounts:
            - name: blueprints
              mountPath: /blueprints/custom
              readOnly: true
      volumes:
        - name: blueprints
          configMap:
            name: {{ .Release.Name }}-authentik-blueprints
```

- [ ] **Step 8: Validate rendering**

```bash
cd deploy/helm/iverson
helm dependency build
helm template iverson . -f values.yaml -f values-local.yaml > /tmp/rendered.yaml
grep -c "kind: Deployment" /tmp/rendered.yaml
grep "name: iverson-authentik" /tmp/rendered.yaml
helm lint charts/authentik/ charts/redis/
```

Expected: renders without error; `iverson-authentik-server`, `iverson-authentik-worker`,
`iverson-authentik`, `iverson-authentik-config`, `iverson-authentik-app`,
`iverson-authentik-blueprints` all appear; `helm lint` reports 0 failures on both new subcharts.

- [ ] **Step 9: Commit**

```bash
git add deploy/helm/iverson/charts/authentik/
git commit -m "feat(helm): add Authentik server/worker Deployments, Service, and config"
```

---

## Task 4: Wire subcharts into the umbrella chart

**Files:**
- Modify: `deploy/helm/iverson/Chart.yaml`
- Modify: `deploy/helm/iverson/values.yaml`
- Modify: `deploy/helm/iverson/values-local.yaml`
- Modify: `deploy/helm/iverson/templates/networkpolicies.yaml`

**Interfaces:**
- Consumes: `redis` and `authentik` subcharts (Tasks 1-3).

- [ ] **Step 1: Add both subcharts as dependencies**

In `deploy/helm/iverson/Chart.yaml`, add to `dependencies:` (after `prometheus`):

```yaml
  - name: redis
    version: "0.1.0"
    repository: "file://charts/redis"
  - name: authentik
    version: "0.1.0"
    repository: "file://charts/authentik"
```

Also update the chart `description` on line 3:

```yaml
description: Iverson — cloud-agnostic Kubernetes deployment (Postgres, Kafka, StarRocks, Qdrant, Ollama, API, Jaeger, Prometheus, Authentik)
```

- [ ] **Step 2: Add default values**

In `deploy/helm/iverson/values.yaml`, add after the `prometheus:` block:

```yaml
redis:
  imageTag: "7.4-alpine"
  resources:
    requests: { cpu: "100m", memory: "128Mi" }
    limits: { cpu: "250m", memory: "256Mi" }
  nodeSelector: {}

authentik:
  imageTag: "2026.5.3"
  resources:
    server:
      requests: { cpu: "250m", memory: "512Mi" }
      limits: { cpu: "500m", memory: "1Gi" }
    worker:
      requests: { cpu: "250m", memory: "512Mi" }
      limits: { cpu: "500m", memory: "1Gi" }
  nodeSelector: {}
  bootstrapEmail: "admin@iverson.local"
```

- [ ] **Step 3: Add local-dev overrides**

In `deploy/helm/iverson/values-local.yaml`, add after the `prometheus:` block:

```yaml
redis:
  resources:
    requests: { cpu: "50m", memory: "64Mi" }
    limits: { cpu: "100m", memory: "128Mi" }

authentik:
  resources:
    server:
      requests: { cpu: "100m", memory: "256Mi" }
      limits: { cpu: "500m", memory: "512Mi" }
    worker:
      requests: { cpu: "100m", memory: "256Mi" }
      limits: { cpu: "500m", memory: "512Mi" }
```

- [ ] **Step 4: Add NetworkPolicy rules**

In `deploy/helm/iverson/templates/networkpolicies.yaml`, modify the existing
`{{ .Release.Name }}-postgres-ingress` policy's `from` list (add `authentik-server` and
`authentik-worker` as allowed sources, alongside the existing `api`/`worker` entries):

```yaml
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: {{ .Release.Name }}-postgres-ingress
  annotations:
    # postgres is a CloudNativePG-operator-managed CR; kube-score can't see the
    # runtime pod labels the operator applies, so it can't match this policy's
    # podSelector to any pod it renders — confirmed false positive.
    kube-score/ignore: networkpolicy-targets-pod
spec:
  podSelector:
    matchLabels: { cnpg.io/cluster: {{ .Release.Name }}-postgres }
  policyTypes: ["Ingress"]
  ingress:
    - from:
        - podSelector: { matchLabels: { app: {{ .Release.Name }}-api } }
        - podSelector: { matchLabels: { app: {{ .Release.Name }}-worker } }
        - podSelector: { matchLabels: { app: {{ .Release.Name }}-authentik-server } }
        - podSelector: { matchLabels: { app: {{ .Release.Name }}-authentik-worker } }
      ports: [{ protocol: TCP, port: 5432 }]
```

Then add four new policies, after the existing `{{ .Release.Name }}-prometheus-egress` policy, and
before the final `{{- end }}`:

```yaml
---
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: {{ .Release.Name }}-authentik-ingress
spec:
  podSelector:
    matchLabels: { app: {{ .Release.Name }}-authentik-server }
  policyTypes: ["Ingress"]
  ingress:
    - from: []   # kubelet readiness/liveness probes: same any-source reasoning as api-ingress's
               # kubelet-probe rule — authentik's server process serves both app traffic and its
               # own health endpoints (/-/health/live/, /-/health/ready/) on the same port, so
               # there's no separate dedicated health port to scope this to.
      ports: [{ protocol: TCP, port: 9000 }]
---
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: {{ .Release.Name }}-authentik-egress
spec:
  podSelector:
    matchExpressions:
      - key: app
        operator: In
        values: ["{{ .Release.Name }}-authentik-server", "{{ .Release.Name }}-authentik-worker"]
  policyTypes: ["Egress"]
  egress:
    - to: []   # DNS
      ports: [{ protocol: UDP, port: 53 }, { protocol: TCP, port: 53 }]
    - to: [{ podSelector: { matchLabels: { cnpg.io/cluster: {{ .Release.Name }}-postgres } } }]
      ports: [{ protocol: TCP, port: 5432 }]
    - to: [{ podSelector: { matchLabels: { app: {{ .Release.Name }}-redis } } }]
      ports: [{ protocol: TCP, port: 6379 }]
---
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: {{ .Release.Name }}-redis-ingress
spec:
  podSelector:
    matchLabels: { app: {{ .Release.Name }}-redis }
  policyTypes: ["Ingress"]
  ingress:
    - from:
        - podSelector: { matchLabels: { app: {{ .Release.Name }}-authentik-server } }
        - podSelector: { matchLabels: { app: {{ .Release.Name }}-authentik-worker } }
      ports: [{ protocol: TCP, port: 6379 }]
---
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: {{ .Release.Name }}-redis-egress
spec:
  podSelector:
    matchLabels: { app: {{ .Release.Name }}-redis }
  policyTypes: ["Egress"]
  egress:
    - to: []   # DNS
      ports: [{ protocol: UDP, port: 53 }, { protocol: TCP, port: 53 }]
```

- [ ] **Step 5: Validate the chart renders**

```bash
cd deploy/helm/iverson
helm dependency build
helm template iverson . -f values.yaml -f values-local.yaml > /tmp/rendered.yaml
grep -c "kind: Deployment" /tmp/rendered.yaml
grep -c "kind: NetworkPolicy" /tmp/rendered.yaml
grep "name: iverson-authentik-ingress\|name: iverson-redis-ingress" /tmp/rendered.yaml
```

Expected: renders without error; `NetworkPolicy` count includes the 4 new policies; both new
ingress policy names appear.

- [ ] **Step 6: Commit**

```bash
git add deploy/helm/iverson/Chart.yaml deploy/helm/iverson/values.yaml \
        deploy/helm/iverson/values-local.yaml \
        deploy/helm/iverson/templates/networkpolicies.yaml \
        deploy/helm/iverson/Chart.lock
git commit -m "feat(helm): wire redis and authentik subcharts into the umbrella chart"
```

(Do not `git add` any `charts/*.tgz` — this repo's convention, confirmed in the prior
Prometheus-subchart plan, is that these are gitignored and never committed; only `Chart.lock` is.)

---

## Task 5: docker-compose local dev parity

**Files:**
- Create: `deploy/postgres/init-authentik-db.sql`
- Modify: `docker-compose.yml`

**Interfaces:**
- Consumes: the existing `postgres` service (adds a second database to the same container).

- [ ] **Step 1: Create the Postgres init script**

`deploy/postgres/init-authentik-db.sql`:

```sql
CREATE USER authentik WITH PASSWORD 'authentik';
CREATE DATABASE authentik OWNER authentik;
```

(`OWNER authentik` at creation time, not a separate `GRANT` afterward — this makes `authentik` the
owner of the database's default `public` schema too, avoiding a common Postgres 15+ pitfall where
a plain `GRANT ALL PRIVILEGES ON DATABASE` no longer implies schema-level privileges, which would
otherwise make Authentik's own migrations fail with `permission denied for schema public`.
`docker-entrypoint-initdb.d/*.sql` scripts only run once, against a completely empty data
directory — this only takes effect on a fresh `postgres_data` volume.)

- [ ] **Step 2: Mount the init script into the `postgres` service**

In `docker-compose.yml`, modify the `postgres` service's `volumes:`:

```yaml
  postgres:
    image: postgres:16
    container_name: iverson-postgres
    restart: unless-stopped
    environment:
      POSTGRES_USER: iverson
      POSTGRES_PASSWORD: iverson
      POSTGRES_DB: iverson
    ports:
      - "5432:5432"
    volumes:
      - postgres_data:/var/lib/postgresql/data
      - ./deploy/postgres/init-authentik-db.sql:/docker-entrypoint-initdb.d/init-authentik-db.sql:ro
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U iverson"]
      interval: 10s
      timeout: 5s
      retries: 5
```

- [ ] **Step 3: If your local `postgres_data` volume already exists, create the `authentik`
      role/database manually**

`docker-entrypoint-initdb.d/*.sql` scripts only run against a completely empty data directory, so
Step 1's script has no effect if you've run `docker compose up` in this repo before this task. Check
first:

```bash
docker volume ls -q | grep -q "$(basename "$(pwd)")_postgres_data" && echo "volume exists — run the commands below" || echo "volume is new — Step 1's init script will handle it"
```

If it exists, bring up just `postgres` and run the same two statements from Step 1's script by hand,
once:

```bash
docker compose up -d postgres
docker compose exec postgres psql -U iverson -c "CREATE USER authentik WITH PASSWORD 'authentik';"
docker compose exec postgres psql -U iverson -c "CREATE DATABASE authentik OWNER authentik;"
```

(Skip this step entirely on a fresh clone/volume — Step 1's mounted script already covers it.)

- [ ] **Step 4: Add a new `# ── Identity ──` section with `redis`, `authentik-server`, and
      `authentik-worker` services**

In `docker-compose.yml`, add this new section after `# ── Observability ──`'s `prometheus:`
service block and before `# ── Application ──`:

```yaml
  # ── Identity ────────────────────────────────────────────────────────────────

  redis:
    image: redis:7.4-alpine
    container_name: iverson-redis
    restart: unless-stopped
    command: ["redis-server", "--save", ""]   # cache-only, no volume mounted — matches the Helm
                                               # subchart's identical reasoning (Task 1)
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 10s
      timeout: 5s
      retries: 5

  authentik-server:
    image: ghcr.io/goauthentik/server:2026.5.3
    container_name: iverson-authentik-server
    restart: unless-stopped
    command: server
    environment:
      AUTHENTIK_SECRET_KEY: "dev-only-not-for-production-use-0123456789abcdef"
      AUTHENTIK_REDIS__HOST: redis
      AUTHENTIK_POSTGRESQL__HOST: postgres
      AUTHENTIK_POSTGRESQL__NAME: authentik
      AUTHENTIK_POSTGRESQL__USER: authentik
      AUTHENTIK_POSTGRESQL__PASSWORD: authentik
      AUTHENTIK_BOOTSTRAP_EMAIL: admin@iverson.local
      AUTHENTIK_BOOTSTRAP_PASSWORD: dev-admin-password
      AUTHENTIK_BOOTSTRAP_TOKEN: dev-bootstrap-token-0123456789
    ports:
      - "9000:9000"
    volumes:
      - ./deploy/helm/iverson/charts/authentik/blueprints:/blueprints/custom:ro
    depends_on:
      postgres:
        condition: service_healthy
      redis:
        condition: service_healthy
    healthcheck:
      test: ["CMD", "ak", "healthcheck"]
      interval: 15s
      timeout: 10s
      retries: 10
      start_period: 30s

  authentik-worker:
    image: ghcr.io/goauthentik/server:2026.5.3
    container_name: iverson-authentik-worker
    restart: unless-stopped
    command: worker
    environment:
      AUTHENTIK_SECRET_KEY: "dev-only-not-for-production-use-0123456789abcdef"
      AUTHENTIK_REDIS__HOST: redis
      AUTHENTIK_POSTGRESQL__HOST: postgres
      AUTHENTIK_POSTGRESQL__NAME: authentik
      AUTHENTIK_POSTGRESQL__USER: authentik
      AUTHENTIK_POSTGRESQL__PASSWORD: authentik
      AUTHENTIK_BOOTSTRAP_EMAIL: admin@iverson.local
      AUTHENTIK_BOOTSTRAP_PASSWORD: dev-admin-password
      AUTHENTIK_BOOTSTRAP_TOKEN: dev-bootstrap-token-0123456789
    volumes:
      - ./deploy/helm/iverson/charts/authentik/blueprints:/blueprints/custom:ro
    depends_on:
      postgres:
        condition: service_healthy
      redis:
        condition: service_healthy
    healthcheck:
      test: ["CMD", "ak", "healthcheck"]
      interval: 15s
      timeout: 10s
      retries: 10
      start_period: 30s
```

(Reusing the exact same blueprint files the Helm chart mounts — `./deploy/helm/iverson/charts/authentik/blueprints` — rather than a separate copy, so local dev and every k8s environment are
provably configured identically from the same source files. No `user: root` / Docker-socket mount,
unlike Authentik's own reference compose file — that's only needed for its optional
Docker-outpost integration, which this deployment doesn't use.)

- [ ] **Step 5: Verify the compose file parses**

```bash
docker compose config --quiet
```

Expected: no output, exit code 0.

- [ ] **Step 6: Commit**

```bash
git add deploy/postgres/init-authentik-db.sql docker-compose.yml
git commit -m "feat(observability): add Redis and Authentik services to docker-compose for local dev parity"
```

---

## Task 6: Configure MFA/OAuth2/SAML via Authentik's REST API and commit as Blueprints

**Files:**
- Create: `deploy/helm/iverson/charts/authentik/blueprints/mfa-enforcement.yaml`
- Create: `deploy/helm/iverson/charts/authentik/blueprints/oauth2-provider.yaml`
- Create: `deploy/helm/iverson/charts/authentik/blueprints/saml-provider.yaml`
- Delete: `deploy/helm/iverson/charts/authentik/blueprints/.gitkeep`

**Interfaces:**
- Consumes: the running docker-compose stack from Task 5.
- Produces: three Blueprint YAML files that Task 3's `blueprints-configmap.yaml` template
  (`.Files.Glob "blueprints/*.yaml"`) automatically picks up on the next `helm template`/`helm
  upgrade` — no further chart changes needed after this task.

Rewritten from an original Admin-UI-click-path design after live verification against a running
Authentik 2026.5.3 instance (via its REST API and OpenAPI schema at `/api/v3/schema/`) found: (1)
Authentik already ships a built-in `default-authentication-mfa-validation` stage, already bound at
the correct position (order=30, immediately after the password stage at order=20) in
`default-authentication-flow` — MFA-enforcement is a matter of *modifying* this existing stage's
config, not creating a new stage + binding; (2) Authentik already ships default
`default-authenticator-totp-setup` / `default-authenticator-webauthn-setup` stages usable directly
as `configuration_stages`; (3) Blueprint YAML supports a `!Find [model, [field, value]]` tag that
resolves references to existing objects by any unique field (e.g. flow `slug`, stage `name`) at
apply time — this avoids ever hardcoding a database pk (randomly generated per Authentik install,
not portable across environments).

- [ ] **Step 1: Bring up an isolated docker-compose stack**

This repo's docker-compose project name defaults to the current directory's basename
(`Iverson.Server`) when no `-p` is given. Every git worktree of this repo has a directory with
that same basename, so running plain `docker compose up` from a worktree silently reuses the
*main checkout's* shared `iversonserver_postgres_data` volume — which was never provisioned with
the `authentik` role/database Task 5 added, causing `authentik-server` to fail Postgres
authentication. Avoid this by giving the stack a project name unique to this task's verification
run:

```bash
docker compose -p authentik-blueprint-verify up -d postgres redis authentik-server authentik-worker
```

Wait for `docker compose -p authentik-blueprint-verify ps` to show `authentik-server` as `healthy`
(may take ~30-60s on first boot — Authentik runs its own database migrations before the
healthcheck passes).

- [ ] **Step 2: Write the MFA-enforcement blueprint**

`deploy/helm/iverson/charts/authentik/blueprints/mfa-enforcement.yaml`:

```yaml
version: 1
metadata:
  name: MFA enforcement (TOTP + WebAuthn required)
entries:
  - model: authentik_stages_authenticator_validate.authenticatorvalidatestage
    identifiers:
      name: default-authentication-mfa-validation
    attrs:
      not_configured_action: configure
      device_classes:
        - totp
        - webauthn
      configuration_stages:
        - !Find [authentik_stages_authenticator_totp.authenticatortotpstage, [name, default-authenticator-totp-setup]]
        - !Find [authentik_stages_authenticator_webauthn.authenticatorwebauthnstage, [name, default-authenticator-webauthn-setup]]
```

(`identifiers: {name: default-authentication-mfa-validation}` targets Authentik's existing
built-in stage — Blueprints match-or-create by `identifiers`, so this updates it in place rather
than creating a duplicate. `not_configured_action: configure` is what makes MFA mandatory — a user
with no enrolled factor is routed into enrollment rather than allowed through. `device_classes:
[totp, webauthn]` narrows the built-in stage's default of all six device classes down to just
these two, per the design's explicit "no SMS/email OTP" decision. The two `!Find` entries resolve
to Authentik's own default enrollment stages by name at apply time — no new enrollment stages need
to be created.)

- [ ] **Step 3: Write the OAuth2/OIDC provider blueprint**

`deploy/helm/iverson/charts/authentik/blueprints/oauth2-provider.yaml`:

```yaml
version: 1
metadata:
  name: Default OAuth2/OIDC provider (PKCE, no application bound yet)
entries:
  - model: authentik_providers_oauth2.oauth2provider
    identifiers:
      name: iverson-oidc-default
    attrs:
      authorization_flow: !Find [authentik_flows.flow, [slug, default-provider-authorization-implicit-consent]]
      invalidation_flow: !Find [authentik_flows.flow, [slug, default-provider-invalidation-flow]]
      client_type: public
      redirect_uris:
        - matching_mode: strict
          url: "http://localhost/placeholder-callback"
          redirect_uri_type: authorization
```

(`client_type: public` means PKCE is the confidentiality mechanism, no client secret required —
Authentik enforces S256 PKCE unconditionally for Public clients (confirmed: no separate PKCE
toggle exists in the provider's schema). `redirect_uris` is a required field with no sensible real
value yet since no relying application exists — the placeholder is intentional and gets replaced
with the real callback URL when Part 2/3 defines an actual consumer. No `Application` binding —
this provider exists for Part 2/3 to point at.)

- [ ] **Step 4: Write the SAML provider blueprint**

`deploy/helm/iverson/charts/authentik/blueprints/saml-provider.yaml`:

```yaml
version: 1
metadata:
  name: Default SAML provider (no application bound yet)
entries:
  - model: authentik_providers_saml.samlprovider
    identifiers:
      name: iverson-saml-default
    attrs:
      authorization_flow: !Find [authentik_flows.flow, [slug, default-provider-authorization-implicit-consent]]
      invalidation_flow: !Find [authentik_flows.flow, [slug, default-provider-invalidation-flow]]
      acs_url: "http://localhost/placeholder-acs"
```

(`acs_url` is a required field on `SAMLProviderRequest`; same placeholder reasoning as the OAuth2
provider's `redirect_uris` — no relying SAML Service Provider exists yet.)

- [ ] **Step 5: Remove the now-unneeded placeholder**

```bash
git rm deploy/helm/iverson/charts/authentik/blueprints/.gitkeep
```

- [ ] **Step 6: Verify each blueprint applies cleanly against the live instance**

Register and apply all three via the API (using the bootstrap token from Task 5's compose
environment):

```bash
for f in mfa-enforcement oauth2-provider saml-provider; do
  PK=$(curl -s -X POST -H "Authorization: Bearer dev-bootstrap-token-0123456789" -H "Content-Type: application/json" \
    -d "$(python3 -c "import json; print(json.dumps({'name': '$f', 'content': open('deploy/helm/iverson/charts/authentik/blueprints/$f.yaml').read(), 'enabled': True}))")" \
    http://localhost:9000/api/v3/managed/blueprints/ | python3 -c "import json,sys; print(json.load(sys.stdin)['pk'])")
  curl -s -X POST -H "Authorization: Bearer dev-bootstrap-token-0123456789" \
    "http://localhost:9000/api/v3/managed/blueprints/$PK/apply/" > /dev/null
  sleep 1
  STATUS=$(curl -s -H "Authorization: Bearer dev-bootstrap-token-0123456789" \
    "http://localhost:9000/api/v3/managed/blueprints/$PK/" | python3 -c "import json,sys; print(json.load(sys.stdin)['status'])")
  echo "$f: $STATUS"
done
```

Expected: `successful` printed for all three. Then confirm the actual objects reflect the intended
config:

```bash
curl -s -H "Authorization: Bearer dev-bootstrap-token-0123456789" \
  "http://localhost:9000/api/v3/stages/authenticator/validate/?search=default-authentication-mfa-validation" \
  | grep -o '"not_configured_action":"[^"]*"'
curl -s -H "Authorization: Bearer dev-bootstrap-token-0123456789" \
  "http://localhost:9000/api/v3/providers/oauth2/?search=iverson-oidc-default" | grep -o '"name":"[^"]*"'
curl -s -H "Authorization: Bearer dev-bootstrap-token-0123456789" \
  "http://localhost:9000/api/v3/providers/saml/?search=iverson-saml-default" | grep -o '"name":"[^"]*"'
```

Expected: `"not_configured_action":"configure"`; both provider names returned.

- [ ] **Step 7: Tear down the verification stack**

```bash
docker compose -p authentik-blueprint-verify down -v
```

This isolated stack (and its scratch volume) was only for verifying the blueprint files apply
cleanly — the files are the deliverable, not this running instance. Task 7's kind smoke test is
the real proof that these blueprints reproduce the configuration on a completely fresh install.

- [ ] **Step 8: Commit**

```bash
git add deploy/helm/iverson/charts/authentik/blueprints/
git commit -m "feat(authentik): add MFA-enforcement, OAuth2, and SAML provider blueprints"
```

---

## Task 7: kind smoke test

**Files:** none (verification only).

**Interfaces:**
- Consumes: everything from Tasks 1-6.

- [ ] **Step 1: Tear down any existing kind cluster**

```bash
kind delete cluster --name iverson
docker ps -a --filter "name=iverson-control-plane" -q | xargs -r docker rm -f
```

- [ ] **Step 2: Create the cluster and install operators**

```bash
cd deploy/kind
./setup.sh
```

Wait for Calico, ingress-nginx, CloudNativePG, Strimzi, and the StarRocks operator to all reach
`Running`/`Ready` (see `setup.sh`'s own printed instructions for the exact wait commands it
expects).

- [ ] **Step 3: Build and load the app image, install the umbrella chart**

```bash
./build-and-load-image.sh
cd ../helm/iverson
helm dependency build
helm upgrade --install iverson . -f values.yaml -f values-local.yaml \
  --namespace iverson --create-namespace --wait --timeout 15m
```

If `--wait` times out, fall back to installing without it and poll
`kubectl get pods -n iverson -w` manually until every pod is `Running`/`Ready` — this repo's
established fallback for slow first-boot image pulls (see the prior plan's kind smoke test notes).

- [ ] **Step 4: Verify Authentik-specific pods and resources**

```bash
kubectl get pods -n iverson -l 'app in (iverson-authentik-server,iverson-authentik-worker,iverson-redis)'
kubectl get databases.postgresql.cnpg.io -n iverson
kubectl get networkpolicy -n iverson | grep -E "authentik|redis"
```

Expected: `authentik-server`, `authentik-worker`, and `redis` pods all `Running`/`Ready`; the
`authentik` `Database` object `Ready`; all 4 new NetworkPolicies present.

- [ ] **Step 5: Verify the blueprints auto-applied on this fresh cluster**

```bash
kubectl port-forward -n iverson svc/iverson-authentik 9000:9000 &
sleep 3
TOKEN=$(kubectl get secret iverson-authentik-app -n iverson -o jsonpath='{.data.bootstrap-token}' | base64 -d)
curl -s -H "Authorization: Bearer $TOKEN" \
  http://localhost:9000/api/v3/providers/oauth2/?search=iverson-oidc-default \
  | grep -o '"name":"[^"]*"'
curl -s -H "Authorization: Bearer $TOKEN" \
  http://localhost:9000/api/v3/providers/saml/?search=iverson-saml-default \
  | grep -o '"name":"[^"]*"'
curl -s -H "Authorization: Bearer $TOKEN" \
  "http://localhost:9000/api/v3/flows/instances/default-authentication-flow/" \
  | grep -o '"slug":"[^"]*"'
curl -s -H "Authorization: Bearer $TOKEN" \
  "http://localhost:9000/api/v3/stages/authenticator/validate/?search=default-authentication-mfa-validation" \
  | grep -o '"not_configured_action":"[^"]*"'
kill %1
```

Expected: the first two return the provider names; the third returns
`"slug":"default-authentication-flow"`; the fourth returns
`"not_configured_action":"configure"`, confirming the blueprint's modification to Authentik's
built-in MFA-validation stage (which is already bound into the login flow by default) actually
applied — not just that a stray stage object exists in isolation. All four prove the blueprints
reproduced the *complete* MFA-enforcement configuration on a cluster that never had any manual
Admin UI configuration — this is the actual gate this whole plan exists to pass.

- [ ] **Step 6: Leave the cluster running** (do not tear down — matches this repo's established
      practice of leaving a successful kind smoke-test cluster up for further manual inspection).

No commit for this task — it's pure verification of Tasks 1-6's already-committed work.

---

## Self-review

**Spec coverage:**
- Authentik as standalone source of truth, MFA (TOTP+WebAuthn only) → Task 6.
- Full deployment-target parity (docker-compose + kind + Helm, no cloud-values changes) → Tasks
  4-5, 7.
- New database on the existing CNPG cluster, no new operator → Task 2.
- Hand-rolled subchart, not the upstream Authentik chart → Tasks 1, 3 (plain Deployment/Service,
  no CRDs, no chart dependency on an external repo).
- Config as code via Blueprints → Task 6.
- Zero Iverson application code changes → confirmed; no task touches any `.cs`/`.proto`/client-SDK
  file.

**Placeholder scan:** no TBD/TODO; every file has complete content. Task 6's blueprint YAML content
was hand-authored and empirically verified (applied against a live Authentik 2026.5.3 instance,
confirmed via its REST API) rather than described as an Admin-UI click-path — every field value,
model name, and cross-reference is fully specified and was confirmed to work, not assumed.

**Type/naming consistency:** `{{ .Release.Name }}-authentik-server` / `-worker` pod labels are used
identically across Task 3 (Deployments), Task 4 (NetworkPolicy), and Task 7 (verification
`kubectl get pods -l`). `{{ .Release.Name }}-authentik-postgres` Secret name matches between Task 2
(created) and Task 3 (consumed via `secretKeyRef`). `{{ .Release.Name }}-authentik-app` Secret keys
(`secret-key`, `bootstrap-password`, `bootstrap-token`) match between Task 3's `secret.yaml` and its
own `deployment-server.yaml`/`deployment-worker.yaml` references, and Task 6/7's bootstrap-token
usage. The blueprints directory path (`deploy/helm/iverson/charts/authentik/blueprints/`) is used
identically by Task 3's `.Files.Glob`, Task 5's docker-compose volume mount, and Task 6's file
output paths.
