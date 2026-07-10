# Iverson Infrastructure at 1TB Scale: AWS and Azure

This document sizes cloud infrastructure for a production Iverson deployment carrying 1TB of entity data, then provides itemized monthly cost estimates for AWS and Azure. All prices are approximate on-demand rates for US East regions as of mid-2026; 1-year reserved commitments reduce compute costs by 30–40%.

---

## 1. Data distribution model

"1TB of data" refers to the raw entity payload stored in PostgreSQL. Each Iverson subsystem holds a different projection of that data at a different size.

| Subsystem | What it stores | Estimated size at 1TB raw |
|-----------|---------------|--------------------------|
| **PostgreSQL** | Full JSONB entity objects (source of truth) | ~1.4TB on disk (data + indexes + WAL) |
| **StarRocks** | Indexed scalar fields, columnar (5–8× compression) | ~150–300GB |
| **Qdrant** | Float32 embeddings (768-dim) + HNSW graphs | ~250–400GB per replica |
| **Kafka** | 7-day event log (ingestion replay buffer) | ~100–200GB (log retention) |
| **Ollama** | Model weights (nomic-embed-text, stateless) | ~300MB in memory |

**Working assumptions:**
- Average entity size: ~20KB → ~50M entities in 1TB
- 50% of entities carry embeddings → ~25M vectors → ~75GB raw float32 at 768 dims
- HNSW index overhead: ~2–3× raw vector size → ~225GB total Qdrant data per replica
- StarRocks stores indexed scalar fields only (not the full JSONB body) with columnar compression → ~200GB
- Kafka retention: 7 days × moderate write throughput → ~150GB across all partitions

---

## 2. Architecture overview

Iverson runs six distinct infrastructure roles. Neither AWS nor Azure offers managed services for StarRocks or Qdrant, so both clouds require VMs for those components.

| Role | AWS option | Azure option |
|------|-----------|-------------|
| Entity store (PostgreSQL) | Amazon RDS PostgreSQL (managed) | Azure Database for PostgreSQL Flexible Server (managed) |
| HTAP store (StarRocks) | EC2 (self-managed) | Azure VMs (self-managed) |
| Vector store (Qdrant) | EC2 (self-managed) | Azure VMs (self-managed) |
| Event stream (Kafka) | Amazon MSK (managed) | Azure Event Hubs — Kafka protocol (managed) |
| Embedding service (Ollama) | EC2 (self-managed) | Azure VMs (self-managed) |
| Application API (Iverson.Api) | ECS Fargate (managed containers) | Azure Container Apps (managed containers) |
| Distributed tracing (Jaeger) | Amazon OpenSearch / CloudWatch | Azure Monitor + Application Insights |
| Load balancing | Application Load Balancer (ALB) | Azure Application Gateway |
| Storage (block) | EBS gp3 | Azure Premium SSD v2 |
| Networking | VPC, NAT Gateway, PrivateLink | VNet, NAT Gateway, Private Endpoint |

---

## 3. AWS configuration

### 3.1 Component sizing

**PostgreSQL — Amazon RDS Multi-AZ**

At 1TB of entity data, the critical constraint is RAM for the active working set. The `db.r7g.2xlarge` (8 vCPU, 64GB RAM) handles moderate query concurrency; upgrade to `db.r7g.4xlarge` (16 vCPU, 128GB) if read throughput is heavy.

- 1 Multi-AZ deployment (primary + standby in separate AZs)
- 1.5TB gp3 storage (1TB data + headroom for WAL, vacuum, and temp)
- Automated backups: 7-day PITR retention

**StarRocks — EC2**

StarRocks separates Frontend (FE, metadata/query coordination) and Backend (BE, storage/execution). At 200GB compressed data with analytical query workloads, each BE needs ~64GB RAM to keep hot segments in the query execution buffer.

- 1× FE: `r7i.xlarge` (4 vCPU, 32GB RAM) — metadata and query planning only
- 2× BE: `r7i.2xlarge` (8 vCPU, 64GB RAM) — storage and MPP execution
- 2× 500GB EBS gp3 for BE storage (1TB total, ~5× headroom over 200GB compressed)
- FE and BEs in separate AZs for zone fault tolerance

