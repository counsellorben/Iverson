// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T11 S3
// Source: Iverson.Server/deploy/helm/iverson/templates/admission-policy.yaml variables and validations (43–126), with matchConstraints (35–42) and the binding (128–138)
// Code ref: 8ef849174092218f507041e82b8185facafb1937
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-21-csr-round12-platform-implementation-plan/admission-policy.validations.dfy

// ---------------------------------------------------------------------------------------------
// Shared vocabulary and request shape
// ---------------------------------------------------------------------------------------------

datatype Option<T> = None | Some(value: T)
newtype CodePoint = x: int | 0 <= x < 0x110000

datatype Container = Container(name: seq<CodePoint>, image: seq<CodePoint>)
// Each Option is CEL has(): name = metadata.name, podSa = spec.serviceAccountName (pod),
// initContainers = spec.initContainers (pod), tplSa = spec.template.spec.serviceAccountName
// (apps workloads, Job), cronTplSa = spec.jobTemplate.spec.template.spec.serviceAccountName (CronJob).
datatype Obj = Obj(name: Option<seq<CodePoint>>, podSa: Option<seq<CodePoint>>, containers: seq<Container>, initContainers: Option<seq<Container>>, tplSa: Option<seq<CodePoint>>, cronTplSa: Option<seq<CodePoint>>)
// sub is the value of variables.sub (request.subResource, '' when absent); object/oldObject None is CEL null.
datatype AReq = AReq(op: seq<CodePoint>, group: seq<CodePoint>, resource: seq<CodePoint>, sub: seq<CodePoint>, name: seq<CodePoint>, ns: seq<CodePoint>, username: seq<CodePoint>, groups: Option<seq<seq<CodePoint>>>, object: Option<Obj>, oldObject: Option<Obj>)
// Helm renders .Release.Name, .Release.Namespace and admission.operatorGroups into the CEL literals.
datatype Cfg = Cfg(rel: seq<CodePoint>, releaseNs: seq<CodePoint>, operatorGroups: seq<seq<CodePoint>>)

// r.object: `object` is a Dafny keyword, so `.object` cannot be written as a member access.
function ObjectOf(r: AReq): Option<Obj>
{
  match r
  case AReq(_, _, _, _, _, _, _, _, o, _) => o
}

datatype CelVal = CTrue | CFalse | CError
datatype SVal = SV(s: seq<CodePoint>) | SErr
datatype LVal = LV(l: seq<seq<CodePoint>>) | LErr

// ---------------------------------------------------------------------------------------------
// Library boundary constants (CEL string literals)
// ---------------------------------------------------------------------------------------------

