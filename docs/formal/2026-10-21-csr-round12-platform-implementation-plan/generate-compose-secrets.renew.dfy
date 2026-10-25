// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T22 S3
// Source: scripts/generate-compose-secrets.sh LEAVES, the renew decision and the regenerate branch (53–89)
// Code ref: 8ef849174092218f507041e82b8185facafb1937
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-21-csr-round12-platform-implementation-plan/generate-compose-secrets.renew.dfy

// ---------------------------------------------------------------------------------------------
// Vocabulary: bash strings are byte sequences.
// ---------------------------------------------------------------------------------------------
newtype Byte = x: int | 0 <= x < 256

// The file system as the script observes it under TLS_DIR.
//   files:    names (relative to TLS_DIR) for which `[ -f "$TLS_DIR/<name>" ]` succeeds
//   expiring: the `.crt` names for which `openssl x509 -checkend 2592000 -noout -in …` fails
//             (unparseable ones included) -- the axiomatised library boundary
datatype Fs = Fs(files: set<seq<Byte>>, expiring: set<seq<Byte>>)

// A file the script writes: under TLS_DIR, or under the `mktemp -d` work directory.
datatype Loc = TlsDir(name: seq<Byte>) | WorkDir(name: seq<Byte>)

// "ca.crt"
const CaCrt: seq<Byte> := [99, 97, 46, 99, 114, 116]
// "ca.key"
const CaKey: seq<Byte> := [99, 97, 46, 107, 101, 121]
// ".crt"
const DotCrt: seq<Byte> := [46, 99, 114, 116]
// ".key"
const DotKey: seq<Byte> := [46, 107, 101, 121]
// ".csr"
const DotCsr: seq<Byte> := [46, 99, 115, 114]
// ".cnf"
const DotCnf: seq<Byte> := [46, 99, 110, 102]
// "openssl.err"
const OpensslErr: seq<Byte> := [111, 112, 101, 110, 115, 115, 108, 46, 101, 114, 114]
// " "
const Space: Byte := 32

// "$stem.crt", "$stem.key", "$stem.csr", "$stem.cnf"
function Crt(stem: seq<Byte>): seq<Byte> { stem + DotCrt }
function Key(stem: seq<Byte>): seq<Byte> { stem + DotKey }
function Csr(stem: seq<Byte>): seq<Byte> { stem + DotCsr }
function Cnf(stem: seq<Byte>): seq<Byte> { stem + DotCnf }

// ---------------------------------------------------------------------------------------------
// Lines 52-59: LEAVES=( "<file stem> <common name> <subjectAltName>" ... )
// ---------------------------------------------------------------------------------------------
// "tls"
const StemTls: seq<Byte> := [116, 108, 115]
// "authentik-server DNS:authentik-server,DNS:localhost"
const RestTls: seq<Byte> := [97, 117, 116, 104, 101, 110, 116, 105, 107, 45, 115, 101, 114, 118, 101, 114, 32, 68, 78, 83, 58, 97, 117, 116, 104, 101, 110, 116, 105, 107, 45, 115, 101, 114, 118, 101, 114, 44, 68, 78, 83, 58, 108, 111, 99, 97, 108, 104, 111, 115, 116]
// "postgres"
const StemPostgres: seq<Byte> := [112, 111, 115, 116, 103, 114, 101, 115]
// "postgres DNS:postgres,DNS:localhost"
const RestPostgres: seq<Byte> := [112, 111, 115, 116, 103, 114, 101, 115, 32, 68, 78, 83, 58, 112, 111, 115, 116, 103, 114, 101, 115, 44, 68, 78, 83, 58, 108, 111, 99, 97, 108, 104, 111, 115, 116]
// "starrocks"
const StemStarrocks: seq<Byte> := [115, 116, 97, 114, 114, 111, 99, 107, 115]
// "starrocks DNS:starrocks,DNS:localhost"
const RestStarrocks: seq<Byte> := [115, 116, 97, 114, 114, 111, 99, 107, 115, 32, 68, 78, 83, 58, 115, 116, 97, 114, 114, 111, 99, 107, 115, 44, 68, 78, 83, 58, 108, 111, 99, 97, 108, 104, 111, 115, 116]
// "kestrel"
const StemKestrel: seq<Byte> := [107, 101, 115, 116, 114, 101, 108]
// "iverson-api DNS:iverson-api,DNS:iverson-worker,DNS:localhost,IP:127.0.0.1"
const RestKestrel: seq<Byte> := [105, 118, 101, 114, 115, 111, 110, 45, 97, 112, 105, 32, 68, 78, 83, 58, 105, 118, 101, 114, 115, 111, 110, 45, 97, 112, 105, 44, 68, 78, 83, 58, 105, 118, 101, 114, 115, 111, 110, 45, 119, 111, 114, 107, 101, 114, 44, 68, 78, 83, 58, 108, 111, 99, 97, 108, 104, 111, 115, 116, 44, 73, 80, 58, 49, 50, 55, 46, 48, 46, 48, 46, 49]
// "qdrant"
const StemQdrant: seq<Byte> := [113, 100, 114, 97, 110, 116]
// "qdrant DNS:qdrant,DNS:localhost"
const RestQdrant: seq<Byte> := [113, 100, 114, 97, 110, 116, 32, 68, 78, 83, 58, 113, 100, 114, 97, 110, 116, 44, 68, 78, 83, 58, 108, 111, 99, 97, 108, 104, 111, 115, 116]

