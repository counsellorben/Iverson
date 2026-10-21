// Spec: /home/ben/repositories/Iverson/docs/specs/2026-10-19-csr-round12-platform-design.md @ a11e848d722d05cc808840ae796e6afdb2101aa2
// Source: §3.1 "What renders", "Rules" and "Consequences", lines 228-243
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-19-csr-round12-platform-design/AdmissionPolicy.dfy
// Round: 3

// ---------------------------------------------------------------------------
// Configuration (lines 228-231)
// ---------------------------------------------------------------------------

// A service account: namespace and name.
datatype SA = SA(ns: string, name: string)

// The release: `<rel>`, its namespace (the binding's namespaceSelector, line 230)
// and `admission.operatorGroups` (line 231).
datatype AdmissionConfig = AdmissionConfig(rel: string, releaseNs: string, operatorGroups: set<string>)
{
  // `<rel>-api` and `<rel>-admin-automation` (lines 234, 236), in the release namespace.
  const protectedAccounts: set<SA> :=
    {SA(releaseNs, rel + "-api"), SA(releaseNs, rel + "-admin-automation")}
}

// `admission.operatorGroups` is non-empty, render-checked (line 231).
predicate RendersOk(cfg: AdmissionConfig)
{
  cfg.operatorGroups != {}
}

// ---------------------------------------------------------------------------
// Requests
// ---------------------------------------------------------------------------

// The requester's user name. KubeSystemSA(n) is
// `system:serviceaccount:kube-system:<n>` (lines 236, 237).
datatype User = KubeSystemSA(saName: string) | OtherUser(userName: string)

datatype PodRef = PodRef(ns: string, name: string)

datatype ConnectSub = Exec | Attach | PortForward

datatype WorkloadKind = Deployment | ReplicaSet | StatefulSet | DaemonSet | Job | CronJob

datatype Verb = Create | Update

datatype Op =
    // (a) CREATE on serviceaccounts/token for account `sa` (line 234)
    TokenCreate(sa: SA)
    // (b) CONNECT on pods/exec|attach|portforward (line 235)
  | PodConnect(sub: ConnectSub, pod: PodRef)
    // (c) Pod CREATE with spec.serviceAccountName `saName`, in pod.ns (line 236)
  | PodCreate(pod: PodRef, saName: string)
    // (d) CREATE/UPDATE of a workload object (main resource) in namespace `wns`
    //     whose pod template's serviceAccountName is oldSaName (oldObject) and
    //     newSaName (object) (line 237)
  | WorkloadWrite(verb: Verb, kind: WorkloadKind, wns: string, oldSaName: string, newSaName: string)
    // UPDATE through a workload's `scale` subresource (line 243)
  | ScaleUpdate(skind: WorkloadKind, sns: string)
    // (e) UPDATE on pods/ephemeralcontainers (line 238)
  | EphemeralUpdate(pod: PodRef)
    // (f) UPDATE on pods (main resource); images = every spec.containers[*].image
    //     and spec.initContainers[*].image, oldObject vs object (line 239)
  | PodUpdate(pod: PodRef, oldImages: seq<string>, newImages: seq<string>)
    // Anything else the policy does not name.
  | OtherOp(ons: string)
{
  // The template's service account in oldObject. A CREATE has no oldObject, so
  // the only template is the new one.
  const oldSa: SA :=
    if this.WorkloadWrite? then
      (if this.verb == Create then SA(this.wns, this.newSaName) else SA(this.wns, this.oldSaName))
    else SA("", "")
  // The template's service account in object.
  const newSa: SA :=
    if this.WorkloadWrite? then SA(this.wns, this.newSaName) else SA("", "")
}

datatype Request = Request(user: User, groups: set<string>, op: Op)

// The request's namespace, which the binding's namespaceSelector tests (line 230).
function RequestNs(op: Op): string
{
  match op
  case TokenCreate(sa) => sa.ns
  case PodConnect(_, pod) => pod.ns
  case PodCreate(pod, _) => pod.ns
  case WorkloadWrite(_, _, wns, _, _) => wns
  case ScaleUpdate(_, sns) => sns
  case EphemeralUpdate(pod) => pod.ns
  case PodUpdate(pod, _, _) => pod.ns
  case OtherOp(ons) => ons
}

// ---------------------------------------------------------------------------
// Rule vocabulary (lines 233-239)
// ---------------------------------------------------------------------------

// "Each rule admits a requester whose request.userInfo.groups intersects
// admission.operatorGroups" (line 233).
predicate IsOperator(r: Request, cfg: AdmissionConfig)
{
  r.groups * cfg.operatorGroups != {}
}

// (b)'s pod names: `<rel>-api-*`, `<rel>-worker-*`, `<rel>-authentik-*`,
// `<rel>-postgres-*` (line 235), reused by (e) and (f) (lines 238, 239).
function GatedPrefixes(cfg: AdmissionConfig): set<string>
{
  {cfg.rel + "-api-", cfg.rel + "-worker-", cfg.rel + "-authentik-", cfg.rel + "-postgres-"}
}

