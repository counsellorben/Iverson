// Spec: /home/ben/repositories/Iverson/docs/specs/2026-10-26-csr-round12-supply-chain-design.md @ f6f5db2cbea0425d938739f73a8d6e9147479682
// Source: §3 Admission-time verification (and Goals line 27, Choices made while writing item 4), lines 69-128
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-26-csr-round12-supply-chain-design/AdmissionDecision.dfy
// Round: 1

datatype Option<T> = None | Some(value: T)
datatype Image = Image(repo: string, digest: Option<string>)
datatype Attestation = Attestation(identityOk: bool, spdx: bool, subject: string)
datatype Pod = Pod(containers: seq<Image>, initContainers: seq<Image>)

// Spec line 69-: the four first-party images, one ClusterImagePolicy each
const FirstParty: set<string> := {"ghcr.io/counsellorben/iverson-api", "ghcr.io/counsellorben/iverson-admin-ui", "ghcr.io/counsellorben/iverson-ollama-model", "ghcr.io/counsellorben/iverson-tei-model"}

// Spec line 98: glob "<policyRepo>*"
predicate Matches(policyRepo: string, repo: string)
{
  |policyRepo| <= |repo| && repo[..|policyRepo|] == policyRepo
}

// Spec line 117: a tag is rewritten to tag@digest via the registry; a digest ref stays
function DigestOf(img: Image, resolve: Image -> Option<string>): Option<string>
{
  match img.digest
  case Some(d) => Some(d)
  case None => resolve(img)
}

// Spec lines 99-115 (policy + behaviour table): the attestation must come from the workflow
// identity, be SPDX, and carry subject[0].name == the policy's own name
predicate AttestedUnder(d: string, q: string, atts: string -> set<Attestation>)
{
  exists a :: a in atts(d) && a.identityOk && a.spdx && a.subject == q
}

// One image: unresolvable refused (line 117); each matching first-party policy must be
// satisfied (lines 109-115); otherwise admitted unverified (line 117, no-match-policy allow)
predicate ImageOk(img: Image, resolve: Image -> Option<string>, atts: string -> set<Attestation>)
{
  match DigestOf(img, resolve)
  case None => false
  case Some(d) => forall q :: q in FirstParty && Matches(q, img.repo) ==> AttestedUnder(d, q, atts)
}

predicate AllOk(imgs: seq<Image>, resolve: Image -> Option<string>, atts: string -> set<Attestation>)
{
  forall i :: 0 <= i < |imgs| ==> ImageOk(imgs[i], resolve, atts)
}

// Spec line 118: other namespaces unaffected; release namespace fails closed when engine down;
// line 115: init containers are checked too
function Admit(p: Pod, labelled: bool, engineUp: bool, resolve: Image -> Option<string>, atts: string -> set<Attestation>): bool
{
  if !labelled then true
  else if !engineUp then false
  else AllOk(p.containers, resolve, atts) && AllOk(p.initContainers, resolve, atts)
}

lemma AllOkMember(imgs: seq<Image>, img: Image, resolve: Image -> Option<string>, atts: string -> set<Attestation>)
  requires AllOk(imgs, resolve, atts) && img in imgs
  ensures ImageOk(img, resolve, atts)
{
  var i :| 0 <= i < |imgs| && imgs[i] == img;
}

lemma AllOkFromMembers(imgs: seq<Image>, resolve: Image -> Option<string>, atts: string -> set<Attestation>)
  requires forall img :: img in imgs ==> ImageOk(img, resolve, atts)
  ensures AllOk(imgs, resolve, atts)
{
}

lemma AdmitMember(p: Pod, img: Image, resolve: Image -> Option<string>, atts: string -> set<Attestation>)
  requires Admit(p, true, true, resolve, atts)
  requires img in p.containers + p.initContainers
  ensures ImageOk(img, resolve, atts)
{
  if img in p.containers { AllOkMember(p.containers, img, resolve, atts); }
  else { AllOkMember(p.initContainers, img, resolve, atts); }
}

lemma SingleOk(i: Image, resolve: Image -> Option<string>, atts: string -> set<Attestation>)
  ensures AllOk([i], resolve, atts) <==> ImageOk(i, resolve, atts)
{
  assert [i][0] == i;
}

lemma NoSubjectNotAttested(d: string, q: string, atts: string -> set<Attestation>)
  requires forall a :: a in atts(d) ==> a.subject != q
  ensures !AttestedUnder(d, q, atts)
{
}

lemma DiffNotMatch(q: string, r: string, k: nat)
  requires k < |q| && k < |r| && q[k] != r[k]
  ensures !Matches(q, r)
{
}

// Citation: "A cloud cluster admits a first-party image only if the publish workflow attested that digest under that image's own name" (Goals, line 27)
lemma P1_FirstPartyAdmittedOnlyIfAttestedUnderOwnName(p: Pod, resolve: Image -> Option<string>, atts: string -> set<Attestation>, img: Image, q: string, d: string)
  requires Admit(p, true, true, resolve, atts)
  requires img in p.containers + p.initContainers
  requires q in FirstParty && Matches(q, img.repo)
  requires DigestOf(img, resolve) == Some(d)
  ensures exists a :: a in atts(d) && a.identityOk && a.spdx && a.subject == q
{
  AdmitMember(p, img, resolve, atts);
  assert AttestedUnder(d, q, atts);
}

