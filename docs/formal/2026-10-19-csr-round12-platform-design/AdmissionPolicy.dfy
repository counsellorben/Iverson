// Spec: /home/ben/repositories/Iverson/docs/specs/2026-10-19-csr-round12-platform-design.md @ a782ac7674fde9185ad55bb1a986df3008adf050
// Source: §3.1 "What renders", "Rules" and "Consequences", lines 228-243
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-19-csr-round12-platform-design/AdmissionPolicy.dfy
// Round: 2

// ---------------------------------------------------------------------------
// Model
// ---------------------------------------------------------------------------

// A service account identity: its namespace and name. Namespace-qualified so the
// binding's namespaceSelector on the release namespace (line 230) is part of the
// identity: a same-named account in another namespace is a different account.
// NoSA stands for "no old object" (oldObject is null on CREATE).
datatype SA = SA(ns: string, name: string) | NoSA

// A pod reference: its namespace and name. CONNECT carries only the pod name
// (line 235); a pod CREATE is seen with its final, generated name (line 236, VA 96).
datatype PodRef = PodRef(ns: string, name: string)

// spec.containers[*].image and spec.initContainers[*].image (line 239).
datatype PodImages = PodImages(containers: seq<string>, initContainers: seq<string>)

// (b): pods/exec, pods/attach, pods/portforward (line 235).
datatype ConnectKind = Exec | Attach | PortForward

// (d): Deployments, ReplicaSets, StatefulSets, DaemonSets, Jobs and CronJobs (line 237).
datatype WorkloadKind = Deployment | ReplicaSet | StatefulSet | DaemonSet | Job | CronJob

// (d): CREATE and UPDATE (line 237).
datatype WriteVerb = Create | Update

// The admission request's operation, in the spec's order of rules (a)-(f),
// followed by the requests no rule names.
datatype Operation =
  | TokenCreate(sa: SA)                                   // (a) CREATE serviceaccounts/token
  | PodConnect(kind: ConnectKind, pod: PodRef)             // (b) CONNECT pods/exec|attach|portforward
  | PodCreate(pod: PodRef, saName: string)                // (c) Pod CREATE: the pod and its serviceAccountName
  | WorkloadWrite(verb: WriteVerb, wkind: WorkloadKind,
                  oldSa: SA, newSa: SA)                   // (d) template SA in oldObject / object
  | EphemeralUpdate(pod: PodRef)                          // (e) UPDATE pods/ephemeralcontainers
  | PodUpdate(pod: PodRef, oldImages: PodImages,
              newImages: PodImages)                       // (f) UPDATE pods (main resource)
  | ScaleUpdate(wkind: WorkloadKind)                      // UPDATE through the scale subresource (line 243)
  | OtherOp                                               // anything else

// request.userInfo.username and request.userInfo.groups (line 233).
datatype Request = Request(user: string, groups: set<string>, op: Operation)

// Rendered values: the release name, the release namespace (namespaceSelector,
// line 230) and admission.operatorGroups (line 231; render-checked non-empty,
// which no property here depends on).
datatype AdmissionConfig = AdmissionConfig(rel: string, releaseNs: string, operatorGroups: set<string>)
{
  // "<rel>-api and <rel>-admin-automation" (line 234), in the release namespace.
  const protectedAccounts: set<SA> :=
    {SA(releaseNs, rel + "-api"), SA(releaseNs, rel + "-admin-automation")}
}

function KubeSystemSA(name: string): string
{
  "system:serviceaccount:kube-system:" + name
}

// (c) line 236: the controllers admitted for bare pod CREATE.
function PodCreateControllers(): set<string>
{
  {KubeSystemSA("replicaset-controller"), KubeSystemSA("statefulset-controller"),
   KubeSystemSA("job-controller"), KubeSystemSA("daemon-set-controller")}
}

// (d) line 237: the controllers admitted for workload template writes.
function WorkloadControllers(): set<string>
{
  {KubeSystemSA("deployment-controller"), KubeSystemSA("replicaset-controller"),
   KubeSystemSA("cronjob-controller"), KubeSystemSA("job-controller"),
   KubeSystemSA("generic-garbage-collector")}
}

// Binding namespaceSelector on the release namespace (line 230).
predicate InReleaseNs(ns: string, cfg: AdmissionConfig)
{
  ns == cfg.releaseNs
}

// A protected account: namespaceSelector (line 230) and the two names (line 234).
predicate ProtectedSA(sa: SA, cfg: AdmissionConfig)
{
  sa.SA? && InReleaseNs(sa.ns, cfg) && sa in cfg.protectedAccounts
}