const UpdateOp: seq<CodePoint> := [85, 80, 68, 65, 84, 69] // "UPDATE"
const CreateOp: seq<CodePoint> := [67, 82, 69, 65, 84, 69] // "CREATE"
const ConnectOp: seq<CodePoint> := [67, 79, 78, 78, 69, 67, 84] // "CONNECT"
const CoreGroup: seq<CodePoint> := [] // ""
const AppsGroup: seq<CodePoint> := [97, 112, 112, 115] // "apps"
const BatchGroup: seq<CodePoint> := [98, 97, 116, 99, 104] // "batch"
const PodsResource: seq<CodePoint> := [112, 111, 100, 115] // "pods"
const ServiceAccountsResource: seq<CodePoint> := [115, 101, 114, 118, 105, 99, 101, 97, 99, 99, 111, 117, 110, 116, 115] // "serviceaccounts"
const DeploymentsResource: seq<CodePoint> := [100, 101, 112, 108, 111, 121, 109, 101, 110, 116, 115] // "deployments"
const ReplicaSetsResource: seq<CodePoint> := [114, 101, 112, 108, 105, 99, 97, 115, 101, 116, 115] // "replicasets"
const StatefulSetsResource: seq<CodePoint> := [115, 116, 97, 116, 101, 102, 117, 108, 115, 101, 116, 115] // "statefulsets"
const DaemonSetsResource: seq<CodePoint> := [100, 97, 101, 109, 111, 110, 115, 101, 116, 115] // "daemonsets"
const JobsResource: seq<CodePoint> := [106, 111, 98, 115] // "jobs"
const CronJobsResource: seq<CodePoint> := [99, 114, 111, 110, 106, 111, 98, 115] // "cronjobs"
const TokenSub: seq<CodePoint> := [116, 111, 107, 101, 110] // "token"
const ScaleSub: seq<CodePoint> := [115, 99, 97, 108, 101] // "scale"
const ExecSub: seq<CodePoint> := [101, 120, 101, 99] // "exec"
const AttachSub: seq<CodePoint> := [97, 116, 116, 97, 99, 104] // "attach"
const PortforwardSub: seq<CodePoint> := [112, 111, 114, 116, 102, 111, 114, 119, 97, 114, 100] // "portforward"
const ProxySub: seq<CodePoint> := [112, 114, 111, 120, 121] // "proxy"
const ServicesResource: seq<CodePoint> := [115, 101, 114, 118, 105, 99, 101, 115] // "services"
const Colon: CodePoint := 58 // ':'
const EphemeralSub: seq<CodePoint> := [101, 112, 104, 101, 109, 101, 114, 97, 108, 99, 111, 110, 116, 97, 105, 110, 101, 114, 115] // "ephemeralcontainers"
const EmptyStr: seq<CodePoint> := [] // ""
const DefaultSa: seq<CodePoint> := [100, 101, 102, 97, 117, 108, 116] // "default"
const SystemNodes: seq<CodePoint> := [115, 121, 115, 116, 101, 109, 58, 110, 111, 100, 101, 115] // "system:nodes"
const ReplicaSetController: seq<CodePoint> := [115, 121, 115, 116, 101, 109, 58, 115, 101, 114, 118, 105, 99, 101, 97, 99, 99, 111, 117, 110, 116, 58, 107, 117, 98, 101, 45, 115, 121, 115, 116, 101, 109, 58, 114, 101, 112, 108, 105, 99, 97, 115, 101, 116, 45, 99, 111, 110, 116, 114, 111, 108, 108, 101, 114] // "system:serviceaccount:kube-system:replicaset-controller"
const StatefulSetController: seq<CodePoint> := [115, 121, 115, 116, 101, 109, 58, 115, 101, 114, 118, 105, 99, 101, 97, 99, 99, 111, 117, 110, 116, 58, 107, 117, 98, 101, 45, 115, 121, 115, 116, 101, 109, 58, 115, 116, 97, 116, 101, 102, 117, 108, 115, 101, 116, 45, 99, 111, 110, 116, 114, 111, 108, 108, 101, 114] // "system:serviceaccount:kube-system:statefulset-controller"
const JobController: seq<CodePoint> := [115, 121, 115, 116, 101, 109, 58, 115, 101, 114, 118, 105, 99, 101, 97, 99, 99, 111, 117, 110, 116, 58, 107, 117, 98, 101, 45, 115, 121, 115, 116, 101, 109, 58, 106, 111, 98, 45, 99, 111, 110, 116, 114, 111, 108, 108, 101, 114] // "system:serviceaccount:kube-system:job-controller"
const DaemonSetController: seq<CodePoint> := [115, 121, 115, 116, 101, 109, 58, 115, 101, 114, 118, 105, 99, 101, 97, 99, 99, 111, 117, 110, 116, 58, 107, 117, 98, 101, 45, 115, 121, 115, 116, 101, 109, 58, 100, 97, 101, 109, 111, 110, 45, 115, 101, 116, 45, 99, 111, 110, 116, 114, 111, 108, 108, 101, 114] // "system:serviceaccount:kube-system:daemon-set-controller"
const DeploymentController: seq<CodePoint> := [115, 121, 115, 116, 101, 109, 58, 115, 101, 114, 118, 105, 99, 101, 97, 99, 99, 111, 117, 110, 116, 58, 107, 117, 98, 101, 45, 115, 121, 115, 116, 101, 109, 58, 100, 101, 112, 108, 111, 121, 109, 101, 110, 116, 45, 99, 111, 110, 116, 114, 111, 108, 108, 101, 114] // "system:serviceaccount:kube-system:deployment-controller"
const CronJobController: seq<CodePoint> := [115, 121, 115, 116, 101, 109, 58, 115, 101, 114, 118, 105, 99, 101, 97, 99, 99, 111, 117, 110, 116, 58, 107, 117, 98, 101, 45, 115, 121, 115, 116, 101, 109, 58, 99, 114, 111, 110, 106, 111, 98, 45, 99, 111, 110, 116, 114, 111, 108, 108, 101, 114] // "system:serviceaccount:kube-system:cronjob-controller"
const GcController: seq<CodePoint> := [115, 121, 115, 116, 101, 109, 58, 115, 101, 114, 118, 105, 99, 101, 97, 99, 99, 111, 117, 110, 116, 58, 107, 117, 98, 101, 45, 115, 121, 115, 116, 101, 109, 58, 103, 101, 110, 101, 114, 105, 99, 45, 103, 97, 114, 98, 97, 103, 101, 45, 99, 111, 108, 108, 101, 99, 116, 111, 114] // "system:serviceaccount:kube-system:generic-garbage-collector"
const Dash: seq<CodePoint> := [45] // "-"
const EqSign: seq<CodePoint> := [61] // "="
const ApiSuffix: seq<CodePoint> := [45, 97, 112, 105] // "-api"
const AdminAutomationSuffix: seq<CodePoint> := [45, 97, 100, 109, 105, 110, 45, 97, 117, 116, 111, 109, 97, 116, 105, 111, 110] // "-admin-automation"
const ApiTail: seq<CodePoint> := [45, 97, 112, 105, 45] // "-api-"
const WorkerTail: seq<CodePoint> := [45, 119, 111, 114, 107, 101, 114, 45] // "-worker-"
const AuthentikTail: seq<CodePoint> := [45, 97, 117, 116, 104, 101, 110, 116, 105, 107, 45] // "-authentik-"
const PostgresTail: seq<CodePoint> := [45, 112, 111, 115, 116, 103, 114, 101, 115, 45] // "-postgres-"
const ContainerName: seq<CodePoint> := [97, 112, 105] // "api" (PodCreateBy's one container)

// ---------------------------------------------------------------------------------------------
// CEL (cel-go) semantics: error-absorbing commutative && / ||, !, ?:, in, startsWith, list !=
// ---------------------------------------------------------------------------------------------