**Qdrant — EC2**

Qdrant's HNSW index can run in memory-mapped mode (on-disk with mmap), which reduces RAM requirements at the cost of higher read latency on cold queries. At 225GB per replica, memmap mode on NVMe-backed EBS is the viable path without requiring 256GB+ RAM instances.

- 2× `r7i.2xlarge` (8 vCPU, 64GB RAM) with memmap mode enabled
- 2× 500GB EBS gp3 per node (500GB data + HNSW graph space)
- Qdrant replication factor 2 (both nodes serve reads; one is the leader per shard)

**Kafka — Amazon MSK**

MSK removes the Zookeeper operational burden and supports KRaft mode on recent Kafka versions. Three brokers across three AZs provide fault tolerance with replication factor 3.

- 3× `kafka.m5.large` brokers (2 vCPU, 8GB RAM each)
- 200GB EBS storage per broker (600GB total)
- TLS encryption in transit enabled by default on MSK

**Ollama — EC2**

The nomic-embed-text model (~270MB weights) runs comfortably on CPU. For production embedding throughput under sustained load, 2 instances behind a load balancer prevent the embedding service from becoming a bottleneck under fan-out write spikes.

- 2× `c7i.2xlarge` (8 vCPU, 16GB RAM)
- 20GB EBS gp3 per instance (model weights + temp)

**Iverson API — ECS Fargate**

Stateless gRPC service. Two tasks provide high availability; three during peak load. Each task needs ~2 vCPU and 4GB for the .NET runtime under typical query concurrency.

- 3× Fargate tasks (2 vCPU, 4GB RAM each)
- ALB target group with gRPC health checks on port 8080

**Observability**

Amazon CloudWatch replaces the docker-compose Jaeger container in production. Traces can be sent to AWS X-Ray instead of the OTLP collector; logs go to CloudWatch Logs.

- CloudWatch Logs: standard ingestion + 30-day retention
- AWS X-Ray: trace sampling at 5%
- CloudWatch Alarms: CPU, memory, RDS connections, MSK consumer lag

---

### 3.2 AWS itemized monthly cost

All prices are on-demand, us-east-1. Reserved 1-year (no upfront) reduces compute ~36%.

| Component | Resource | Unit price | Qty / hours | Monthly |
|-----------|----------|-----------|------------|---------|
| **RDS PostgreSQL** | db.r7g.2xlarge Multi-AZ | $1.164/hr | 730 hrs | $850 |
| RDS storage | gp3, 1.5TB | $0.115/GB | 1,536 GB | $177 |
| RDS backup storage | PITR, 1TB retained | $0.095/GB | 1,024 GB | $97 |
| **StarRocks FE** | r7i.xlarge EC2 | $0.252/hr | 730 hrs | $184 |
| **StarRocks BE × 2** | r7i.2xlarge EC2 | $0.504/hr | 2 × 730 hrs | $736 |
| StarRocks BE storage | EBS gp3, 500GB × 2 | $0.08/GB | 1,024 GB | $82 |
| **Qdrant × 2** | r7i.2xlarge EC2 | $0.504/hr | 2 × 730 hrs | $736 |
| Qdrant storage | EBS gp3, 500GB × 2 | $0.08/GB | 1,024 GB | $82 |
| **Kafka (MSK)** | kafka.m5.large × 3 | $0.210/hr | 3 × 730 hrs | $460 |
| MSK storage | EBS, 200GB × 3 brokers | $0.10/GB | 600 GB | $60 |
| **Ollama × 2** | c7i.2xlarge EC2 | $0.357/hr | 2 × 730 hrs | $521 |
| Ollama storage | EBS gp3, 20GB × 2 | $0.08/GB | 40 GB | $3 |
| **Iverson API (Fargate)** | 3 tasks × 2 vCPU | $0.04048/vCPU-hr | 6 vCPU × 730 hrs | $177 |
| Iverson API (Fargate) | 3 tasks × 4GB RAM | $0.004445/GB-hr | 12 GB × 730 hrs | $39 |
| **Application Load Balancer** | ALB hourly | $0.008/hr | 730 hrs | $6 |
| ALB — LCU | Load Balancer Capacity Units | $0.008/LCU-hr | ~5 LCU × 730 | $29 |
| **CloudWatch Logs** | Log ingestion | $0.50/GB | ~20 GB | $10 |
| CloudWatch Logs | Log storage, 30-day | $0.03/GB | ~600 GB | $18 |
| **AWS X-Ray** | Trace recording | $5.00/M traces | ~0.5M | $3 |
| **NAT Gateway** | Data processing | $0.045/GB | ~100 GB | $5 |
| NAT Gateway | Hourly | $0.045/hr | 730 hrs | $33 |
| **Data transfer out** | Internet egress | $0.09/GB | ~50 GB | $5 |
| | | | **Total (on-demand)** | **~$4,313/mo** |
| | | | **Total (1-yr reserved compute)** | **~$3,030/mo** |