// Each array element is the literal string, written as <stem> + " " + <rest>.
const LEAVES: seq<seq<Byte>> := [
  StemTls + [Space] + RestTls,
  StemPostgres + [Space] + RestPostgres,
  StemStarrocks + [Space] + RestStarrocks,
  StemKestrel + [Space] + RestKestrel,
  StemQdrant + [Space] + RestQdrant
]

// ---------------------------------------------------------------------------------------------
// `read -r stem _ <<< "$leaf"` (lines 63, 79): default IFS (space, tab, newline); leading IFS
// whitespace is skipped and `stem` receives the first field.
// ---------------------------------------------------------------------------------------------
predicate IsIfs(b: Byte) { b == 32 || b == 9 || b == 10 }

function DropIfs(s: seq<Byte>): seq<Byte>
  decreases |s|
{
  if |s| > 0 && IsIfs(s[0]) then DropIfs(s[1..]) else s
}

function TakeField(s: seq<Byte>): seq<Byte>
  decreases |s|
{
  if |s| == 0 || IsIfs(s[0]) then [] else [s[0]] + TakeField(s[1..])
}

function ReadStem(line: seq<Byte>): seq<Byte> { TakeField(DropIfs(line)) }

// The stems the loops iterate over: `for leaf in "${LEAVES[@]}"; do read -r stem … <<< "$leaf"`.
function StemsOf(leaves: seq<seq<Byte>>): seq<seq<Byte>>
  decreases |leaves|
{
  if leaves == [] then [] else [ReadStem(leaves[0])] + StemsOf(leaves[1..])
}

function Leaves(): seq<seq<Byte>> { StemsOf(LEAVES) }