function B(b: bool): CelVal { if b then CTrue else CFalse }

function And(a: CelVal, b: CelVal): CelVal
{
  if a == CFalse || b == CFalse then CFalse
  else if a == CTrue && b == CTrue then CTrue
  else CError
}

function Or(a: CelVal, b: CelVal): CelVal
{
  if a == CTrue || b == CTrue then CTrue
  else if a == CFalse && b == CFalse then CFalse
  else CError
}

function Not(a: CelVal): CelVal
{
  match a
  case CTrue => CFalse
  case CFalse => CTrue
  case CError => CError
}

// list.size() == n  (list error: error)
function SizeEq(l: LVal, n: int): CelVal
{
  match l
  case LV(xs) => B(|xs| == n)
  case LErr => CError
}

// list[i]  (list error or index out of range: error)
function IndexS(l: LVal, i: int): SVal
{
  match l
  case LV(xs) => if 0 <= i < |xs| then SV(xs[i]) else SErr
  case LErr => SErr
}

// cel-go ext.Strings s.split(sep) == Go strings.Split(s, sep) for a one-code-point separator:
// every occurrence splits, so "" gives [""] and ":" gives ["", ""].
function SplitOn(s: seq<CodePoint>, sep: CodePoint): seq<seq<CodePoint>>
  ensures |SplitOn(s, sep)| >= 1
  decreases |s|
{
  if |s| == 0 then [[]]
  else if s[0] == sep then [[]] + SplitOn(s[1..], sep)
  else
    var rest := SplitOn(s[1..], sep);
    [[s[0]] + rest[0]] + rest[1..]
}

// list.exists(p, v.startsWith(p)) over a non-empty literal list (v error: every element errors, so error)
function ExistsStartsWithS(v: SVal, l: seq<seq<CodePoint>>): CelVal
{
  match v
  case SV(s) => B(exists p :: p in l && StartsWith(s, p))
  case SErr => if |l| == 0 then CFalse else CError
}

// cond ? a : b with a string result
function TernS(cond: CelVal, a: SVal, b: SVal): SVal
{
  match cond
  case CTrue => a
  case CFalse => b
  case CError => SErr
}

// x in list (list of string literals)
function InListS(x: SVal, l: seq<seq<CodePoint>>): CelVal
{
  match x
  case SV(s) => B(s in l)
  case SErr => CError
}

// list != list
function NeL(a: LVal, b: LVal): CelVal
{
  if a.LV? && b.LV? then B(a.l != b.l) else CError
}

predicate StartsWith(s: seq<CodePoint>, p: seq<CodePoint>)
{
  |p| <= |s| && s[..|p|] == p
}

// ---------------------------------------------------------------------------------------------
// Transliteration: matchConstraints (39–46) and the binding (146–156)
// ---------------------------------------------------------------------------------------------

// admission-policy.yaml:40–46 resourceRules (apiVersions ["v1"] not modelled; see notes)
predicate MatchesRule(r: AReq)
{
  // :41 serviceaccounts/token CREATE
  (r.group == CoreGroup && r.op == CreateOp && r.resource == ServiceAccountsResource && r.sub == TokenSub)
  // :42 pods/exec, pods/attach, pods/portforward, pods/proxy, services/proxy CONNECT
  || (r.group == CoreGroup && r.op == ConnectOp
      && ((r.resource == PodsResource && r.sub in [ExecSub, AttachSub, PortforwardSub, ProxySub])
          || (r.resource == ServicesResource && r.sub == ProxySub)))
  // :43 pods CREATE, UPDATE
  || (r.group == CoreGroup && r.op in [CreateOp, UpdateOp] && r.resource == PodsResource && r.sub == EmptyStr)
  // :44 pods/ephemeralcontainers UPDATE
  || (r.group == CoreGroup && r.op == UpdateOp && r.resource == PodsResource && r.sub == EphemeralSub)
  // :45 apps workloads CREATE, UPDATE
  || (r.group == AppsGroup && r.op in [CreateOp, UpdateOp]
      && r.resource in [DeploymentsResource, ReplicaSetsResource, StatefulSetsResource, DaemonSetsResource] && r.sub == EmptyStr)
  // :46 batch workloads CREATE, UPDATE
  || (r.group == BatchGroup && r.op in [CreateOp, UpdateOp]
      && r.resource in [JobsResource, CronJobsResource] && r.sub == EmptyStr)
}

// :153–156 namespaceSelector kubernetes.io/metadata.name == .Release.Namespace
predicate MatchesPolicy(r: AReq, c: Cfg)
{
  MatchesRule(r) && r.ns == c.releaseNs
}

// ---------------------------------------------------------------------------------------------
// Transliteration: variables (47–100)
// ---------------------------------------------------------------------------------------------

// :48–49 protected
function VProtected(c: Cfg): seq<seq<CodePoint>>
{
  [c.rel + ApiSuffix, c.rel + AdminAutomationSuffix]
}

// :50–51 operator
function VOperator(r: AReq, c: Cfg): CelVal
{
  And(B(r.groups.Some?),
      if r.groups.Some? then B(exists g :: g in r.groups.value && g in c.operatorGroups) else CError)
}