// (b) line 235: pods named <rel>-api-*, <rel>-worker-*, <rel>-authentik-*, <rel>-postgres-*,
// in the release namespace (line 230).
predicate ProtectedPod(p: PodRef, cfg: AdmissionConfig)
{
  InReleaseNs(p.ns, cfg) &&
  ( (cfg.rel + "-api-") <= p.name
  || (cfg.rel + "-worker-") <= p.name
  || (cfg.rel + "-authentik-") <= p.name
  || (cfg.rel + "-postgres-") <= p.name )
}

// (f) line 239: any container or init-container image differs between oldObject and object.
predicate ImagesDiffer(oldI: PodImages, newI: PodImages)
{
  oldI.containers != newI.containers || oldI.initContainers != newI.initContainers
}

// Whether a rule (a)-(f) matches the request (lines 234-239; line 243 for scale).
predicate Matched(r: Request, cfg: AdmissionConfig)
{
  match r.op
  case TokenCreate(sa) => ProtectedSA(sa, cfg)                                     // (a)
  case PodConnect(_, pod) => ProtectedPod(pod, cfg)                                // (b)
  case PodCreate(pod, saName) => ProtectedSA(SA(pod.ns, saName), cfg)              // (c)
  case WorkloadWrite(_, _, oldSa, newSa) =>
    ProtectedSA(oldSa, cfg) || ProtectedSA(newSa, cfg)                             // (d)
  case EphemeralUpdate(pod) => ProtectedPod(pod, cfg)                              // (e)
  case PodUpdate(pod, oldI, newI) => ProtectedPod(pod, cfg) && ImagesDiffer(oldI, newI) // (f)
  case ScaleUpdate(_) => false     // line 243: "other than through the `scale` subresource"
  case OtherOp => false
}

// The requester is in admission.operatorGroups (line 233).
predicate Operator(r: Request, cfg: AdmissionConfig)
{
  r.groups * cfg.operatorGroups != {}
}

// The per-rule "Also admitted" requesters of the rule the request's operation falls under.
predicate ExemptRequester(r: Request)
{
  match r.op
  case TokenCreate(_) => "system:nodes" in r.groups                 // (a) the kubelet (line 234)
  case PodConnect(_, _) => false                                     // (b) none
  case PodCreate(_, _) => r.user in PodCreateControllers()          // (c) line 236
  case WorkloadWrite(_, _, _, _) => r.user in WorkloadControllers() // (d) line 237
  case EphemeralUpdate(_) => false                                   // (e) none
  case PodUpdate(_, _, _) => false                                   // (f) none
  case ScaleUpdate(_) => false
  case OtherOp => false
}

// (c) line 236: "Whoever creates it, the operators group and those controllers
// included, a pod with either account is admitted only when its name starts with
// `<rel>-api-`". Only rule (c) carries a clause on the object beyond its match.
predicate NameClause(r: Request, cfg: AdmissionConfig)
{
  match r.op
  case PodCreate(pod, _) => (cfg.rel + "-api-") <= pod.name
  case _ => true
}

