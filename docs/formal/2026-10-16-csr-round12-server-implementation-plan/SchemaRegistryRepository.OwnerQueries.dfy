// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T7 S3
// Source: Iverson.Server/Iverson.Sql/SchemaRegistryRepository.cs `GetOwnersAsync`, `UpsertIfOwnerAsync`, `ExistsAsync`
// Code ref: f7a0842fc0e707359e777ddf44f7c2e23050c968
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/SchemaRegistryRepository.OwnerQueries.dfy

// ---------------------------------------------------------------- shared vocabulary

type CsString = s: seq<char> | |s| < 0x8000_0000

datatype Option<T> = None | Some(value: T)

predicate EqIC(a: seq<char>, b: seq<char>) { OrdinalUpper(a) == OrdinalUpper(b) }

predicate Ascii(s: seq<char>) { forall i :: 0 <= i < |s| ==> s[i] < 128 as char }

// ---------------------------------------------------------------- approved axioms

function JsonOwner(schemaJson: CsString): Option<CsString>

function {:axiom} PgLower(s: CsString): CsString
  ensures (forall i :: 0 <= i < |s| ==> s[i] < 128 as char) ==>
    |PgLower(s)| == |s| &&
    forall i :: 0 <= i < |s| ==> PgLower(s)[i] == (if 'A' <= s[i] <= 'Z' then (s[i] as int + 32) as char else s[i])

function {:axiom} CharToLowerInvariant(c: char): char
  ensures c < 128 as char ==> CharToLowerInvariant(c) == (if 'A' <= c <= 'Z' then (c as int + 32) as char else c)

predicate CharIsUpper(c: char)

function {:axiom} OrdinalUpper(s: seq<char>): seq<char>
  ensures (forall i :: 0 <= i < |s| ==> s[i] < 128 as char) ==>
    |OrdinalUpper(s)| == |s| &&
    forall i :: 0 <= i < |s| ==> OrdinalUpper(s)[i] == (if 'a' <= s[i] <= 'z' then (s[i] as int - 32) as char else s[i])

// ---------------------------------------------------------------- transliteration

// _iverson_schema: type_name (TEXT PRIMARY KEY, case-sensitive) -> schema_json.
type Table = map<CsString, CsString>

// The WHERE clause shared by GetOwnersAsync and ExistsAsync: lower(type_name) = lower(@TypeName).
predicate LowerMatches(typeName: CsString, param: CsString) { PgLower(typeName) == PgLower(param) }

// GetOwnersAsync (SchemaRegistryRepository.cs:40-43):
//   SELECT schema_json->>'ownerTenantId' FROM _iverson_schema WHERE lower(type_name) = lower(@TypeName)
// The rows come back in no defined order, so the list is modelled as a multiset (one element per row).
ghost function GetOwners(tbl: Table, t: CsString): multiset<Option<CsString>>
  decreases |tbl.Keys|
{
  if |tbl.Keys| == 0 then multiset{}
  else
    var k :| k in tbl;
    assert (tbl - {k}).Keys == tbl.Keys - {k};
    var rest := GetOwners(tbl - {k}, t);
    if LowerMatches(k, t) then rest + multiset{JsonOwner(tbl[k])} else rest
}

// UpsertIfOwnerAsync (SchemaRegistryRepository.cs:47-57):
//   INSERT ... VALUES (@TypeName, @Json::jsonb, now())
//   ON CONFLICT (type_name) DO UPDATE SET ... WHERE _iverson_schema.schema_json->>'ownerTenantId' IS NOT DISTINCT FROM @Owner
//   ... > 0
function UpsertIfOwner(tbl: Table, t: CsString, json: CsString, owner: Option<CsString>): (bool, Table)
{
  if t !in tbl then
    // no conflict: the INSERT affects one row
    (1 > 0, tbl[t := json])
  else if JsonOwner(tbl[t]) == owner then
    // conflict, WHERE holds: the DO UPDATE affects one row
    (1 > 0, tbl[t := json])
  else
    // conflict, WHERE fails: zero rows, the stored row is unchanged
    (0 > 0, tbl)
}

// substr(x, 2): the string from the second character on; '' for '' (Postgres does not throw).
function Substr2(x: CsString): CsString { if |x| == 0 then [] else x[1..] }