// :52–53 node
function VNode(r: AReq): CelVal
{
  And(B(r.groups.Some?),
      if r.groups.Some? then B(SystemNodes in r.groups.value) else CError)
}

// :54–55 res
function VRes(r: AReq): seq<CodePoint> { r.resource }

// :56–57 sub  (has(request.subResource) ? request.subResource : '' — r.sub already holds this value)
function VSub(r: AReq): seq<CodePoint> { r.sub }

// :31 $connectPrefixes (connectPod's and proxyTarget's prefix list)
function ConnectPrefixes(c: Cfg): seq<seq<CodePoint>>
{
  [c.rel + ApiTail, c.rel + WorkerTail, c.rel + AuthentikTail, c.rel + PostgresTail]
}

// :59–60 connectPod
function VConnectPod(r: AReq, c: Cfg): CelVal
{
  B(exists p :: p in ConnectPrefixes(c) && StartsWith(r.name, p))
}

// :63–66 proxyName
function VProxyName(r: AReq): SVal
{
  TernS(Or(B(VRes(r) != PodsResource), B(VSub(r) != ProxySub)),
        SV(EmptyStr),
        TernS(SizeEq(LV(SplitOn(r.name, Colon)), 3),
              IndexS(LV(SplitOn(r.name, Colon)), 1),
              IndexS(LV(SplitOn(r.name, Colon)), 0)))
}

// :69–73 proxyTarget
function VProxyTarget(r: AReq, c: Cfg): CelVal
{
  And(B(VSub(r) == ProxySub),
      Or(B(VRes(r) == ServicesResource),
         And(B(VRes(r) == PodsResource), ExistsStartsWithS(VProxyName(r), ConnectPrefixes(c)))))
}

// has(o.spec.serviceAccountName) ? o.spec.serviceAccountName : 'default'   (o null: error)
function PodSaField(o: Option<Obj>): SVal
{
  if o.None? then SErr
  else if o.value.podSa.Some? then SV(o.value.podSa.value) else SV(DefaultSa)
}

// :75–79 podSa
function VPodSa(r: AReq): SVal
{
  TernS(And(And(And(B(VRes(r) == PodsResource), B(VSub(r) == EmptyStr)), B(r.op == CreateOp)), B(ObjectOf(r).Some?)),
        PodSaField(ObjectOf(r)),
        SV(EmptyStr))
}

const WorkloadFive: seq<seq<CodePoint>> := [DeploymentsResource, ReplicaSetsResource, StatefulSetsResource, DaemonSetsResource, JobsResource]

// has(o.spec.template.spec.serviceAccountName) ? ... : 'default'
function TplSaField(o: Option<Obj>): SVal
{
  if o.None? then SErr
  else if o.value.tplSa.Some? then SV(o.value.tplSa.value) else SV(DefaultSa)
}

// has(o.spec.jobTemplate.spec.template.spec.serviceAccountName) ? ... : 'default'
function CronTplSaField(o: Option<Obj>): SVal
{
  if o.None? then SErr
  else if o.value.cronTplSa.Some? then SV(o.value.cronTplSa.value) else SV(DefaultSa)
}

// :81–87 newTplSa
function VNewTplSa(r: AReq): SVal
{
  TernS(And(B(VRes(r) in WorkloadFive), B(ObjectOf(r).Some?)),
        TplSaField(ObjectOf(r)),
        TernS(And(B(VRes(r) == CronJobsResource), B(ObjectOf(r).Some?)),
              CronTplSaField(ObjectOf(r)),
              SV(EmptyStr)))
}

// :88–94 oldTplSa
function VOldTplSa(r: AReq): SVal
{
  TernS(And(B(VRes(r) in WorkloadFive), B(r.oldObject.Some?)),
        TplSaField(r.oldObject),
        TernS(And(B(VRes(r) == CronJobsResource), B(r.oldObject.Some?)),
              CronTplSaField(r.oldObject),
              SV(EmptyStr)))
}

// cs.map(c, c.name + '=' + c.image)
function MapStrs(cs: seq<Container>): seq<seq<CodePoint>>
{
  seq(|cs|, i requires 0 <= i < |cs| => cs[i].name + EqSign + cs[i].image)
}

// o.spec.containers.map(...)   (o null: error)
function CMap(o: Option<Obj>): LVal
{
  if o.None? then LErr else LV(MapStrs(o.value.containers))
}

// (has(o.spec.initContainers) ? o.spec.initContainers.map(...) : [])   (o null: error)
function InitMap(o: Option<Obj>): LVal
{
  if o.None? then LErr
  else if o.value.initContainers.Some? then LV(MapStrs(o.value.initContainers.value)) else LV([])
}

// :95–100 imagesChanged
function VImagesChanged(r: AReq): CelVal
{
  And(And(And(And(And(B(VRes(r) == PodsResource), B(VSub(r) == EmptyStr)), B(r.op == UpdateOp)),
                  B(ObjectOf(r).Some?)), B(r.oldObject.Some?)),
      Or(NeL(CMap(ObjectOf(r)), CMap(r.oldObject)),
         NeL(InitMap(ObjectOf(r)), InitMap(r.oldObject))))
}