predicate ProtectedPod(pod: PodRef, cfg: AdmissionConfig)
{
  pod.ns == cfg.releaseNs && exists p :: p in GatedPrefixes(cfg) && p <= pod.name
}

// (c)'s admitted controllers (line 236).
const PodControllers: set<string> :=
  {"replicaset-controller", "statefulset-controller", "job-controller", "daemon-set-controller"}

// (d)'s admitted controllers (line 237).
const WorkloadControllers: set<string> :=
  {"deployment-controller", "replicaset-controller", "cronjob-controller", "job-controller", "generic-garbage-collector"}

// The per-rule non-operator admissions: the kubelet for (a) (line 234), the
// controllers for (c) (line 236) and (d) (line 237). (b), (e), (f) have none.
predicate ExemptRequester(r: Request)
{
  match r.op
  case TokenCreate(_) => "system:nodes" in r.groups
  case PodCreate(_, _) => r.user.KubeSystemSA? && r.user.saName in PodControllers
  case WorkloadWrite(_, _, _, _, _) => r.user.KubeSystemSA? && r.user.saName in WorkloadControllers
  case _ => false
}

// Whether the bound policy matches the request: the namespaceSelector (line 230)
// and then each rule's match condition, in the spec's order (a)-(f).
predicate Matched(r: Request, cfg: AdmissionConfig)
{
  RequestNs(r.op) == cfg.releaseNs &&
  match r.op
  // (a) line 234
  case TokenCreate(sa) => sa in cfg.protectedAccounts
  // (b) line 235
  case PodConnect(_, pod) => ProtectedPod(pod, cfg)
  // (c) line 236
  case PodCreate(pod, saName) => SA(pod.ns, saName) in cfg.protectedAccounts
  // (d) line 237: "whose pod template (old or new) uses either of those service accounts"
  case WorkloadWrite(_, _, _, _, _) =>
    r.op.oldSa in cfg.protectedAccounts || r.op.newSa in cfg.protectedAccounts
  // line 243: "other than through the `scale` subresource"
  case ScaleUpdate(_, _) => false
  // (e) line 238
  case EphemeralUpdate(pod) => ProtectedPod(pod, cfg)
  // (f) line 239: only when an image differs
  case PodUpdate(pod, oldImages, newImages) => ProtectedPod(pod, cfg) && oldImages != newImages
  case OtherOp(_) => false
}

// The admission decision (failurePolicy Fail, validationActions [Deny], line 229:
// a matched request that the rule does not admit is denied).
predicate Admits(r: Request, cfg: AdmissionConfig)
{
  if !Matched(r, cfg) then true
  else
    match r.op
    // (a) operators, and the kubelet (line 234)
    case TokenCreate(_) => IsOperator(r, cfg) || ExemptRequester(r)
    // (b) operators only (line 235)
    case PodConnect(_, _) => IsOperator(r, cfg)
    // (c) operators or the four controllers, and in every case the name
    //     clause `<rel>-api-` (line 236)
    case PodCreate(pod, _) =>
      (IsOperator(r, cfg) || ExemptRequester(r)) && (cfg.rel + "-api-") <= pod.name
    // (d) operators or the five controllers (line 237)
    case WorkloadWrite(_, _, _, _, _) => IsOperator(r, cfg) || ExemptRequester(r)
    case ScaleUpdate(_, _) => true
    // (e) operators only (line 238)
    case EphemeralUpdate(_) => IsOperator(r, cfg)
    // (f) operators only (line 239)
    case PodUpdate(_, _, _) => IsOperator(r, cfg)
    case OtherOp(_) => true
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

lemma PrefixOfConcat(a: string, b: string)
  ensures a <= a + b
{
  assert (a + b)[..|a|] == a;
}

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------

// Citation: "Each rule admits a requester whose `request.userInfo.groups` intersects `admission.operatorGroups`, and denies everyone else; rule (c)'s name clause binds the operators group too." (§3.1 line 233)
lemma P1_OperatorsAdmitted(r: Request, cfg: AdmissionConfig)
  requires r.groups * cfg.operatorGroups != {}
  requires !(r.op.PodCreate? && SA(r.op.pod.ns, r.op.saName) in cfg.protectedAccounts
             && !((cfg.rel + "-api-") <= r.op.pod.name))
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
  assert cfg.rel + "-api-" in GatedPrefixes(cfg);
}

// Citation: "the api's ReplicaSet pods (`<rel>-api-<hash>-<suffix>`) already do (Verified assumption 96)" (§3.1 line 236)
lemma P9_ApiReplicaSetPodsAdmitted(cfg: AdmissionConfig, groups: set<string>, hash: string, suffix: string)
  ensures Admits(Request(KubeSystemSA("replicaset-controller"), groups,
                         PodCreate(PodRef(cfg.releaseNs, cfg.rel + "-api-" + hash + "-" + suffix), cfg.rel + "-api")), cfg)
{
  var p := cfg.rel + "-api-";
  var rest := hash + "-" + suffix;
  assert cfg.rel + "-api-" + hash + "-" + suffix == p + rest;
  PrefixOfConcat(p, rest);
}
