// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T3 S3
// Source: Iverson.Server/Iverson.Api/Consumers/EngagementStoreConsumer.cs `WithOwnerValue`
// Replay base: c79d7de067519edb3b325fbc0aa53332944c6d1c
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/EngagementStoreConsumer.WithOwnerValue.dfy

// ── Shared vocabulary ────────────────────────────────────────────────────────

type CsString = s: seq<char> | |s| < 0x8000_0000

datatype Option<T> = None | Some(value: T)
datatype Result<T> = Ok(value: T) | Threw

predicate EqIC(a: seq<char>, b: seq<char>) { OrdinalUpper(a) == OrdinalUpper(b) }

// ── Candidate model: System.Text.Json.Nodes.JsonObject ──────────────────────

datatype JNode = JStr(s: CsString) | JOther(raw: CsString)

// A JsonObject: an ordered sequence of (key, value) entries whose keys are
// compared ordinally (JsonNode.Parse with default JsonNodeOptions).
type JsonObj = seq<(CsString, JNode)>

predicate DistinctKeysJ(o: JsonObj)
{
  forall i, j :: 0 <= i < |o| && 0 <= j < |o| && i != j ==> o[i].0 != o[j].0
}

// ── Approved axioms ─────────────────────────────────────────────────────────

function ParseJsonObject(json: CsString): Option<JsonObj>

function ToJsonString(o: JsonObj): CsString

function OrdinalUpper(s: seq<char>): seq<char>

// ── Library modelling: JsonObject members used by the candidate ─────────────

// node.Select(kv => kv.Key): the keys in enumeration order.
function Keys(o: JsonObj): seq<CsString>
{
  if |o| == 0 then [] else [o[0].0] + Keys(o[1..])
}

// .Where(k => string.Equals(k, ownerField, StringComparison.OrdinalIgnoreCase)).ToList()
function FilterEqIC(ks: seq<CsString>, f: CsString): seq<CsString>
{
  if |ks| == 0 then []
  else if EqIC(ks[0], f) then [ks[0]] + FilterEqIC(ks[1..], f)
  else FilterEqIC(ks[1..], f)
}

// JsonObject.Remove(key): removes the entry whose key is ordinally equal to key
// (at most one, as keys are distinct); a missing key is a no-op returning false.
function RemoveKey(o: JsonObj, k: CsString): JsonObj
{
  if |o| == 0 then []
  else if o[0].0 == k then RemoveKey(o[1..], k)
  else [o[0]] + RemoveKey(o[1..], k)
}

// foreach (var key in keys) node.Remove(key);
function RemoveAll(o: JsonObj, ks: seq<CsString>): JsonObj
  decreases |ks|
{
  if |ks| == 0 then o else RemoveAll(RemoveKey(o, ks[0]), ks[1..])
}

// node[k] = v: replaces the value in place when the key is present (ordinally),
// otherwise appends a new entry.
function SetKey(o: JsonObj, k: CsString, v: JNode): JsonObj
{
  if |o| == 0 then [(k, v)]
  else if o[0].0 == k then [(k, v)] + o[1..]
  else [o[0]] + SetKey(o[1..], k, v)
}

// ── Transliteration ─────────────────────────────────────────────────────────

// Body between JsonNode.Parse(payloadJson)!.AsObject() and ToJsonString().
function WithOwnerValueObj(o: JsonObj, ownerField: CsString, value: Option<CsString>): JsonObj
{
  var node := RemoveAll(o, FilterEqIC(Keys(o), ownerField));
  if value.Some? then SetKey(node, ownerField, JStr(value.value))
  else node
}

// private static string WithOwnerValue(string payloadJson, string ownerField, string? authoritativeOwnerValue)
function WithOwnerValue(payloadJson: CsString, ownerField: CsString, authoritativeOwnerValue: Option<CsString>): Result<CsString>
{
  var parsed := ParseJsonObject(payloadJson);
  if parsed.None? then Threw
  else Ok(ToJsonString(WithOwnerValueObj(parsed.value, ownerField, authoritativeOwnerValue)))
}

// ── Helper lemmas ───────────────────────────────────────────────────────────

lemma EqICRefl(a: seq<char>)
  ensures EqIC(a, a)
{
}