// ExistsAsync (SchemaRegistryRepository.cs:63-70):
//   SELECT EXISTS (SELECT 1 FROM _iverson_schema
//                  WHERE lower(type_name) = lower(@TypeName) AND substr(type_name, 2) = substr(@TypeName, 2))
predicate Exists(tbl: Table, t: CsString)
{
  exists k :: k in tbl && LowerMatches(k, t) && Substr2(k) == Substr2(t)
}

// ---------------------------------------------------------------- existing code used by the properties

// NamingExtensions.ToSnakeCase (Iverson.Api/NamingExtensions.cs:5-14), the loop from index i on.
function ToSnakeCaseFrom(name: seq<char>, i: nat): seq<char>
  decreases |name| - i
{
  if i >= |name| then []
  else
    (if CharIsUpper(name[i]) && i > 0 then "_" else [])
    + [CharToLowerInvariant(name[i])]
    + ToSnakeCaseFrom(name, i + 1)
}

function ToSnakeCase(name: seq<char>): seq<char> { ToSnakeCaseFrom(name, 0) }

// SchemaBuilder.ToTableName (Iverson.Api/Schema/SchemaBuilder.cs:31).
function ToTableName(typeName: seq<char>): seq<char> { ToSnakeCase(typeName) + "s" }

// ---------------------------------------------------------------- helper lemmas

function AsciiLower(c: char): char { if 'A' <= c <= 'Z' then (c as int + 32) as char else c }
function AsciiUpper(c: char): char { if 'a' <= c <= 'z' then (c as int - 32) as char else c }

lemma UpperEqImpliesLowerEq(a: char, b: char)
  requires a < 128 as char && b < 128 as char
  requires AsciiUpper(a) == AsciiUpper(b)
  ensures AsciiLower(a) == AsciiLower(b)
{
}

lemma EqICImpliesPgLowerEq(n: CsString, t: CsString)
  requires Ascii(n) && Ascii(t) && EqIC(n, t)
  ensures PgLower(n) == PgLower(t)
{
  assert |n| == |OrdinalUpper(n)| == |OrdinalUpper(t)| == |t|;
  forall i | 0 <= i < |n|
    ensures PgLower(n)[i] == PgLower(t)[i]
  {
    assert OrdinalUpper(n)[i] == AsciiUpper(n[i]);
    assert OrdinalUpper(t)[i] == AsciiUpper(t[i]);
    UpperEqImpliesLowerEq(n[i], t[i]);
  }
}

lemma GetOwnersHasMatch(tbl: Table, t: CsString, n: CsString)
  requires n in tbl && LowerMatches(n, t)
  ensures JsonOwner(tbl[n]) in GetOwners(tbl, t)
  decreases |tbl.Keys|
{
  assert |tbl.Keys| != 0;
  var k :| k in tbl && GetOwners(tbl, t) ==
    (var rest := GetOwners(tbl - {k}, t);
     if LowerMatches(k, t) then rest + multiset{JsonOwner(tbl[k])} else rest);
  if k == n {
    var rest := GetOwners(tbl - {k}, t);
    assert LowerMatches(k, t);
    assert GetOwners(tbl, t) == rest + multiset{JsonOwner(tbl[n])};
    assert JsonOwner(tbl[n]) in multiset{JsonOwner(tbl[n])};
  } else {
    assert (tbl - {k}).Keys == tbl.Keys - {k};
    assert n in tbl - {k};
    GetOwnersHasMatch(tbl - {k}, t, n);
  }
}

lemma SnakeFromEq(a: seq<char>, b: seq<char>, i: nat)
  requires |a| == |b| && i >= 1
  requires forall j :: i <= j < |a| ==> a[j] == b[j]
  ensures ToSnakeCaseFrom(a, i) == ToSnakeCaseFrom(b, i)
  decreases |a| - i
{
  if i < |a| {
    SnakeFromEq(a, b, i + 1);
  }
}

// ---------------------------------------------------------------- properties