> Reserved pricing applies the ~36% discount to all EC2 and RDS instance costs (all rows with an hourly instance rate). Storage, MSK, Fargate, and data transfer charges are unchanged.

---

## 4. Azure configuration

### 4.1 Component sizing

Azure's compute families map closely to AWS but with different names. The equivalents used here:

| AWS | Azure | vCPU | RAM |
|-----|-------|------|-----|
| r7i.xlarge | Standard_E4ds_v5 | 4 | 32GB |
| r7i.2xlarge | Standard_E8ds_v5 | 8 | 64GB |
| c7i.2xlarge | Standard_F8s_v2 | 8 | 16GB |

**PostgreSQL — Azure Database for PostgreSQL Flexible Server**

Memory Optimized tier (`MO_Standard_E8ds_v5`, 8 vCPU, 64GB RAM) with Zone-Redundant High Availability (primary + standby in separate availability zones).

- 1 Zone-Redundant HA deployment
- 1.5TB storage (Premium SSD)
- 7-day backup retention with geo-redundant backup

**StarRocks — Azure VMs (Standard_E series)**

Same FE + 2× BE topology as AWS.

- 1× FE: `Standard_E4ds_v5` (4 vCPU, 32GB RAM)
- 2× BE: `Standard_E8ds_v5` (8 vCPU, 64GB RAM)
- 2× 512GB Azure Premium SSD v2 for BE storage

**Qdrant — Azure VMs**

- 2× `Standard_E8ds_v5` (8 vCPU, 64GB RAM), memmap mode
- 2× 512GB Azure Premium SSD v2

**Kafka — Azure Event Hubs (Premium tier)**

Azure Event Hubs exposes the Kafka protocol. Iverson's `KafkaConsumer` and `KafkaProducer` connect with no code changes — only bootstrap server and listener addresses differ. Event Hubs Premium provides dedicated capacity, 7-day retention, and private endpoint support.

- 1 Event Hubs namespace, Premium tier
- 2 Processing Units (PU) — each PU handles ~1,000 events/sec ingress; 2 PU provides ample headroom for fan-out writes
- 7-day event retention included in Premium

> **Note:** Event Hubs does not support all Kafka APIs (no `kafka-reassign-partitions`, limited admin operations). Iverson's producer/consumer pattern is fully compatible. The Confluent Schema Registry is not available — not required for Iverson since schema management is internal.

**Ollama — Azure VMs**

- 2× `Standard_F8s_v2` (8 vCPU, 16GB RAM compute-optimized)
- 32GB OS disk per instance (Standard SSD)

**Iverson API — Azure Container Apps**

- 3 replicas, 2 vCPU / 4GB each, consumption plan
- Azure Container Registry for image storage (~5GB)

**Observability**

Azure Application Insights replaces Jaeger. The Iverson OTLP exporter (`Otel__Endpoint`) points to the Application Insights OTLP endpoint; no code changes are needed.

- Application Insights: custom metrics + trace ingestion
- Azure Monitor Logs (Log Analytics Workspace): container and VM logs

---

### 4.2 Azure itemized monthly cost

All prices are Pay-As-You-Go, East US. 1-year Reserved VM Instances reduce compute ~36%.