lemma RemoveKeySubset(o: JsonObj, k: CsString)
  ensures forall e :: e in RemoveKey(o, k) ==> e in o && e.0 != k
{
  if |o| > 0 {
    RemoveKeySubset(o[1..], k);
  }
}

lemma KeysMember(o: JsonObj, e: (CsString, JNode))
  requires e in o
  ensures e.0 in Keys(o)
{
  if o[0] != e {
    KeysMember(o[1..], e);
  }
}

lemma FilterMember(ks: seq<CsString>, f: CsString, k: CsString)
  requires k in ks && EqIC(k, f)
  ensures k in FilterEqIC(ks, f)
{
  if ks[0] != k {
    FilterMember(ks[1..], f, k);
  }
}

// Every surviving entry came from o, and its key is none of the removed keys.
lemma RemoveAllSubset(o: JsonObj, ks: seq<CsString>)
  ensures forall e :: e in RemoveAll(o, ks) ==> e in o && e.0 !in ks
  decreases |ks|
{
  if |ks| > 0 {
    var o' := RemoveKey(o, ks[0]);
    RemoveKeySubset(o, ks[0]);
    RemoveAllSubset(o', ks[1..]);
  }
}

// After the removal loop, no key EqIC to ownerField survives.
lemma NoOwnerKeyAfterRemoval(o: JsonObj, f: CsString)
  ensures forall e :: e in RemoveAll(o, FilterEqIC(Keys(o), f)) ==> !EqIC(e.0, f)
{
  var ks := FilterEqIC(Keys(o), f);
  RemoveAllSubset(o, ks);
  forall e | e in RemoveAll(o, ks)
    ensures !EqIC(e.0, f)
  {
    if EqIC(e.0, f) {
      KeysMember(o, e);
      FilterMember(Keys(o), f, e.0);
    }
  }
}

lemma SetKeyAbsent(o: JsonObj, k: CsString, v: JNode)
  requires forall e :: e in o ==> e.0 != k
  ensures SetKey(o, k, v) == o + [(k, v)]
{
  if |o| > 0 {
    assert o[0] in o;
    forall e | e in o[1..] ensures e.0 != k { assert e in o; }
    SetKeyAbsent(o[1..], k, v);
  }
}

// ── Property lemmas ─────────────────────────────────────────────────────────

// Citation: "`EngagementStoreConsumer.WithOwnerValue` removes every case variant of the owner key, then writes the authoritative value under the column's spelling, as `OutboxWriter.WithTenantColumn` already does." (spec §2.3)
lemma P1_TheAuthoritativeOwnerIsTheOnlyOwnerKey(o: JsonObj, ownerField: CsString, owner: CsString)
  requires DistinctKeysJ(o)
  ensures (ownerField, JStr(owner)) in WithOwnerValueObj(o, ownerField, Some(owner))
  ensures forall e :: e in WithOwnerValueObj(o, ownerField, Some(owner)) && EqIC(e.0, ownerField) ==>
    e == (ownerField, JStr(owner))
{
  var node := RemoveAll(o, FilterEqIC(Keys(o), ownerField));
  NoOwnerKeyAfterRemoval(o, ownerField);
  EqICRefl(ownerField);
  forall e | e in node ensures e.0 != ownerField {
    assert !EqIC(e.0, ownerField);
  }
  SetKeyAbsent(node, ownerField, JStr(owner));
  assert WithOwnerValueObj(o, ownerField, Some(owner)) == node + [(ownerField, JStr(owner))];
}

// Citation: "Fail closed: with no authoritative owner, no spelling of the owner key may reach StarRocks." (plan T3 S1b, the comment heading `HandleUpsert_WithReCasedOwnerKeysAndNoAuthoritativeOwner_UpsertsNoOwnerKeyAtAll`, whose assertion on the case-insensitive owner keys ends ".Should().BeEmpty();")
lemma P2_WithNoAuthoritativeOwnerNoOwnerKeyRemains(o: JsonObj, ownerField: CsString)
  requires DistinctKeysJ(o)
  ensures forall e :: e in WithOwnerValueObj(o, ownerField, None) ==> !EqIC(e.0, ownerField)
{
  NoOwnerKeyAfterRemoval(o, ownerField);
}