// Citation: "**Owner (#7).** It reads the stored owner from `_iverson_schema`: `schema_json->>'ownerTenantId'`, matched with `lower(type_name) = lower(@TypeName)` (the registry is case-insensitive; the column isn't)." (spec §5.3)
lemma P1_TheOwnerReadCoversEveryCaseVariantTheRegistryFolds(tbl: Table, t: CsString, n: CsString)
  requires n in tbl && Ascii(n) && Ascii(t) && EqIC(n, t)
  ensures JsonOwner(tbl[n]) in GetOwners(tbl, t)
{
  EqICImpliesPgLowerEq(n, t);
  GetOwnersHasMatch(tbl, t, n);
}

// Citation: "**Registry write.** `INSERT … ON CONFLICT (type_name) DO UPDATE … WHERE _iverson_schema.schema_json->>'ownerTenantId' IS NOT DISTINCT FROM @Owner`. Zero rows fails with `PermissionDenied`." (spec §5.3)
lemma P2_TheRegistryWriteReplacesOnlyARowOfTheSameOwner(tbl: Table, t: CsString, json: CsString, owner: Option<CsString>)
  ensures UpsertIfOwner(tbl, t, json, owner).0 <==> (t !in tbl || JsonOwner(tbl[t]) == owner)
  ensures UpsertIfOwner(tbl, t, json, owner).1 == (if UpsertIfOwner(tbl, t, json, owner).0 then tbl[t := json] else tbl)
{
}

// Citation: "**Adoption (#1).** This applies when the type has no registry row but its derived table exists in `public`." (spec §5.3) and "StatusCode.AlreadyExists, "holds rows of another tenant");" (plan T7 S1f, `ACaseVariantOfTheOwnersOwnType_StillRunsTheAdoptionCheck`, generalised)
lemma P3_SkippingTheAdoptionCheckNeedsAStoredRowOfTheSameTable(tbl: Table, t: CsString)
  requires Ascii(t) && forall n :: n in tbl ==> Ascii(n)
  requires Exists(tbl, t)
  ensures exists n :: n in tbl && ToTableName(n) == ToTableName(t)
{
  var n :| n in tbl && LowerMatches(n, t) && Substr2(n) == Substr2(t);
  assert Ascii(n);
  assert |n| == |PgLower(n)| == |PgLower(t)| == |t|;
  if |n| > 0 {
    forall j | 1 <= j < |n|
      ensures n[j] == t[j]
    {
      assert n[j] == n[1..][j - 1];
      assert t[j] == t[1..][j - 1];
    }
    SnakeFromEq(n, t, 1);
    assert PgLower(n)[0] == PgLower(t)[0];
    assert CharToLowerInvariant(n[0]) == CharToLowerInvariant(t[0]);
    assert ToSnakeCaseFrom(n, 0) == [CharToLowerInvariant(n[0])] + ToSnakeCaseFrom(n, 1);
    assert ToSnakeCaseFrom(t, 0) == [CharToLowerInvariant(t[0])] + ToSnakeCaseFrom(t, 1);
  }
  assert ToTableName(n) == ToTableName(t);
}

// Citation: "// "Firstletterprobe" and "firstletterprobe" derive the same table, firstletterprobes, which" / "// the stored row adopted. Another tenant's earlier row in it must not block its owner." (plan T7 S1f, `AFirstLetterVariantOfTheOwnersOwnType_SkipsTheAdoptionCheck`)
lemma P4_AStoredFirstLetterVariantCountsAsTheSameType(tbl: Table, t: CsString, n: CsString)
  requires n in tbl && Ascii(n) && Ascii(t) && |n| > 0 && |t| > 0
  requires EqIC(n[..1], t[..1]) && n[1..] == t[1..]
  ensures Exists(tbl, t)
{
  assert |n| == |t|;
  assert Ascii(n[..1]) && Ascii(t[..1]);
  assert OrdinalUpper(n[..1])[0] == AsciiUpper(n[0]);
  assert OrdinalUpper(t[..1])[0] == AsciiUpper(t[0]);
  UpperEqImpliesLowerEq(n[0], t[0]);
  forall i | 0 <= i < |n|
    ensures PgLower(n)[i] == PgLower(t)[i]
  {
    if i > 0 {
      assert n[i] == n[1..][i - 1];
      assert t[i] == t[1..][i - 1];
    }
  }
  assert PgLower(n) == PgLower(t);
  assert LowerMatches(n, t) && Substr2(n) == Substr2(t);
}