| Component | Resource | Unit price | Qty / hours | Monthly |
|-----------|----------|-----------|------------|---------|
| **PostgreSQL Flexible Server** | MO_Standard_E8ds_v5 ZR-HA | $1.176/hr | 730 hrs | $859 |
| PostgreSQL storage | Premium SSD, 1.5TB | $0.115/GB | 1,536 GB | $177 |
| PostgreSQL backup | Geo-redundant, 1TB | $0.095/GB | 1,024 GB | $97 |
| **StarRocks FE** | Standard_E4ds_v5 | $0.252/hr | 730 hrs | $184 |
| **StarRocks BE × 2** | Standard_E8ds_v5 | $0.504/hr | 2 × 730 hrs | $736 |
| StarRocks BE storage | Premium SSD v2, 512GB × 2 | $0.131/GB | 1,024 GB | $134 |
| **Qdrant × 2** | Standard_E8ds_v5 | $0.504/hr | 2 × 730 hrs | $736 |
| Qdrant storage | Premium SSD v2, 512GB × 2 | $0.131/GB | 1,024 GB | $134 |
| **Event Hubs Premium** | 1 namespace, 2 PU | $0.926/PU-hr | 2 PU × 730 hrs | $1,352 |
| Event Hubs storage | 7-day retention included in Premium | — | — | $0 |
| **Ollama × 2** | Standard_F8s_v2 | $0.338/hr | 2 × 730 hrs | $494 |
| Ollama storage | Standard SSD, 32GB × 2 | $0.016/GB | 64 GB | $1 |
| **Iverson API (Container Apps)** | 3 replicas × 2 vCPU | $0.000016/vCPU-sec | 6 vCPU × 2.63M sec | $253 |
| Iverson API (Container Apps) | 3 replicas × 4GB RAM | $0.000002/GiB-sec | 12 GB × 2.63M sec | $63 |
| **Azure Application Gateway** | WAF_v2, hourly | $0.360/hr | 730 hrs | $263 |
| Application Gateway | Capacity Units | $0.011/CU-hr | ~5 CU × 730 hrs | $40 |
| **Application Insights** | Log ingestion | $2.30/GB | ~10 GB | $23 |
| **Log Analytics** | Log ingestion | $2.76/GB | ~5 GB | $14 |
| **NAT Gateway** | Processing | $0.045/GB | ~100 GB | $5 |
| NAT Gateway | Hourly | $0.045/hr | 730 hrs | $33 |
| **Data transfer out** | Internet egress | $0.087/GB | ~50 GB | $4 |
| **Container Registry** | Basic tier | $0.167/day | 30 days | $5 |
| | | | **Total (pay-as-you-go)** | **~$5,607/mo** |
| | | | **Total (1-yr reserved compute)** | **~$4,144/mo** |

> Reserved pricing applies to all VM and PostgreSQL Flexible Server instance costs. Container Apps, Event Hubs, Application Gateway, and storage are unchanged.

---

## 5. Cost comparison

| Component | AWS (on-demand) | AWS (1-yr reserved) | Azure (PAYG) | Azure (1-yr reserved) |
|-----------|----------------|--------------------|--------------|-----------------------|
| PostgreSQL | $1,124 | $755 | $1,133 | $760 |
| StarRocks (FE + 2 BE + storage) | $1,002 | $686 | $1,054 | $720 |
| Qdrant (2 nodes + storage) | $818 | $560 | $870 | $595 |
| Kafka | $520 | $520¹ | $1,352 | $1,352¹ |
| Ollama (2 nodes) | $524 | $367 | $495 | $347 |
| Iverson API | $216 | $216¹ | $316 | $316¹ |
| Load balancing | $35 | $35¹ | $303 | $303¹ |
| Observability + networking | $74 | $74¹ | $84 | $84¹ |
| **Total** | **~$4,313** | **~$3,213** | **~$5,607** | **~$4,477** |

¹ These services have no reserved pricing — spot or commitment savings do not apply.

**AWS is approximately 25% cheaper than Azure at this configuration**, driven by two factors:

1. **Event Hubs Premium vs MSK:** The largest single gap. MSK at 3 × `kafka.m5.large` costs $460/month; Event Hubs Premium at 2 PU costs $1,352/month. Azure Event Hubs Standard would cost ~$140/month but caps retention at 1 day (7-day retention requires Premium or a self-managed Kafka cluster on Azure VMs).