// ---------------------------------------------------------------------------------------------
// Lines 60-68: the renew decision.
// ---------------------------------------------------------------------------------------------
// Lines 62-68: one iteration per leaf, in array order.
function RenewLoop(fs: Fs, leaves: seq<seq<Byte>>, renew: bool): bool
  decreases |leaves|
{
  if leaves == [] then renew
  else
    var stem := ReadStem(leaves[0]);                                  // line 63
    var renew' :=
      if Crt(stem) !in fs.files || Key(stem) !in fs.files              // line 64
         || Crt(stem) in fs.expiring                                    // line 65 (short-circuit kept)
      then true                                                         // line 66
      else renew;
    RenewLoop(fs, leaves[1..], renew')
}

// Lines 60-61, 62-68, and the test at line 69: `[ "$renew" = yes ]`.
function Renew(fs: Fs): bool
{
  var renew := false;                                                   // line 60
  var renew1 := if CaCrt in fs.files then renew else true;              // line 61
  RenewLoop(fs, LEAVES, renew1)                                         // lines 62-68
}

// ---------------------------------------------------------------------------------------------
// Lines 69-89: the regenerate branch, as the sequence of files it writes.
// `quiet` (line 75) redirects stderr to "$work/openssl.err" on every call; openssl failures
// exit 1 (outside the properties) and are not modelled.
// ---------------------------------------------------------------------------------------------
// Lines 78-86: one iteration per leaf.
function LeafWrites(leaves: seq<seq<Byte>>): seq<Loc>
  decreases |leaves|
{
  if leaves == [] then []
  else
    var stem := ReadStem(leaves[0]);                                  // line 79
    [ WorkDir(OpensslErr), TlsDir(Key(stem)), WorkDir(Csr(stem)),       // lines 80-81
      WorkDir(Cnf(stem)),                                               // line 82
      WorkDir(OpensslErr), TlsDir(Crt(stem)) ]                          // lines 83-84
    + LeafWrites(leaves[1..])
}

// Lines 70-88.
function Regenerate(): seq<Loc>
{
  // lines 70-73: mkdir/chmod/mktemp/trap write no file under TLS_DIR
  [ WorkDir(OpensslErr), WorkDir(CaKey), TlsDir(CaCrt) ]                // lines 76-77
  + LeafWrites(LEAVES)                                                  // lines 78-86
  // lines 87-88: chmod/echo write no file
}

function Writes(fs: Fs): seq<Loc>
{
  if Renew(fs) then Regenerate() else []                                // lines 69, 89
}

// The files under TLS_DIR the script writes.
function TlsNames(ws: seq<Loc>): set<seq<Byte>>
{
  set l | l in ws && l.TlsDir? :: l.name
}

function Written(fs: Fs): set<seq<Byte>> { TlsNames(Writes(fs)) }

// ---------------------------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------------------------
predicate Bad(fs: Fs, s: seq<Byte>)
{
  Crt(s) !in fs.files || Key(s) !in fs.files || Crt(s) in fs.expiring
}

lemma RenewLoopSpec(fs: Fs, leaves: seq<seq<Byte>>, renew: bool)
  ensures RenewLoop(fs, leaves, renew) <==> (renew || exists s :: s in StemsOf(leaves) && Bad(fs, s))
  decreases |leaves|
{
  if leaves != [] {
    var stem := ReadStem(leaves[0]);
    var renew' := if Bad(fs, stem) then true else renew;
    RenewLoopSpec(fs, leaves[1..], renew');
    assert StemsOf(leaves) == [stem] + StemsOf(leaves[1..]);
    if exists s :: s in StemsOf(leaves) && Bad(fs, s) {
      var s :| s in StemsOf(leaves) && Bad(fs, s);
      if s != stem {
        assert s in StemsOf(leaves[1..]);
      }
    }
  }
}

lemma TlsNamesConcat(a: seq<Loc>, b: seq<Loc>)
  ensures TlsNames(a + b) == TlsNames(a) + TlsNames(b)
{
  forall n | n in TlsNames(a + b) ensures n in TlsNames(a) + TlsNames(b) {
    var l :| l in a + b && l.TlsDir? && l.name == n;
    if l in a { assert n in TlsNames(a); } else { assert l in b; assert n in TlsNames(b); }
  }
  forall n | n in TlsNames(a) + TlsNames(b) ensures n in TlsNames(a + b) {
    if n in TlsNames(a) {
      var l :| l in a && l.TlsDir? && l.name == n;
      assert l in a + b;
    } else {
      var l :| l in b && l.TlsDir? && l.name == n;
      assert l in a + b;
    }
  }
}

lemma LeafWritesSpec(leaves: seq<seq<Byte>>)
  ensures TlsNames(LeafWrites(leaves))
          == (set s | s in StemsOf(leaves) :: Crt(s)) + (set s | s in StemsOf(leaves) :: Key(s))
  decreases |leaves|
{
  if leaves == [] {
    assert TlsNames(LeafWrites(leaves)) == {};
  } else {
    var stem := ReadStem(leaves[0]);
    var head: seq<Loc> := [ WorkDir(OpensslErr), TlsDir(Key(stem)), WorkDir(Csr(stem)),
                            WorkDir(Cnf(stem)), WorkDir(OpensslErr), TlsDir(Crt(stem)) ];
    assert LeafWrites(leaves) == head + LeafWrites(leaves[1..]);
    TlsNamesConcat(head, LeafWrites(leaves[1..]));
    assert TlsNames(head) == {Key(stem), Crt(stem)} by {
      assert TlsDir(Key(stem)) in head;
      assert TlsDir(Crt(stem)) in head;
    }
    LeafWritesSpec(leaves[1..]);
    var rest := StemsOf(leaves[1..]);
    assert StemsOf(leaves) == [stem] + rest;
    assert (set s | s in StemsOf(leaves) :: Crt(s)) == {Crt(stem)} + (set s | s in rest :: Crt(s));
    assert (set s | s in StemsOf(leaves) :: Key(s)) == {Key(stem)} + (set s | s in rest :: Key(s));
  }
}

predicate NoIfs(s: seq<Byte>) { forall i :: 0 <= i < |s| ==> !IsIfs(s[i]) }

lemma TakeFieldSplit(stem: seq<Byte>, rest: seq<Byte>)
  requires NoIfs(stem)
  ensures TakeField(stem + [Space] + rest) == stem
  decreases |stem|
{
  if stem == [] {
    assert (stem + [Space] + rest)[0] == Space;
  } else {
    assert (stem + [Space] + rest)[1..] == stem[1..] + [Space] + rest;
    TakeFieldSplit(stem[1..], rest);
  }
}

lemma ReadStemSplit(stem: seq<Byte>, rest: seq<Byte>)
  requires |stem| > 0 && NoIfs(stem)
  ensures ReadStem(stem + [Space] + rest) == stem
{
  assert (stem + [Space] + rest)[0] == stem[0];
  assert DropIfs(stem + [Space] + rest) == stem + [Space] + rest;
  TakeFieldSplit(stem, rest);
}

lemma LeavesValue()
  ensures Leaves() == [StemTls, StemPostgres, StemStarrocks, StemKestrel, StemQdrant]
{
  ReadStemSplit(StemTls, RestTls);
  ReadStemSplit(StemPostgres, RestPostgres);
  ReadStemSplit(StemStarrocks, RestStarrocks);
  ReadStemSplit(StemKestrel, RestKestrel);
  ReadStemSplit(StemQdrant, RestQdrant);
  var l := LEAVES;
  assert l[1..][1..][1..][1..][1..] == [];
  assert StemsOf(l[1..][1..][1..][1..]) == [ReadStem(l[4])];
  assert StemsOf(l[1..][1..][1..]) == [ReadStem(l[3]), ReadStem(l[4])];
  assert StemsOf(l[1..][1..]) == [ReadStem(l[2]), ReadStem(l[3]), ReadStem(l[4])];
  assert StemsOf(l[1..]) == [ReadStem(l[1]), ReadStem(l[2]), ReadStem(l[3]), ReadStem(l[4])];
  assert StemsOf(l) == [ReadStem(l[0]), ReadStem(l[1]), ReadStem(l[2]), ReadStem(l[3]), ReadStem(l[4])];
}

// ---------------------------------------------------------------------------------------------
// Properties (frozen)
// ---------------------------------------------------------------------------------------------

// Citation: "All are rewritten together whenever any is missing or near expiry" (§4.6 line 438); plan check `t22_gen.sh` (T22 S1: "a re-run keeps the files", "a missing leaf key rewrites every file", "a leaf near expiry rewrites every file"), generalised
lemma P1_RenewWhenAnyMissingOrExpiring(fs: Fs)
  ensures Renew(fs) <==> (CaCrt !in fs.files || exists s :: s in Leaves() && (Crt(s) !in fs.files || Key(s) !in fs.files || Crt(s) in fs.expiring))
{
  RenewLoopSpec(fs, LEAVES, CaCrt !in fs.files);
  assert Renew(fs) == RenewLoop(fs, LEAVES, CaCrt !in fs.files);
  assert (exists s :: s in Leaves() && Bad(fs, s))
     <==> (exists s :: s in Leaves() && (Crt(s) !in fs.files || Key(s) !in fs.files || Crt(s) in fs.expiring));
}

// Citation: "All are rewritten together …, and the CA key is still deleted afterwards" (§4.6 line 438); plan check `t22_gen.sh` ("still no CA key")
lemma P2_RenewRewritesEverythingButTheCaKey(fs: Fs)
  requires Renew(fs)
  ensures Written(fs) == {CaCrt} + (set s | s in Leaves() :: Crt(s)) + (set s | s in Leaves() :: Key(s))
  ensures CaKey !in Written(fs)
{
  var head: seq<Loc> := [ WorkDir(OpensslErr), WorkDir(CaKey), TlsDir(CaCrt) ];
  assert Writes(fs) == head + LeafWrites(LEAVES);
  TlsNamesConcat(head, LeafWrites(LEAVES));
  assert TlsNames(head) == {CaCrt} by { assert TlsDir(CaCrt) in head; }
  LeafWritesSpec(LEAVES);
  LeavesValue();
  var L := Leaves();
  forall s | s in L ensures CaKey != Crt(s) && CaKey != Key(s) {
    // every stem's length differs from |"ca"| == 2, so neither name has the length of "ca.key"
    assert |s| != 2;
    assert |CaKey| != |Crt(s)| && |CaKey| != |Key(s)|;
  }
}