// ---------------------------------------------------------------------------------------------
// Transliteration: validations (101–144). GuardX is the negated left side of each rule's ||.
// ---------------------------------------------------------------------------------------------

const PodControllers: seq<seq<CodePoint>> := [ReplicaSetController, StatefulSetController, JobController, DaemonSetController]
const WorkloadControllers: seq<seq<CodePoint>> := [DeploymentController, ReplicaSetController, CronJobController, JobController, GcController]

// :106
function GuardA(r: AReq, c: Cfg): CelVal
{
  And(And(B(VRes(r) == ServiceAccountsResource), B(VSub(r) == TokenSub)), B(r.name in VProtected(c)))
}
// :105–108 (a)
function ValA(r: AReq, c: Cfg): CelVal
{
  Or(Or(Not(GuardA(r, c)), VOperator(r, c)), VNode(r))
}

// :111–112
function GuardB(r: AReq, c: Cfg): CelVal
{
  Or(And(And(B(VRes(r) == PodsResource), B(VSub(r) in [ExecSub, AttachSub, PortforwardSub])), VConnectPod(r, c)),
     VProxyTarget(r, c))
}
// :110–114 (b)
function ValB(r: AReq, c: Cfg): CelVal
{
  Or(Not(GuardB(r, c)), VOperator(r, c))
}

// :117
function GuardC(r: AReq, c: Cfg): CelVal
{
  InListS(VPodSa(r), VProtected(c))
}
// object.metadata.name.startsWith(p)   (object null or name absent: error)
function ObjNameStartsWith(o: Option<Obj>, p: seq<CodePoint>): CelVal
{
  if o.None? then CError
  else if o.value.name.None? then CError
  else B(StartsWith(o.value.name.value, p))
}
// :116–124 (c)
function ValC(r: AReq, c: Cfg): CelVal
{
  Or(Not(GuardC(r, c)),
     And(Or(VOperator(r, c), B(r.username in PodControllers)),
         ObjNameStartsWith(ObjectOf(r), c.rel + ApiTail)))
}

// :127
function GuardD(r: AReq, c: Cfg): CelVal
{
  Or(InListS(VNewTplSa(r), VProtected(c)), InListS(VOldTplSa(r), VProtected(c)))
}
// :126–135 (d)
function ValD(r: AReq, c: Cfg): CelVal
{
  Or(Or(Not(GuardD(r, c)), VOperator(r, c)), B(r.username in WorkloadControllers))
}

// :138
function GuardE(r: AReq, c: Cfg): CelVal
{
  And(And(B(VRes(r) == PodsResource), B(VSub(r) == EphemeralSub)), VConnectPod(r, c))
}
// :137–140 (e)
function ValE(r: AReq, c: Cfg): CelVal
{
  Or(Not(GuardE(r, c)), VOperator(r, c))
}

// :143
function GuardF(r: AReq, c: Cfg): CelVal
{
  And(VConnectPod(r, c), VImagesChanged(r))
}
// :142–144 (f)
function ValF(r: AReq, c: Cfg): CelVal
{
  Or(Not(GuardF(r, c)), VOperator(r, c))
}

function Validation(i: int, r: AReq, c: Cfg): CelVal
{
  if i == 0 then ValA(r, c)
  else if i == 1 then ValB(r, c)
  else if i == 2 then ValC(r, c)
  else if i == 3 then ValD(r, c)
  else if i == 4 then ValE(r, c)
  else ValF(r, c)
}

// :38 failurePolicy: Fail and :152 validationActions: [Deny]: an error or a false validation denies.
predicate Admits(r: AReq, c: Cfg)
{
  !MatchesPolicy(r, c) || forall i :: 0 <= i < 6 ==> Validation(i, r, c) == CTrue
}

// ---------------------------------------------------------------------------------------------
// Spec vocabulary
// ---------------------------------------------------------------------------------------------

predicate IsOperatorReq(r: AReq, c: Cfg)
{
  r.groups.Some? && exists g :: g in r.groups.value && g in c.operatorGroups
}

predicate IsPodCreate(r: AReq)
{
  r.op == CreateOp && r.group == CoreGroup && r.resource == PodsResource && r.sub == EmptyStr
}

predicate IsTokenCreate(r: AReq)
{
  r.op == CreateOp && r.group == CoreGroup && r.resource == ServiceAccountsResource && r.sub == TokenSub
}

predicate IsWorkloadWrite(r: AReq)
{
  r.op in [CreateOp, UpdateOp] && r.sub == EmptyStr
  && ((r.group == AppsGroup && r.resource in [DeploymentsResource, ReplicaSetsResource, StatefulSetsResource, DaemonSetsResource])
      || (r.group == BatchGroup && r.resource in [JobsResource, CronJobsResource]))
}

function SValOr(v: SVal): seq<CodePoint>
{
  match v
  case SV(s) => s
  case SErr => EmptyStr
}

function PodSaOf(r: AReq): seq<CodePoint> { SValOr(VPodSa(r)) }
function NewTplSa(r: AReq): seq<CodePoint> { SValOr(VNewTplSa(r)) }
function OldTplSa(r: AReq): seq<CodePoint> { SValOr(VOldTplSa(r)) }

function Protected(c: Cfg): seq<seq<CodePoint>>
{
  [c.rel + ApiSuffix, c.rel + AdminAutomationSuffix]
}