// Citation: "Another first-party image's digest under this name (cross-image swap) | refused" (§3 table, line 114)
lemma P2_CrossImageSwapRefused(resolve: Image -> Option<string>, atts: string -> set<Attestation>, d: string)
  requires forall a :: a in atts(d) ==> a.subject == "ghcr.io/counsellorben/iverson-admin-ui"
  ensures !Admit(Pod([Image("ghcr.io/counsellorben/iverson-api", Some(d))], []), true, true, resolve, atts)
{
  var img := Image("ghcr.io/counsellorben/iverson-api", Some(d));
  var q := "ghcr.io/counsellorben/iverson-api";
  assert q in FirstParty;
  assert Matches(q, img.repo);
  assert DigestOf(img, resolve) == Some(d);
  NoSubjectNotAttested(d, q, atts);
  assert !ImageOk(img, resolve, atts);
  SingleOk(img, resolve, atts);
}

// Citation: "Bad init container only | refused, naming `spec.initContainers[0].image`" (line 115)
lemma P3_BadInitContainerRefuses(c: Image, i: Image, resolve: Image -> Option<string>, atts: string -> set<Attestation>)
  requires Admit(Pod([c], []), true, true, resolve, atts)
  requires !Admit(Pod([i], []), true, true, resolve, atts)
  ensures !Admit(Pod([c], [i]), true, true, resolve, atts)
{
  SingleOk(i, resolve, atts);
}

// Citation: "Third-party image in the namespace | rewritten to `tag@digest` and admitted unverified (`no-match-policy: allow`)" (line 117)
lemma P4_ThirdPartyResolvableAdmitted(p: Pod, resolve: Image -> Option<string>, atts: string -> set<Attestation>)
  requires forall img :: img in p.containers + p.initContainers ==> (forall q :: q in FirstParty ==> !Matches(q, img.repo)) && DigestOf(img, resolve).Some?
  ensures Admit(p, true, true, resolve, atts)
{
  forall img | img in p.containers + p.initContainers
    ensures ImageOk(img, resolve, atts)
  {
  }
  forall img | img in p.containers ensures ImageOk(img, resolve, atts) {}
  forall img | img in p.initContainers ensures ImageOk(img, resolve, atts) {}
  AllOkFromMembers(p.containers, resolve, atts);
  AllOkFromMembers(p.initContainers, resolve, atts);
}

// Citation: "refused if its registry cannot resolve the tag (VA 45)" (line 117)
lemma P5_UnresolvableRefused(p: Pod, resolve: Image -> Option<string>, atts: string -> set<Attestation>, img: Image)
  requires img in p.containers + p.initContainers && DigestOf(img, resolve) == None
  ensures !Admit(p, true, true, resolve, atts)
{
  assert !ImageOk(img, resolve, atts);
  if Admit(p, true, true, resolve, atts) {
    AdmitMember(p, img, resolve, atts);
  }
}

// Citation: "Engine down | refused in the release namespace (failurePolicy Fail); other namespaces unaffected" (line 118)
lemma P6_EngineDown(p: Pod, resolve: Image -> Option<string>, atts: string -> set<Attestation>)
  ensures !Admit(p, true, false, resolve, atts)
  ensures Admit(p, false, false, resolve, atts)
{
}

// Citation: "Glob per image (`…/iverson-api*`); the four names share no prefix with each other." (Choices made while writing, item 4)
lemma P7_PolicyGlobsDisjoint(q: string, r: string)
  requires q in FirstParty && r in FirstParty && q != r
  ensures !Matches(q, r)
{
  if q == "ghcr.io/counsellorben/iverson-api" && r == "ghcr.io/counsellorben/iverson-admin-ui" { DiffNotMatch(q, r, 31); }
  else if q == "ghcr.io/counsellorben/iverson-api" && r == "ghcr.io/counsellorben/iverson-ollama-model" { DiffNotMatch(q, r, 30); }
  else if q == "ghcr.io/counsellorben/iverson-api" && r == "ghcr.io/counsellorben/iverson-tei-model" { DiffNotMatch(q, r, 30); }
  else if q == "ghcr.io/counsellorben/iverson-admin-ui" && r == "ghcr.io/counsellorben/iverson-api" { DiffNotMatch(q, r, 31); }
  else if q == "ghcr.io/counsellorben/iverson-admin-ui" && r == "ghcr.io/counsellorben/iverson-ollama-model" { DiffNotMatch(q, r, 30); }
  else if q == "ghcr.io/counsellorben/iverson-admin-ui" && r == "ghcr.io/counsellorben/iverson-tei-model" { DiffNotMatch(q, r, 30); }
  else if q == "ghcr.io/counsellorben/iverson-ollama-model" && r == "ghcr.io/counsellorben/iverson-api" { DiffNotMatch(q, r, 30); }
  else if q == "ghcr.io/counsellorben/iverson-ollama-model" && r == "ghcr.io/counsellorben/iverson-admin-ui" { DiffNotMatch(q, r, 30); }
  else if q == "ghcr.io/counsellorben/iverson-ollama-model" && r == "ghcr.io/counsellorben/iverson-tei-model" { DiffNotMatch(q, r, 30); }
  else if q == "ghcr.io/counsellorben/iverson-tei-model" && r == "ghcr.io/counsellorben/iverson-api" { DiffNotMatch(q, r, 30); }
  else if q == "ghcr.io/counsellorben/iverson-tei-model" && r == "ghcr.io/counsellorben/iverson-admin-ui" { DiffNotMatch(q, r, 30); }
  else if q == "ghcr.io/counsellorben/iverson-tei-model" && r == "ghcr.io/counsellorben/iverson-ollama-model" { DiffNotMatch(q, r, 30); }
}