2. **Application Gateway vs ALB:** Azure's WAF-enabled Application Gateway tier costs ~$303/month vs ~$35 for an AWS ALB. Azure Load Balancer Standard (~$18/month) is the closer analog to ALB but lacks the L7 routing Iverson's gRPC health checks depend on.

---

## 6. Infrastructure diagram

```
                   ┌──── AWS ─────────────────────────────────────────┐
 Client gRPC ──────► ALB (L7, gRPC)
                   │       │
                   │  ┌────┴───────────────────┐
                   │  │  ECS Fargate (3 tasks) │  Iverson.Api
                   │  │  2 vCPU / 4GB each     │
                   │  └──┬────┬────┬────┬──────┘
                   │     │    │    │    │
                   │  RDS  MSK  EC2  EC2   EC2
                   │  PG   KAF  SR   QD   OLL
                   │  MA    3B  FE+2B 2N   2N
                   │        │
                   │  (fan-out events from Iverson.Events → MSK → Iverson.Events consumer)
                   └──────────────────────────────────────────────────┘

                   ┌──── Azure ────────────────────────────────────────┐
 Client gRPC ──────► App Gateway (L7, gRPC)
                   │       │
                   │  ┌────┴──────────────────────┐
                   │  │ Container Apps (3 replicas)│  Iverson.Api
                   │  │ 2 vCPU / 4GB each          │
                   │  └──┬────┬────┬────┬──────────┘
                   │     │    │    │    │
                   │  PG  EH   VM   VM   VM
                   │  Flex Prem SR  QD  OLL
                   │  ZR  2PU FE+2B 2N  2N
                   └──────────────────────────────────────────────────┘

  SR = StarRocks   QD = Qdrant   OLL = Ollama   PG = PostgreSQL
  KAF = MSK Kafka  EH = Event Hubs   ZR = Zone-Redundant HA   MA = Multi-AZ
```

---

## 7. Scaling beyond 1TB

These estimates assume a single-region deployment with modest query concurrency. The components that hit limits first as data grows past 1TB:

| Component | Limiting factor at >1TB | Next step |
|-----------|------------------------|-----------|
| **PostgreSQL** | Working set exceeds 64GB RAM; query plans go disk-bound | `db.r7g.4xlarge` (128GB) or read replica for search offload |
| **StarRocks BE** | Scan throughput and JOIN memory exceed 64GB per BE | Add a 3rd BE node; horizontal scale is StarRocks' strength |
| **Qdrant** | Disk IOPS exceed gp3 baseline; mmap latency increases | Upgrade to io2 EBS (AWS) or Ultra Disk (Azure); or add a 3rd Qdrant node |
| **Kafka (MSK)** | Partition throughput saturates m5.large (10MB/s per broker) | Upgrade to `kafka.m5.xlarge` or add brokers |
| **Ollama** | CPU embedding latency backs up write fan-out | Add instances; or migrate to GPU (`g4dn.xlarge` / `Standard_NC4as_T4_v3`) |

---

## 8. Assumptions and caveats

- **Prices are approximate on-demand rates for mid-2026, US East regions.** Cloud pricing changes frequently; verify against the AWS Pricing Calculator and Azure Pricing Calculator before committing.
- **Storage throughput costs (EBS IOPS provisioning, Premium SSD performance tiers) are excluded.** Baseline gp3 and Premium SSD v2 IOPS are sufficient for this workload; provisioned IOPS would add cost if write latency SLAs require it.
- **Egress within the same region/VPC is free** on both clouds; the data transfer cost above covers only external client traffic.
- **Qdrant Cloud** (managed SaaS, cloud-provider agnostic) is an alternative to self-managed EC2/VM deployment. At 225GB of vector data, Qdrant Cloud's pricing would be approximately $400–$600/month per cluster, which may undercut the VM cost while eliminating operational burden.
- **This analysis does not include a dev/staging environment.** A staging environment at 10% of this scale adds roughly $350–$550/month on either cloud.
- **Kafka on Azure VMs** is a viable alternative to Event Hubs Premium that would reduce Azure's Kafka cost from $1,352 to ~$450/month (3× `Standard_D4ds_v5` VMs + disks). The trade-off is losing the managed service and having to operate Kafka directly.