function GatedPrefixes(c: Cfg): seq<seq<CodePoint>>
{
  [c.rel + ApiTail, c.rel + WorkerTail, c.rel + AuthentikTail, c.rel + PostgresTail]
}

function ApiPrefix(c: Cfg): seq<CodePoint> { c.rel + ApiTail }

function ObjName(r: AReq): Option<seq<CodePoint>>
{
  if ObjectOf(r).Some? then ObjectOf(r).value.name else None
}

predicate StartsWithOpt(o: Option<seq<CodePoint>>, p: seq<CodePoint>)
{
  match o
  case None => false
  case Some(s) => StartsWith(s, p)
}

predicate RuleMatches(r: AReq, c: Cfg)
{
  MatchesPolicy(r, c)
  && (GuardA(r, c) == CTrue || GuardB(r, c) == CTrue || GuardC(r, c) == CTrue
      || GuardD(r, c) == CTrue || GuardE(r, c) == CTrue || GuardF(r, c) == CTrue)
}

predicate ExemptRequester(r: AReq)
{
  (IsTokenCreate(r) && r.groups.Some? && SystemNodes in r.groups.value)
  || (IsPodCreate(r) && r.username in PodControllers)
  || (IsWorkloadWrite(r) && r.username in WorkloadControllers)
}

predicate SameImages(o: Obj, p: Obj)
{
  MapStrs(o.containers) == MapStrs(p.containers)
  && (if o.initContainers.Some? then MapStrs(o.initContainers.value) else [])
     == (if p.initContainers.Some? then MapStrs(p.initContainers.value) else [])
}

function PodCreateBy(user: seq<CodePoint>, groups: Option<seq<seq<CodePoint>>>, ns: seq<CodePoint>, name: seq<CodePoint>, sa: seq<CodePoint>, image: seq<CodePoint>): AReq
{
  AReq(CreateOp, CoreGroup, PodsResource, EmptyStr, EmptyStr, ns, user, groups,
       Some(Obj(Some(name), Some(sa), [Container(ContainerName, image)], None, None, None)), None)
}

// ---------------------------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------------------------

lemma AdmitsIff(r: AReq, c: Cfg)
  ensures Admits(r, c) <==> (!MatchesPolicy(r, c)
    || (ValA(r, c) == CTrue && ValB(r, c) == CTrue && ValC(r, c) == CTrue
        && ValD(r, c) == CTrue && ValE(r, c) == CTrue && ValF(r, c) == CTrue))
{
  assert Validation(0, r, c) == ValA(r, c);
  assert Validation(1, r, c) == ValB(r, c);
  assert Validation(2, r, c) == ValC(r, c);
  assert Validation(3, r, c) == ValD(r, c);
  assert Validation(4, r, c) == ValE(r, c);
  assert Validation(5, r, c) == ValF(r, c);
}

lemma OperatorIff(r: AReq, c: Cfg)
  ensures VOperator(r, c) == B(IsOperatorReq(r, c))
{
}

lemma PodSaNoError(r: AReq)
  ensures VPodSa(r) == SV(PodSaOf(r))
  ensures PodSaOf(r) != EmptyStr ==> IsPodCreate(r) || (r.resource == PodsResource && r.sub == EmptyStr && r.op == CreateOp)
{
}

lemma TplSaNoError(r: AReq)
  ensures VNewTplSa(r) == SV(NewTplSa(r))
  ensures VOldTplSa(r) == SV(OldTplSa(r))
  ensures NewTplSa(r) != EmptyStr ==> r.resource in WorkloadFive || r.resource == CronJobsResource
  ensures OldTplSa(r) != EmptyStr ==> r.resource in WorkloadFive || r.resource == CronJobsResource
{
}

lemma EmptyNotProtected(c: Cfg)
  ensures EmptyStr !in VProtected(c)
  ensures VProtected(c) == Protected(c)
{
}

lemma ImagesChangedNoError(r: AReq)
  ensures VImagesChanged(r) != CError
{
}

// A matched request whose resource is a workload is a workload write.
lemma MatchedWorkload(r: AReq, c: Cfg)
  requires MatchesPolicy(r, c)
  requires r.resource in WorkloadFive || r.resource == CronJobsResource
  ensures IsWorkloadWrite(r)
{
}

lemma MatchedToken(r: AReq, c: Cfg)
  requires MatchesPolicy(r, c)
  requires r.resource == ServiceAccountsResource
  ensures IsTokenCreate(r)
{
}

lemma PrefixStartsWith(a: seq<CodePoint>, b: seq<CodePoint>)
  ensures StartsWith(a + b, a)
{
  assert (a + b)[..|a|] == a;
}

// ---------------------------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------------------------

