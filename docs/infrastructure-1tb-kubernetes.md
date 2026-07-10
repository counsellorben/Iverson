# Iverson Infrastructure at 1TB Scale: Kubernetes (EKS and AKS)

This document sizes a Kubernetes-based deployment for the same 1TB Iverson workload covered in [infrastructure-1tb-aws-azure.md](infrastructure-1tb-aws-azure.md), but with **every subsystem running as a container inside a Kubernetes cluster** rather than a mix of managed services (RDS, MSK, Event Hubs, Fargate/Container Apps) and self-managed VMs. Pod CPU/RAM requests are set to match the instance sizes used in the VM-based document, so the two documents are directly comparable component-by-component. All prices are approximate on-demand rates for US East regions as of mid-2026; 1-year reserved node commitments reduce compute costs by ~30–40%, consistent with the source document.

---

## 1. What changes from the VM/managed-service model

The data distribution model is unchanged — see [infrastructure-1tb-aws-azure.md §1](infrastructure-1tb-aws-azure.md#1-data-distribution-model) for the per-subsystem size breakdown (~1.4TB Postgres, ~200GB StarRocks, ~225GB/replica Qdrant, ~150GB Kafka, ~300MB Ollama). Kubernetes doesn't change how much data Iverson stores; it changes how compute is packaged and scheduled.

Two components lose their managed-service form entirely, because the brief calls for **all services in docker containers**:

| Component | Source document (VM/managed) | This document (Kubernetes) |
|-----------|------------------------------|------------------------------|
| PostgreSQL | Amazon RDS / Azure Flexible Server (fully managed HA, automated backups) | StatefulSet via **CloudNativePG** operator — self-managed HA, self-managed backups |
| Kafka | Amazon MSK / Azure Event Hubs (fully managed broker fleet) | StatefulSet via **Strimzi** operator, KRaft mode — self-managed brokers |
| StarRocks | Self-managed EC2/VM (already unmanaged) | StatefulSet via the official **StarRocks Kubernetes Operator** |
| Qdrant | Self-managed EC2/VM (already unmanaged) | StatefulSet, native Qdrant Raft clustering (no operator required) |
| Ollama | Self-managed EC2/VM (already unmanaged) | Deployment, model pulled by an init container |
| Iverson API | ECS Fargate / Azure Container Apps (managed serverless containers) | Deployment + HorizontalPodAutoscaler |
| Jaeger | CloudWatch/X-Ray or Application Insights (managed observability) | Deployment, all-in-one container (same image as `docker-compose.yml`) |

Losing managed Postgres and Kafka is the single biggest architectural shift versus the source document — see [§10](#10-operational-tradeoffs-of-self-managing-postgres-and-kafka) before treating this as a drop-in replacement.

---

## 2. Kubernetes workload architecture

| Role | Workload type | Operator / mechanism | Replicas | Pod request (CPU / RAM) | PVC size |
|------|---------------|----------------------|----------|--------------------------|----------|
| PostgreSQL | StatefulSet | CloudNativePG (primary + streaming standby) | 2 | 8 vCPU / 64GB | 1.5TB each |
| StarRocks FE | StatefulSet | StarRocks Operator | 1 | 4 vCPU / 32GB | 50GB |
| StarRocks BE | StatefulSet | StarRocks Operator | 2 | 8 vCPU / 64GB | 500GB each |
| Qdrant | StatefulSet | Native Raft clustering | 2 | 8 vCPU / 64GB | 500GB each |
| Kafka (KRaft, combined broker+controller) | StatefulSet | Strimzi | 3 | 2 vCPU / 8GB | 200GB each |
| Ollama | Deployment | Init container pulls `nomic-embed-text` | 2 | 8 vCPU / 16GB | 20GB each |
| Iverson API | Deployment + HPA | — | 2–5 (HPA on CPU 70%) | 2 vCPU / 4GB | — |
| Jaeger | Deployment | All-in-one image | 1 | 2 vCPU / 4GB | — (in-memory/Badger) |

Pod resource requests are identical to the instance sizes in the source document (same `r7i.xlarge`/`r7i.2xlarge`/`c7i.2xlarge`/`Standard_E*`/`Standard_F*` profiles), so a component's *compute footprint* doesn't change — only how it's packaged and who operates it.

---

## 3. Cluster-level building blocks

These are common to both clouds and sit outside the per-component pod sizing above:

- **CSI driver** — `aws-ebs-csi-driver` (EKS) / `azuredisk-csi-driver` (AKS), one `StorageClass` per component matching the disk type used in the source document (gp3 / Premium SSD, Premium SSD v2 for StarRocks and Qdrant).
- **Ingress** — AWS Load Balancer Controller (provisions an ALB from an `Ingress` resource) / Application Gateway Ingress Controller — AGIC (provisions an App Gateway). Same L7 gRPC routing requirement as the source document, same underlying load balancer products and cost.
- **Autoscaling** — Cluster Autoscaler (or Karpenter on EKS) scales the general-purpose node pool for the Iverson API HPA; stateful node pools (Postgres, StarRocks, Qdrant, Kafka, Ollama) are fixed-size, since these components scale by adding replicas deliberately, not by bursty demand.
- **Operators** — CloudNativePG (Postgres HA + backup), Strimzi (Kafka KRaft brokers), StarRocks Operator (FE/BE lifecycle). All three are cloud-agnostic and run identically on EKS and AKS.
- **Node pool isolation** — each stateful component gets its own node pool (`nodeSelector` + taint/toleration) to avoid noisy-neighbor contention on disk and network I/O; this mirrors the "one VM type per component" model in the source document rather than aggressively bin-packing everything onto uniform nodes.
- **Monitoring** — CloudWatch Container Insights / Azure Monitor for Containers for node and pod-level metrics (CPU, memory, disk, restarts); this is separate from Jaeger, which now runs in-cluster and handles application tracing only.

---

## 4. AWS EKS configuration

### 4.1 Node pools

| Node pool | Instance type | Nodes | Hosts |
|-----------|---------------|-------|-------|
| `postgres-pool` | `r7i.2xlarge` (8 vCPU, 64GB) | 2 | CloudNativePG primary + standby, one per AZ |
| `starrocks-pool` | `r7i.xlarge` (FE) + `r7i.2xlarge` (BE) | 1 + 2 | StarRocks FE, StarRocks BE ×2 |
| `qdrant-pool` | `r7i.2xlarge` (8 vCPU, 64GB) | 2 | Qdrant ×2, memmap mode |
| `kafka-pool` | `m5.large` (2 vCPU, 8GB) | 3 | Strimzi KRaft brokers ×3 |
| `ollama-pool` | `c7i.2xlarge` (8 vCPU, 16GB) | 2 | Ollama ×2 |
| `general-pool` | `m6i.xlarge` (4 vCPU, 16GB) | 2 (autoscaling) | Iverson API, Jaeger, CoreDNS, ALB controller, cluster-autoscaler, CSI controller pods |

14 EKS worker nodes total, up from 7 self-managed EC2 instances in the source document — the increase is entirely Postgres (2 nodes) and Kafka (3 nodes) moving from managed services onto cluster nodes, plus the general-purpose pool (2 nodes) that replaces Fargate.

### 4.2 AWS EKS itemized monthly cost

| Component | Resource | Unit price | Qty / hours | Monthly |
|-----------|----------|-----------|------------|---------|
| **EKS control plane** | Cluster management fee | $0.10/hr | 730 hrs | $73 |
| **Postgres pool × 2** | `r7i.2xlarge` (CloudNativePG) | $0.504/hr | 2 × 730 hrs | $736 |
| Postgres PVC storage | EBS gp3, 1.5TB × 2 replicas | $0.115/GB | 3,072 GB | $353 |
| Postgres backup | pgBackRest → S3 Standard-IA, 1TB | $0.0125/GB | 1,024 GB | $13 |
| **StarRocks FE** | `r7i.xlarge` | $0.252/hr | 730 hrs | $184 |
| **StarRocks BE × 2** | `r7i.2xlarge` | $0.504/hr | 2 × 730 hrs | $736 |
| StarRocks BE storage | EBS gp3, 500GB × 2 | $0.08/GB | 1,024 GB | $82 |
| **Qdrant × 2** | `r7i.2xlarge`, memmap mode | $0.504/hr | 2 × 730 hrs | $736 |
| Qdrant storage | EBS gp3, 500GB × 2 | $0.08/GB | 1,024 GB | $82 |
| **Kafka × 3** | `m5.large` (Strimzi KRaft) | $0.096/hr | 3 × 730 hrs | $210 |
| Kafka storage | EBS gp3, 200GB × 3 | $0.10/GB | 600 GB | $60 |
| **Ollama × 2** | `c7i.2xlarge` | $0.357/hr | 2 × 730 hrs | $521 |
| Ollama storage | EBS gp3, 20GB × 2 | $0.08/GB | 40 GB | $3 |
| **General pool × 2** | `m6i.xlarge` (API, Jaeger, add-ons) | $0.192/hr | 2 × 730 hrs | $280 |
| **AWS Load Balancer Controller → ALB** | ALB hourly | $0.008/hr | 730 hrs | $6 |
| ALB — LCU | Load Balancer Capacity Units | $0.008/LCU-hr | ~5 LCU × 730 | $29 |
| **CloudWatch Container Insights** | Log + metric ingestion | $0.50/GB | ~20 GB | $10 |
| CloudWatch Logs | Log storage, 30-day | $0.03/GB | ~600 GB | $18 |
| **NAT Gateway** | Data processing | $0.045/GB | ~100 GB | $5 |
| NAT Gateway | Hourly | $0.045/hr | 730 hrs | $33 |
| **Data transfer out** | Internet egress | $0.09/GB | ~50 GB | $5 |
| | | | **Total (on-demand)** | **~$4,175/mo** |
| | | | **Total (1-yr reserved nodes)** | **~$2,950/mo** |

> Reserved pricing applies the ~36% discount to all EC2 node costs (all rows with an hourly instance rate, excluding the EKS control plane fee, storage, LB, NAT, and data transfer).

---

## 5. Azure AKS configuration

### 5.1 Node pools

Azure instance-type mapping follows the source document (§4.1): `r7i.xlarge`→`Standard_E4ds_v5`, `r7i.2xlarge`→`Standard_E8ds_v5`, `c7i.2xlarge`→`Standard_F8s_v2`.

| Node pool | Instance type | Nodes | Hosts |
|-----------|---------------|-------|-------|
| `postgres-pool` | `Standard_E8ds_v5` (8 vCPU, 64GB) | 2 | CloudNativePG primary + standby, one per zone |
| `starrocks-pool` | `Standard_E4ds_v5` (FE) + `Standard_E8ds_v5` (BE) | 1 + 2 | StarRocks FE, StarRocks BE ×2 |
| `qdrant-pool` | `Standard_E8ds_v5` (8 vCPU, 64GB) | 2 | Qdrant ×2, memmap mode |
| `kafka-pool` | `Standard_D2ds_v5` (2 vCPU, 8GB) | 3 | Strimzi KRaft brokers ×3 |
| `ollama-pool` | `Standard_F8s_v2` (8 vCPU, 16GB) | 2 | Ollama ×2 |
| `general-pool` | `Standard_D4ds_v5` (4 vCPU, 16GB) | 2 (autoscaling) | Iverson API, Jaeger, CoreDNS, AGIC, cluster autoscaler, CSI controller pods |

### 5.2 Azure AKS itemized monthly cost

| Component | Resource | Unit price | Qty / hours | Monthly |
|-----------|----------|-----------|------------|---------|
| **AKS control plane** | Standard tier (Uptime SLA, no charge since 2024) | $0/hr | 730 hrs | $0 |
| **Postgres pool × 2** | `Standard_E8ds_v5` (CloudNativePG) | $0.504/hr | 2 × 730 hrs | $736 |
| Postgres PVC storage | Premium SSD, 1.5TB × 2 replicas | $0.115/GB | 3,072 GB | $353 |
| Postgres backup | pgBackRest → Blob Cool tier, 1TB | $0.01/GB | 1,024 GB | $10 |
| **StarRocks FE** | `Standard_E4ds_v5` | $0.252/hr | 730 hrs | $184 |
| **StarRocks BE × 2** | `Standard_E8ds_v5` | $0.504/hr | 2 × 730 hrs | $736 |
| StarRocks BE storage | Premium SSD v2, 512GB × 2 | $0.131/GB | 1,024 GB | $134 |
| **Qdrant × 2** | `Standard_E8ds_v5`, memmap mode | $0.504/hr | 2 × 730 hrs | $736 |
| Qdrant storage | Premium SSD v2, 512GB × 2 | $0.131/GB | 1,024 GB | $134 |
| **Kafka × 3** | `Standard_D2ds_v5` (Strimzi KRaft) | $0.096/hr | 3 × 730 hrs | $210 |
| Kafka storage | Premium SSD, 200GB × 3 | $0.115/GB | 600 GB | $69 |
| **Ollama × 2** | `Standard_F8s_v2` | $0.338/hr | 2 × 730 hrs | $494 |
| Ollama storage | Standard SSD, 32GB × 2 | $0.016/GB | 64 GB | $1 |
| **General pool × 2** | `Standard_D4ds_v5` (API, Jaeger, add-ons) | $0.192/hr | 2 × 730 hrs | $280 |
| **AGIC → Application Gateway** | WAF_v2, hourly | $0.360/hr | 730 hrs | $263 |
| Application Gateway | Capacity Units | $0.011/CU-hr | ~5 CU × 730 hrs | $40 |
| **Azure Monitor / Log Analytics** | Cluster + node log ingestion | $2.76/GB | ~5 GB | $14 |
| **NAT Gateway** | Processing | $0.045/GB | ~100 GB | $5 |
| NAT Gateway | Hourly | $0.045/hr | 730 hrs | $33 |
| **Data transfer out** | Internet egress | $0.087/GB | ~50 GB | $4 |
| **Container Registry** | Basic tier | $0.167/day | 30 days | $5 |
| | | | **Total (pay-as-you-go)** | **~$4,441/mo** |
| | | | **Total (1-yr reserved nodes)** | **~$3,226/mo** |

> Reserved pricing applies to all VM node costs (excluding the AKS control plane, storage, AGIC, NAT, and data transfer). AKS's Standard tier (recommended for production, includes the financially-backed uptime SLA) carries no separate control-plane fee as of the 2024 pricing change, unlike EKS's flat $0.10/hr.

---

## 6. Cost comparison

| Component | AWS EKS (on-demand) | AWS EKS (1-yr reserved) | Azure AKS (PAYG) | Azure AKS (1-yr reserved) |
|-----------|---------------------|--------------------------|-------------------|----------------------------|
| PostgreSQL (compute + storage + backup) | $1,102 | $837 | $1,099 | $834 |
| StarRocks (FE + 2 BE + storage) | $1,002 | $670 | $1,054 | $722 |
| Qdrant (2 nodes + storage) | $818 | $553 | $870 | $605 |
| Kafka (compute + storage) | $270 | $194 | $279 | $203 |
| Ollama (2 nodes + storage) | $524 | $336 | $495 | $317 |
| Application & control plane (API, Jaeger, general pool, control plane) | $353 | $252 | $280 | $179 |
| Load balancing (ALB / App Gateway) | $35 | $35¹ | $303 | $303¹ |
| Observability + networking | $71 | $71¹ | $61 | $61¹ |
| **Total** | **~$4,175** | **~$2,950** | **~$4,441** | **~$3,226** |

¹ These line items have no reserved/committed-use pricing.

**Both clouds come out cheaper on Kubernetes than the managed-service model in the source document, but for different reasons:**

- **AWS: modestly cheaper** ($4,175 vs $4,313 on-demand; $2,950 vs $3,213 reserved, ~8% lower). MSK's per-broker markup over raw EC2 and RDS's markup over raw EC2 pricing are the main things being traded away; the extra EKS control-plane fee and general-purpose node pool claw a little of that back.
- **Azure: substantially cheaper** ($4,441 vs $5,607 PAYG, ~21% lower; $3,226 vs $4,477 reserved, ~28% lower). This is almost entirely the Kafka line: Event Hubs Premium at $1,352/mo is replaced by 3 `Standard_D2ds_v5` nodes running Strimzi at $279/mo — the same trade-off the source document already flagged in its own §8 caveats, just now realized instead of hypothetical.

This is a pure infrastructure-cost comparison. It does not price in the added engineering time to operate CloudNativePG and Strimzi in place of RDS/Flexible Server and MSK/Event Hubs — see [§10](#10-operational-tradeoffs-of-self-managing-postgres-and-kafka).

---

## 7. Infrastructure diagram

```
                   ┌──── AWS EKS ──────────────────────────────────────────────┐
 Client gRPC ──────► ALB (via AWS Load Balancer Controller, L7, gRPC)
                   │       │
                   │  ┌────┴────────────────────┐
                   │  │ general-pool (2 nodes)   │  Iverson.Api (2-5, HPA) + Jaeger
                   │  │ m6i.xlarge               │  + CoreDNS, ALB ctrl, CSI ctrl
                   │  └──┬────┬────┬────┬────────┘
                   │     │    │    │    │
                   │  postgres- starrocks- qdrant-  kafka-   ollama-
                   │  pool      pool       pool     pool     pool
                   │  2× r7i.2x FE+2×BE    2× r7i.2x 3× m5.l 2× c7i.2x
                   │  CNPG      operator   raft      Strimzi
                   │  primary/  StatefulSet StatefulSet KRaft  Deployment
                   │  standby
                   └───────────────────────────────────────────────────────────┘

                   ┌──── Azure AKS ────────────────────────────────────────────┐
 Client gRPC ──────► App Gateway (via AGIC, L7, gRPC)
                   │       │
                   │  ┌────┴────────────────────┐
                   │  │ general-pool (2 nodes)   │  Iverson.Api (2-5, HPA) + Jaeger
                   │  │ Standard_D4ds_v5         │  + CoreDNS, AGIC, CSI ctrl
                   │  └──┬────┬────┬────┬────────┘
                   │     │    │    │    │
                   │  postgres- starrocks- qdrant-  kafka-   ollama-
                   │  pool      pool       pool     pool     pool
                   │  2× E8ds5  FE+2×BE    2× E8ds5 3× D2ds5 2× F8s_v2
                   │  CNPG      operator   raft      Strimzi
                   │  primary/  StatefulSet StatefulSet KRaft  Deployment
                   │  standby
                   └───────────────────────────────────────────────────────────┘

  CNPG = CloudNativePG operator   Strimzi = Kafka KRaft operator
  All stateful pools are dedicated node pools (nodeSelector + taint/toleration)
  to avoid noisy-neighbor contention; only the general pool is shared/bin-packed.
```

---

## 8. Scaling beyond 1TB

| Component | Limiting factor at >1TB | Next step |
|-----------|--------------------------|-----------|
| **PostgreSQL** | Working set exceeds 64GB RAM per pod; query plans go disk-bound | Resize `postgres-pool` nodes to `r7i.4xlarge`/`Standard_E16ds_v5` (128GB), or add a CloudNativePG read replica for search offload |
| **StarRocks BE** | Scan throughput and JOIN memory exceed 64GB per BE pod | Scale the StatefulSet to a 3rd BE replica; the StarRocks Operator rebalances tablets automatically |
| **Qdrant** | Disk IOPS exceed gp3/Premium SSD v2 baseline; mmap latency increases | Move the `StorageClass` to io2 (AWS) / Ultra Disk (Azure), or scale to a 3rd Qdrant replica (native Raft rebalances shards) |
| **Kafka (Strimzi)** | Partition throughput saturates `m5.large`/`Standard_D2ds_v5` (10MB/s per broker) | Scale the `Kafka` custom resource to more/larger broker pods; Strimzi handles the rolling reassignment |
| **Ollama** | CPU embedding latency backs up write fan-out | Increase HPA-style replica count on the Ollama Deployment, or move to a GPU node pool (`g4dn.xlarge` / `Standard_NC4as_T4_v3`) with a `nodeSelector` |
| **Cluster-wide** | Node pools sized manually per component start to under/over-provision as traffic shifts | Adopt Karpenter (EKS) or the AKS node autoprovisioner for the general pool; keep stateful pools on fixed sizing since they scale by deliberate replica changes, not bursty load |

---

## 9. Kubernetes primitives used

- **StatefulSets** for every component that owns disk state (Postgres, StarRocks FE/BE, Qdrant, Kafka) — stable network identity and per-replica PVCs via `volumeClaimTemplates`.
- **Deployments + HPA** for stateless/replaceable components (Iverson API, Ollama, Jaeger).
- **PodDisruptionBudgets** on every StatefulSet (`minAvailable: 1` for 2-replica sets, `minAvailable: 2` for the 3-broker Kafka set) so voluntary node drains (upgrades, autoscaler scale-down) can't take a component fully offline.
- **Pod anti-affinity** (`topologySpreadConstraints` across AZs/zones) on Postgres, Qdrant, and Kafka replicas — the k8s-native equivalent of RDS Multi-AZ / MSK's 3-AZ broker placement.
- **Init containers** for the Ollama model pull (replacing the `ollama-init` sidecar container in `docker-compose.yml`) and for Iverson API's readiness gate on dependent services.
- **StorageClasses** per component matching the disk type/throughput tier used in the source document's VM sizing (gp3/Premium SSD for Postgres and Kafka, gp3/Premium SSD v2 for StarRocks and Qdrant where higher IOPS matters).

---

## 10. Operational tradeoffs of self-managing PostgreSQL and Kafka

This is the part of the design that doesn't show up in the cost table. Moving Postgres and Kafka from managed services onto the cluster trades a lower infrastructure bill for engineering ownership of failure modes the managed services previously absorbed:

- **Postgres failover** — RDS/Flexible Server handle failover detection and promotion automatically with a documented RTO. CloudNativePG does the same, but the operator, its webhook, and the `Instance Manager` sidecar become components *you* patch and monitor; a bug in the operator's failover logic is now your incident, not AWS/Azure's.
- **Postgres backups** — RDS/Flexible Server backups are continuous and PITR-restorable with a few clicks. `pgBackRest` (or Velero + CSI snapshots) achieves the same PITR capability but the WAL-archiving pipeline, restore testing, and retention policy are self-built and need their own runbook.
- **Kafka upgrades** — MSK/Event Hubs handle broker version upgrades and patching transparently. Strimzi automates the rolling upgrade *mechanics*, but choosing when to upgrade, validating client compatibility, and watching under-replicated-partition metrics during the rollout is now an internal responsibility.
- **On-call surface** — the source document's managed services meant AWS/Azure paged themselves for the underlying infrastructure. Here, `postgres-pool` and `kafka-pool` node and pod health become first-class alerts your team owns, in addition to the application-level alerts you'd already have.
- **What doesn't change** — StarRocks, Qdrant, and Ollama were already self-managed in the source document, so containerizing them onto Kubernetes is a packaging change (VM → pod), not a new operational burden. The Iverson API and Jaeger move from serverless-managed (Fargate/Container Apps) to self-scheduled (Deployment + HPA), which is a smaller step down in "someone else runs this" than Postgres/Kafka.

If the lower Kubernetes cost from §6 doesn't clear the bar of the added CloudNativePG/Strimzi operational load, a hybrid is worth considering: keep RDS/Flexible Server and MSK/Event Hubs as managed services (as in the source document) and run only StarRocks, Qdrant, Ollama, Iverson API, and Jaeger as containers in the same cluster. That gets the packaging/scheduling benefits of Kubernetes for the components that were already self-managed, without taking on Postgres/Kafka HA as an in-house responsibility.

---

## 11. Assumptions and caveats

- **Prices are approximate on-demand/PAYG rates for mid-2026, US East regions**, and reuse the per-instance-type rates from [infrastructure-1tb-aws-azure.md](infrastructure-1tb-aws-azure.md) wherever the same instance type appears, for direct comparability. Verify against the AWS Pricing Calculator and Azure Pricing Calculator before committing.
- **Node counts assume dedicated node pools per stateful component**, matching the "one VM type per component" model in the source document, rather than aggressively bin-packing all pods onto a smaller number of large, uniform nodes. Bin-packing would lower the node count and cost further but reintroduces the noisy-neighbor risk the source document avoided by using dedicated VMs.
- **The general-purpose pool (Iverson API, Jaeger, cluster add-ons) is undersized relative to a fully-loaded production cluster.** Admission webhooks, service meshes, or additional observability agents (if added later) would need their own capacity headroom on top of the 2× `m6i.xlarge`/`Standard_D4ds_v5` baseline here.
- **Egress within the same region/VNet/VPC is free** on both clouds; the data transfer cost above covers only external client traffic, same as the source document.
- **This analysis does not include a dev/staging environment or a CI image registry beyond the basic-tier ACR/ECR listed.** A staging cluster at 10% of this scale adds roughly $350–$550/month, consistent with the source document's estimate.
- **Managed Kubernetes alternatives** (e.g., running Postgres/Kafka on RDS/MSK *alongside* an EKS/AKS cluster for the rest of the stack) are cheaper to operate but were intentionally excluded here per the "all services in docker containers" requirement — see [§10](#10-operational-tradeoffs-of-self-managing-postgres-and-kafka) for when that tradeoff is worth revisiting.
