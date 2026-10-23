// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T25 S3
// Source: Iverson.Server/deploy/scripts/admission-check.sh classify (52–60), the option parser (186–197)
// Replay base: 7c4d86687b557e7e33a52c30cc07e6728dfb4622
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-21-csr-round12-platform-implementation-plan/admission-check.classify.dfy

// ---------------------------------------------------------------------------
// Vocabulary: bash strings are byte sequences.
// ---------------------------------------------------------------------------

newtype Byte = x: int | 0 <= x < 256

// s contains t as a contiguous substring.
predicate OccursAt(s: seq<Byte>, t: seq<Byte>, i: int)
{
  0 <= i && i + |t| <= |s| && s[i..i + |t|] == t
}

predicate Contains(s: seq<Byte>, t: seq<Byte>)
{
  exists i | 0 <= i <= |s| :: OccursAt(s, t, i)
}

const Newline: seq<Byte> := [10]                                        // "\n"
const Admitted: seq<Byte> := [97, 100, 109, 105, 116, 116, 101, 100]    // "admitted"
const RefusedWord: seq<Byte> := [114, 101, 102, 117, 115, 101, 100]     // "refused"
const ErrorWord: seq<Byte> := [101, 114, 114, 111, 114]                 // "error"

const ReleaseFlag: seq<Byte> := [45, 45, 114, 101, 108, 101, 97, 115, 101]          // "--release"
const NamespaceFlag: seq<Byte> := [45, 45, 110, 97, 109, 101, 115, 112, 97, 99, 101] // "--namespace"
const HFlag: seq<Byte> := [45, 104]                                                // "-h"
const HelpFlag: seq<Byte> := [45, 45, 104, 101, 108, 112]                          // "--help"
const DefaultRelease: seq<Byte> := [105, 118, 101, 114, 115, 111, 110]             // RELEASE=iverson (line 38)
const DefaultNs: seq<Byte> := [105, 118, 101, 114, 115, 111, 110]                  // NS=iverson (line 39)

// ---------------------------------------------------------------------------
// Library boundary (approved axiom): printf '%s' "$2" | command grep -qF -- "$3"
// ---------------------------------------------------------------------------

function GrepQF(text: seq<Byte>, pat: seq<Byte>): bool
  ensures pat != [] && !Contains(pat, Newline) ==> (GrepQF(text, pat) <==> Contains(text, pat))

// ---------------------------------------------------------------------------
// Transliteration: classify (admission-check.sh 52–60)
// The value is what `$(classify ...)` captures (echo's trailing newline stripped).
// ---------------------------------------------------------------------------

function Classify(rc: int, out: seq<Byte>, policy: seq<Byte>): seq<Byte>
{
  if rc == 0 then                      // 53: if [ "$1" -eq 0 ]; then
    Admitted                           // 54:   echo admitted
  else if GrepQF(out, policy) then     // 55: elif printf '%s' "$2" | command grep -qF -- "$3"; then
    RefusedWord                        // 56:   echo refused
  else                                 // 57: else
    ErrorWord                          // 58:   echo error
}

// ---------------------------------------------------------------------------
// Transliteration: the option parser (admission-check.sh 186–197), run as a script
// (the sourced-guard at 186–188 is not taken; see the correspondence notes).
// ---------------------------------------------------------------------------

datatype OptionsOutcome = Parsed(release: seq<Byte>, ns: seq<Byte>) | Exit(code: int)

function Options(args: seq<seq<Byte>>): OptionsOutcome
{
  OptionsLoop(args, DefaultRelease, DefaultNs)
}

function OptionsLoop(args: seq<seq<Byte>>, release: seq<Byte>, ns: seq<Byte>): OptionsOutcome
  decreases |args|
{
  if |args| == 0 then                                   // 190: while [ $# -gt 0 ]; do
    Parsed(release, ns)
  else if args[0] == ReleaseFlag then                   // 192: --release)
    if |args| >= 2 then                                 //        [ $# -ge 2 ] ||
      OptionsLoop(args[2..], args[1], ns)               //        RELEASE=$2; shift 2
    else
      Exit(2)                                           //        { usage; exit 2; }
  else if args[0] == NamespaceFlag then                 // 193: --namespace)
    if |args| >= 2 then                                 //        [ $# -ge 2 ] ||
      OptionsLoop(args[2..], release, args[1])          //        NS=$2; shift 2
    else
      Exit(2)                                           //        { usage; exit 2; }
  else if args[0] == HFlag || args[0] == HelpFlag then  // 194: -h|--help) usage; exit 0
    Exit(0)
  else                                                  // 195: *) usage; exit 2
    Exit(2)
}

// ---------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------

lemma OptionsLoopExitCodes(args: seq<seq<Byte>>, release: seq<Byte>, ns: seq<Byte>)
  ensures OptionsLoop(args, release, ns).Exit? ==> (OptionsLoop(args, release, ns).code == 0 || OptionsLoop(args, release, ns).code == 2)
  decreases |args|
{
  if |args| >= 2 && args[0] == ReleaseFlag {
    OptionsLoopExitCodes(args[2..], args[1], ns);
  } else if |args| >= 2 && args[0] == NamespaceFlag {
    OptionsLoopExitCodes(args[2..], release, args[1]);
  }
}

// ---------------------------------------------------------------------------
// Properties (frozen)
// ---------------------------------------------------------------------------

// Citation: tests (T25 S1): "success is admitted", "the policy named in the error is refused", "an RBAC refusal is an error", "another policy is an error", generalised; "a non-operator refused … the operators group admitted" (§6 line 519)
lemma P1_Classify(rc: int, out: seq<Byte>, policy: seq<Byte>)
  requires policy != [] && !Contains(policy, Newline)
  ensures Classify(rc, out, policy) == (if rc == 0 then Admitted else if Contains(out, policy) then RefusedWord else ErrorWord)
{
}

// Citation: tests "admission-check: --help exits 0", "admission-check: an unknown option exits 2"
lemma P2_Options(args: seq<seq<Byte>>)
  ensures Options(args).Exit? ==> (Options(args).code == 0 || Options(args).code == 2)
  ensures (|args| >= 1 && (args[0] == HelpFlag || args[0] == HFlag)) ==> Options(args) == Exit(0)
  ensures (|args| >= 1 && args[0] != HelpFlag && args[0] != HFlag && args[0] != ReleaseFlag && args[0] != NamespaceFlag) ==> Options(args) == Exit(2)
{
  OptionsLoopExitCodes(args, DefaultRelease, DefaultNs);
}