// Citation: design AdmissionPolicy.P1_OperatorsAdmitted: "Each rule admits a requester whose `request.userInfo.groups` intersects `admission.operatorGroups`, and denies everyone else; rule (c)'s name clause binds the operators group too." (§3.1 line 233)
lemma P1_OperatorsAdmitted(r: AReq, c: Cfg)
  requires IsOperatorReq(r, c)
  requires !(IsPodCreate(r) && PodSaOf(r) in Protected(c) && !StartsWithOpt(ObjName(r), ApiPrefix(c)))
  ensures Admits(r, c)
{
  AdmitsIff(r, c);
  OperatorIff(r, c);
  PodSaNoError(r);
  EmptyNotProtected(c);
  assert VOperator(r, c) == CTrue;
  if !MatchesPolicy(r, c) {
  } else if PodSaOf(r) in Protected(c) {
    assert IsPodCreate(r);
    assert ObjectOf(r).Some?;
    assert StartsWithOpt(ObjName(r), ApiPrefix(c));
    assert ObjNameStartsWith(ObjectOf(r), c.rel + ApiTail) == CTrue;
    assert ValC(r, c) == CTrue;
  } else {
    assert GuardC(r, c) == CFalse;
  }
}

// Citation: design AdmissionPolicy.P2_ClusterAdminDenied: "Cluster-admin is denied too, by design (Verified assumption 37)" (§3.1 line 233)
lemma P2_ClusterAdminDenied(r: AReq, c: Cfg)
  requires !IsOperatorReq(r, c) && RuleMatches(r, c) && !ExemptRequester(r)
  ensures !Admits(r, c)
{
  AdmitsIff(r, c);
  OperatorIff(r, c);
  PodSaNoError(r);
  TplSaNoError(r);
  EmptyNotProtected(c);
  ImagesChangedNoError(r);
  assert VOperator(r, c) == CFalse;
  if GuardA(r, c) == CTrue {
    MatchedToken(r, c);
    assert VNode(r) == CFalse;
    assert ValA(r, c) == CFalse;
  } else if GuardB(r, c) == CTrue {
    assert ValB(r, c) == CFalse;
  } else if GuardC(r, c) == CTrue {
    assert PodSaOf(r) in VProtected(c);
    assert PodSaOf(r) != EmptyStr;
    assert IsPodCreate(r);
    assert r.username !in PodControllers;
    assert ValC(r, c) == CFalse;
  } else if GuardD(r, c) == CTrue {
    assert NewTplSa(r) in VProtected(c) || OldTplSa(r) in VProtected(c);
    assert NewTplSa(r) != EmptyStr || OldTplSa(r) != EmptyStr;
    MatchedWorkload(r, c);
    assert r.username !in WorkloadControllers;
    assert ValD(r, c) == CFalse;
  } else if GuardE(r, c) == CTrue {
    assert ValE(r, c) == CFalse;
  } else {
    assert GuardF(r, c) == CTrue;
    assert ValF(r, c) == CFalse;
  }
}

// Citation: design AdmissionPolicy.P3_MetadataOnlyPodUpdateAdmitted: "Metadata-only pod updates are not matched (Verified assumption 77)." (§3.1 line 239)
lemma P3_MetadataOnlyPodUpdateAdmitted(r: AReq, c: Cfg)
  requires r.op == UpdateOp && r.group == CoreGroup && r.resource == PodsResource && r.sub == EmptyStr
  requires ObjectOf(r).Some? && r.oldObject.Some? && SameImages(ObjectOf(r).value, r.oldObject.value)
  ensures Admits(r, c)
{
  AdmitsIff(r, c);
  EmptyNotProtected(c);
  assert VImagesChanged(r) == CFalse;
  assert GuardF(r, c) == CFalse;
  assert VPodSa(r) == SV(EmptyStr);
  assert GuardC(r, c) == CFalse;
  assert VNewTplSa(r) == SV(EmptyStr) && VOldTplSa(r) == SV(EmptyStr);
  assert GuardD(r, c) == CFalse;
  assert GuardA(r, c) == CFalse;
  assert GuardB(r, c) == CFalse;
  assert GuardE(r, c) == CFalse;
}

// Citation: design AdmissionPolicy.P4_KubeletMintsTokens: "The kubelet (`system:nodes`) is also admitted, so projected tokens keep mounting." (§3.1 line 234)
lemma P4_KubeletMintsTokens(r: AReq, c: Cfg)
  requires r.op == CreateOp && r.group == CoreGroup && r.resource == ServiceAccountsResource && r.sub == TokenSub
  requires r.groups.Some? && SystemNodes in r.groups.value
  ensures Admits(r, c)
{
  AdmitsIff(r, c);
  EmptyNotProtected(c);
  assert VNode(r) == CTrue;
  assert ValA(r, c) == CTrue;
  assert GuardB(r, c) == CFalse;
  assert VPodSa(r) == SV(EmptyStr);
  assert GuardC(r, c) == CFalse;
  assert VNewTplSa(r) == SV(EmptyStr) && VOldTplSa(r) == SV(EmptyStr);
  assert GuardD(r, c) == CFalse;
  assert GuardE(r, c) == CFalse;
  assert VImagesChanged(r) == CFalse;
  assert GuardF(r, c) == CFalse;
}

// Citation: design AdmissionPolicy.P5_ScaleAdmitted: "creates or updates (other than through the `scale` subresource) a workload whose pod template uses either account" (§3.1 line 243)
lemma P5_ScaleAdmitted(r: AReq, c: Cfg)
  requires r.sub == ScaleSub
  ensures Admits(r, c)
{
  assert !MatchesRule(r);
}