// An unmatched request is admitted. A matched rule admits operators (line 233) and its
// own "also admitted" requesters (lines 234, 236, 237), and denies everyone else,
// cluster-admin included (line 233); rule (c) admits them only under its name clause
// (line 236).
predicate Admits(r: Request, cfg: AdmissionConfig)
{
  !Matched(r, cfg) || ((Operator(r, cfg) || ExemptRequester(r)) && NameClause(r, cfg))
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

// A prefix of a sequence is a prefix of that sequence extended.
lemma PrefixOfConcat(a: string, b: string)
  ensures a <= a + b
{
  assert (a + b)[..|a|] == a;
}

// Concrete scenario for line 233 against line 236: an operator's bare pod `steal`
// running as `<rel>-api` (VA 96's name) is refused by the name clause.
lemma OperatorMisnamedPodRefused()
  ensures var cfg := AdmissionConfig("iverson", "iverson", {"iverson-operators"});
          var r := Request("alice", {"iverson-operators"},
                           PodCreate(PodRef("iverson", "steal"), "iverson-api"));
          r.groups * cfg.operatorGroups != {} && !Admits(r, cfg)
{
  var cfg := AdmissionConfig("iverson", "iverson", {"iverson-operators"});
  var r := Request("alice", {"iverson-operators"},
                   PodCreate(PodRef("iverson", "steal"), "iverson-api"));
  assert "iverson-operators" in r.groups * cfg.operatorGroups;
  assert cfg.rel + "-api" == "iverson-api";
  assert SA("iverson", "iverson-api") in cfg.protectedAccounts;
  assert Matched(r, cfg);
  assert "steal"[0] != (cfg.rel + "-api-")[0];
  assert !((cfg.rel + "-api-") <= "steal");
}

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------

// Citation: "Each rule admits a requester whose `request.userInfo.groups` intersects `admission.operatorGroups`, and denies everyone else." (§3.1 line 233)
lemma P1_OperatorsAdmitted(r: Request, cfg: AdmissionConfig)
  requires r.groups * cfg.operatorGroups != {}
  ensures Admits(r, cfg)
{
}

// Citation: "Cluster-admin is denied too, by design (Verified assumption 37)" (§3.1 line 233)
lemma P2_ClusterAdminDenied(r: Request, cfg: AdmissionConfig)
  requires r.groups * cfg.operatorGroups == {}
  requires Matched(r, cfg) && !ExemptRequester(r)
  ensures !Admits(r, cfg)
{
}

// Citation: "Metadata-only pod updates are not matched (Verified assumption 77)." (§3.1 line 239)
lemma P3_MetadataOnlyPodUpdateAdmitted(r: Request, cfg: AdmissionConfig)
  requires r.op.PodUpdate? && r.op.oldImages == r.op.newImages
  ensures Admits(r, cfg)
{
}

// Citation: "The kubelet (`system:nodes`) is also admitted, so projected tokens keep mounting." (§3.1 line 234)
lemma P4_KubeletMintsTokens(r: Request, cfg: AdmissionConfig)
  requires r.op.TokenCreate? && "system:nodes" in r.groups
  ensures Admits(r, cfg)
{
}

// Citation: "creates or updates (other than through the `scale` subresource) a workload whose pod template uses either account" (§3.1 line 243)
lemma P5_ScaleAdmitted(r: Request, cfg: AdmissionConfig)
  requires r.op.ScaleUpdate?
  ensures Admits(r, cfg)
{
}

// Citation: "Without (d), anyone who can create a Job could run a pod as `<rel>-admin-automation` through the job controller, which (c) admits." (§3.1 line 237)
lemma P6_NoProtectedPodOrTemplateForNonOperators(r: Request, cfg: AdmissionConfig)
  requires r.groups * cfg.operatorGroups == {} && !ExemptRequester(r)
  requires (r.op.PodCreate? && SA(r.op.pod.ns, r.op.saName) in cfg.protectedAccounts)
        || (r.op.WorkloadWrite? && (r.op.oldSa in cfg.protectedAccounts || r.op.newSa in cfg.protectedAccounts))
  ensures !Admits(r, cfg)
{
}

// Citation: "Whoever creates it, the operators group and those controllers included, a pod with either account is admitted only when its name starts with `<rel>-api-`" (§3.1 line 236)
lemma P7_MisnamedProtectedPodRefusedForAll(r: Request, cfg: AdmissionConfig)
  requires r.op.PodCreate? && SA(r.op.pod.ns, r.op.saName) in cfg.protectedAccounts
  requires !((cfg.rel + "-api-") <= r.op.pod.name)
  ensures !Admits(r, cfg)
{
}

// Citation: "so (b), (e) and (f) match every pod that holds a protected token" (§3.1 line 236)
lemma P8_AdmittedProtectedPodIsGated(r: Request, cfg: AdmissionConfig)
  requires r.op.PodCreate? && SA(r.op.pod.ns, r.op.saName) in cfg.protectedAccounts
  requires Admits(r, cfg)
  ensures ProtectedPod(r.op.pod, cfg)
{
}

// Citation: "the api's ReplicaSet pods (`<rel>-api-<hash>-<suffix>`) already do (Verified assumption 96)" (§3.1 line 236)
lemma P9_ApiReplicaSetPodsAdmitted(cfg: AdmissionConfig, groups: set<string>, hash: string, suffix: string)
  ensures Admits(Request(KubeSystemSA("replicaset-controller"), groups,
                         PodCreate(PodRef(cfg.releaseNs, cfg.rel + "-api-" + hash + "-" + suffix), cfg.rel + "-api")), cfg)
{
  var name := cfg.rel + "-api-" + hash + "-" + suffix;
  assert name == (cfg.rel + "-api-") + (hash + "-" + suffix);
  PrefixOfConcat(cfg.rel + "-api-", hash + "-" + suffix);
  assert KubeSystemSA("replicaset-controller") in PodCreateControllers();
}