// Citation: design AdmissionPolicy.P6_NoProtectedPodOrTemplateForNonOperators: "Without (d), anyone who can create a Job could run a pod as `<rel>-admin-automation` through the job controller, which (c) admits." (§3.1 line 237)
lemma P6_NoProtectedPodOrTemplateForNonOperators(r: AReq, c: Cfg)
  requires r.ns == c.releaseNs && !IsOperatorReq(r, c) && !ExemptRequester(r)
  requires (IsPodCreate(r) && PodSaOf(r) in Protected(c))
        || (IsWorkloadWrite(r) && (NewTplSa(r) in Protected(c) || OldTplSa(r) in Protected(c)))
  ensures !Admits(r, c)
{
  AdmitsIff(r, c);
  OperatorIff(r, c);
  PodSaNoError(r);
  TplSaNoError(r);
  EmptyNotProtected(c);
  assert VOperator(r, c) == CFalse;
  if IsPodCreate(r) && PodSaOf(r) in Protected(c) {
    assert MatchesRule(r);
    assert GuardC(r, c) == CTrue;
    assert r.username !in PodControllers;
    assert ValC(r, c) == CFalse;
  } else {
    assert MatchesRule(r);
    assert GuardD(r, c) == CTrue;
    assert r.username !in WorkloadControllers;
    assert ValD(r, c) == CFalse;
  }
}

// Citation: design AdmissionPolicy.P7_MisnamedProtectedPodRefusedForAll: "Whoever creates it, the operators group and those controllers included, a pod with either account is admitted only when its name starts with `<rel>-api-`" (§3.1 line 236)
lemma P7_MisnamedProtectedPodRefusedForAll(r: AReq, c: Cfg)
  requires r.ns == c.releaseNs && IsPodCreate(r) && PodSaOf(r) in Protected(c)
  requires !StartsWithOpt(ObjName(r), ApiPrefix(c))
  ensures !Admits(r, c)
{
  AdmitsIff(r, c);
  PodSaNoError(r);
  EmptyNotProtected(c);
  assert MatchesRule(r);
  assert ObjectOf(r).Some?;
  assert GuardC(r, c) == CTrue;
  assert ObjNameStartsWith(ObjectOf(r), c.rel + ApiTail) != CTrue;
  assert ValC(r, c) != CTrue;
}

// Citation: design AdmissionPolicy.P8_AdmittedProtectedPodIsGated: "so (b), (e) and (f) match every pod that holds a protected token" (§3.1 line 236)
lemma P8_AdmittedProtectedPodIsGated(r: AReq, c: Cfg)
  requires r.ns == c.releaseNs && IsPodCreate(r) && PodSaOf(r) in Protected(c) && Admits(r, c)
  ensures ObjName(r).Some? && exists p :: p in GatedPrefixes(c) && StartsWithOpt(ObjName(r), p)
{
  AdmitsIff(r, c);
  PodSaNoError(r);
  EmptyNotProtected(c);
  assert MatchesRule(r);
  assert ObjectOf(r).Some?;
  assert GuardC(r, c) == CTrue;
  assert ValC(r, c) == CTrue;
  assert ObjNameStartsWith(ObjectOf(r), c.rel + ApiTail) == CTrue;
  assert ObjName(r).Some?;
  assert StartsWithOpt(ObjName(r), ApiPrefix(c));
  assert ApiPrefix(c) in GatedPrefixes(c);
}

// Citation: design AdmissionPolicy.P9_ApiReplicaSetPodsAdmitted: "the api's ReplicaSet pods (`<rel>-api-<hash>-<suffix>`) already do (Verified assumption 96)" (§3.1 line 236)
lemma P9_ApiReplicaSetPodsAdmitted(c: Cfg, groups: Option<seq<seq<CodePoint>>>, hash: seq<CodePoint>, suffix: seq<CodePoint>, image: seq<CodePoint>)
  ensures Admits(PodCreateBy(ReplicaSetController, groups, c.releaseNs, ApiPrefix(c) + hash + Dash + suffix, c.rel + ApiSuffix, image), c)
{
  var name := ApiPrefix(c) + hash + Dash + suffix;
  var r := PodCreateBy(ReplicaSetController, groups, c.releaseNs, name, c.rel + ApiSuffix, image);
  AdmitsIff(r, c);
  EmptyNotProtected(c);
  assert name == ApiPrefix(c) + (hash + Dash + suffix);
  PrefixStartsWith(ApiPrefix(c), hash + Dash + suffix);
  assert ValA(r, c) == CTrue;
  // request.name is '' (generateName pod), so connectPod is false
  assert forall p :: p in ConnectPrefixes(c) ==> |p| > 0;
  assert VConnectPod(r, c) == CFalse;
  assert ValB(r, c) == CTrue;
  assert VPodSa(r) == SV(c.rel + ApiSuffix);
  assert r.username in PodControllers;
  assert ObjNameStartsWith(ObjectOf(r), c.rel + ApiTail) == CTrue;
  assert ValC(r, c) == CTrue;
  assert VNewTplSa(r) == SV(EmptyStr) && VOldTplSa(r) == SV(EmptyStr);
  assert ValD(r, c) == CTrue;
  assert ValE(r, c) == CTrue;
  assert VImagesChanged(r) == CFalse;
  assert ValF(r, c) == CTrue;
}
