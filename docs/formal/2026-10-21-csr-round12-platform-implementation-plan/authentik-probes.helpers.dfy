// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-21-csr-round12-platform-implementation-plan.md, T25 S3
// Source: Iverson.Server/deploy/scripts/authentik-probes.sh edge_client_from_xff, ip_is, is_loopback, is_console, setup_shows_no_prompt (60–92), the option parser (216–227)
// Replay base: 7c4d86687b557e7e33a52c30cc07e6728dfb4622
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-21-csr-round12-platform-implementation-plan/authentik-probes.helpers.dfy

// ---------------------------------------------------------------------------------------------
// Shared vocabulary: bash strings are byte sequences.
// ---------------------------------------------------------------------------------------------

newtype Byte = x: int | 0 <= x < 256

datatype Option<T> = None | Some(value: T)

// ---------------------------------------------------------------------------------------------
// Named constants (values from the Library boundary bullet), as bytes.
// ---------------------------------------------------------------------------------------------

const CommaByte: Byte := 44                                              // ","
const SpaceByte: Byte := 32                                              // " "
const SlashByte: Byte := 47                                              // "/"
const NewlineByte: Byte := 10                                            // "\n"
const Newline: seq<Byte> := [10]                                         // "\n"
const MappedPrefix: seq<Byte> := [58, 58, 102, 102, 102, 102, 58]        // "::ffff:"
const Lo4: seq<Byte> := [49, 50, 55, 46]                                 // "127."
const Lo6: seq<Byte> := [58, 58, 49]                                     // "::1"
const MappedLo4: seq<Byte> := [58, 58, 102, 102, 102, 102, 58, 49, 50, 55, 46] // "::ffff:127."
const S500: seq<Byte> := [53, 48, 48]                                    // "500"
const S302: seq<Byte> := [51, 48, 50]                                    // "302"
const InitialSetupWord: seq<Byte> := [105, 110, 105, 116, 105, 97, 108, 45, 115, 101, 116, 117, 112] // "initial-setup"
const HelpFlag: seq<Byte> := [45, 45, 104, 101, 108, 112]                // "--help"
const HFlag: seq<Byte> := [45, 104]                                      // "-h"
const ReleaseFlag: seq<Byte> := [45, 45, 114, 101, 108, 101, 97, 115, 101]      // "--release"
const NamespaceFlag: seq<Byte> := [45, 45, 110, 97, 109, 101, 115, 112, 97, 99, 101] // "--namespace"
const IversonDefault: seq<Byte> := [105, 118, 101, 114, 115, 111, 110]  // "iverson" (lines 42–43)

// ---------------------------------------------------------------------------------------------
// String helpers used by the properties.
// ---------------------------------------------------------------------------------------------

predicate IsPrefix(p: seq<Byte>, x: seq<Byte>)
{
  |p| <= |x| && x[..|p|] == p
}

// `sub` occurs in `s` at offset `i`.
predicate OccursAt(s: seq<Byte>, sub: seq<Byte>, i: int)
{
  0 <= i && i + |sub| <= |s| && s[i..i + |sub|] == sub
}

predicate Contains(s: seq<Byte>, sub: seq<Byte>)
{
  exists i {:trigger OccursAt(s, sub, i)} :: 0 <= i <= |s| && OccursAt(s, sub, i)
}

// `${x// /}`: every space removed.
function StripSpaces(e: seq<Byte>): seq<Byte>
  decreases |e|
{
  if |e| == 0 then []
  else if e[0] == SpaceByte then StripSpaces(e[1..])
  else [e[0]] + StripSpaces(e[1..])
}

// `${x%/}`: the shortest suffix matching `/` removed, i.e. one trailing slash.
function StripOneSlash(x: seq<Byte>): seq<Byte>
{
  if |x| > 0 && x[|x| - 1] == SlashByte then x[..|x| - 1] else x
}

// `h` split at each `,` (every field, including empty and trailing ones); the empty string has none.
function SplitAt(s: seq<Byte>, d: Byte): seq<seq<Byte>>
  ensures |SplitAt(s, d)| >= 1
  decreases |s|
{
  if |s| == 0 then [[]]
  else if s[0] == d then [[]] + SplitAt(s[1..], d)
  else
    var r := SplitAt(s[1..], d);
    [[s[0]] + r[0]] + r[1..]
}

function Entries(h: seq<Byte>): seq<seq<Byte>>
{
  if h == [] then [] else SplitAt(h, CommaByte)
}

// ---------------------------------------------------------------------------------------------
// Library-boundary models.
// ---------------------------------------------------------------------------------------------

// bash `$(( ))` arithmetic is intmax_t (64-bit, wrapping).
const TwoTo63: int := 0x8000_0000_0000_0000
const TwoTo64: int := 0x1_0000_0000_0000_0000

function Wrap64(x: int): int
{
  ((x + TwoTo63) % TwoTo64) - TwoTo63
}

// `read -r` from the here-string `<<< "$1"` (which appends "\n"): the bytes up to the first newline.
function ReadLine(s: seq<Byte>): seq<Byte>
  decreases |s|
{
  if |s| == 0 then []
  else if s[0] == NewlineByte then []
  else [s[0]] + ReadLine(s[1..])
}

// `read -a` with `IFS=,` (no IFS whitespace): fields split at each `,`; an empty line has no fields;
// a line ending in `,` drops that one trailing empty field (observed: "a," -> [a], "," -> [""],
// "a,," -> [a, ""]).
function ReadArrayComma(line: seq<Byte>): seq<seq<Byte>>
{
  if line == [] then []
  else
    var f := SplitAt(line, CommaByte);
    if line[|line| - 1] == CommaByte then f[..|f| - 1] else f
}

// ---------------------------------------------------------------------------------------------
// Transliteration: authentik-probes.sh lines 60–66, edge_client_from_xff <X-Forwarded-For> <hops>.
// None models `return 1`; Some(v) models `printf '%s\n' v` (status 0), v as the caller's `$(...)` sees it.
// ---------------------------------------------------------------------------------------------

function EdgeClient(header: seq<Byte>, hops: nat): Option<seq<Byte>>
{
  // 61–62: local IFS=, entries=() i ; read -r -a entries <<< "$1"
  var entries := ReadArrayComma(ReadLine(header + Newline));
  var n := |entries|;                                   // ${#entries[@]}
  // 63: i=$(( ${#entries[@]} - $2 )); `$2` is evaluated as intmax_t, as is the difference.
  // An array of 2^63 or more elements has no bash counterpart; there the plain difference is used.
  var i := if n < TwoTo63 then Wrap64(n - Wrap64(hops)) else n - hops;
  // 64: [ "${#entries[@]}" -gt 0 ] && [ "$i" -ge 0 ] || return 1
  if !(n > 0 && i >= 0) then None
  // 65: printf '%s\n' "${entries[$i]// /}"   (an unset index expands to "")
  else Some(StripSpaces(if i < n then entries[i] else []))
}

// Line 76: ip_is() { [ "$1" = "$2" ] || [ "$1" = "::ffff:$2" ]; }
predicate IpIs(a: seq<Byte>, b: seq<Byte>)
{
  a == b || a == MappedPrefix + b
}

// `case` glob `p*` (p free of glob characters): x starts with p.
predicate GlobPrefixStar(x: seq<Byte>, p: seq<Byte>)
{
  |x| >= |p| && x[..|p|] == p
}

// Lines 78–83: case "$1" in 127.*|::1|::ffff:127.*) return 0 ;; *) return 1 ;; esac
predicate IsLoopback(x: seq<Byte>)
{
  if GlobPrefixStar(x, Lo4) || x == Lo6 || GlobPrefixStar(x, MappedLo4) then true
  else false
}

// Line 86: is_console() { [ -n "$1" ] && [ "${1%/}" = "${CONSOLE_URL%/}" ]; }   (CONSOLE_URL as a parameter)
predicate IsConsole(x: seq<Byte>, consoleUrl: seq<Byte>)
{
  x != [] && StripOneSlash(x) == StripOneSlash(consoleUrl)
}

// `[[ s != *w* ]]` negates the glob `*w*` (w free of glob characters): w occurs in s.
predicate GlobStarWordStar(s: seq<Byte>, w: seq<Byte>)
{
  exists i {:trigger OccursAt(s, w, i)} :: 0 <= i <= |s| && OccursAt(s, w, i)
}

// Lines 90–92: [ "$1" = 500 ] || { [ "$1" = 302 ] && [[ "$2" != *initial-setup* ]]; }
predicate SetupShowsNoPrompt(status: seq<Byte>, location: seq<Byte>)
{
  status == S500 || (status == S302 && !GlobStarWordStar(location, InitialSetupWord))
}

// Lines 220–227: the option parser. Parsed(release, ns) is falling out of the loop; Exit(code) is `exit code`.
// Lines 216–218 (`return 0` when sourced) are the sourcing guard; the model is the executed script.
datatype OptionsOutcome = Parsed(release: seq<Byte>, ns: seq<Byte>) | Exit(code: int)

function OptionsLoop(args: seq<seq<Byte>>, release: seq<Byte>, ns: seq<Byte>): OptionsOutcome
  decreases |args|
{
  // 220: while [ $# -gt 0 ]; do
  if |args| == 0 then Parsed(release, ns)
  // 222: --release) [ $# -ge 2 ] || { usage; exit 2; }; RELEASE=$2; shift 2 ;;
  else if args[0] == ReleaseFlag then
    (if |args| >= 2 then OptionsLoop(args[2..], args[1], ns) else Exit(2))
  // 223: --namespace) [ $# -ge 2 ] || { usage; exit 2; }; NS=$2; shift 2 ;;
  else if args[0] == NamespaceFlag then
    (if |args| >= 2 then OptionsLoop(args[2..], release, args[1]) else Exit(2))
  // 224: -h|--help) usage; exit 0 ;;
  else if args[0] == HFlag || args[0] == HelpFlag then Exit(0)
  // 225: *) usage; exit 2 ;;
  else Exit(2)
}

// Lines 42–43: RELEASE=iverson, NS=iverson before the loop.
function Options(args: seq<seq<Byte>>): OptionsOutcome
{
  OptionsLoop(args, IversonDefault, IversonDefault)
}

// ---------------------------------------------------------------------------------------------
// Helper lemmas.
// ---------------------------------------------------------------------------------------------

lemma Wrap64Id(x: int)
  requires -TwoTo63 <= x < TwoTo63
  ensures Wrap64(x) == x
{
  assert 0 <= x + TwoTo63 < TwoTo64;
  assert (x + TwoTo63) % TwoTo64 == x + TwoTo63;
}

lemma NewlineByteContained(h: seq<Byte>, j: int)
  requires 0 <= j < |h| && h[j] == NewlineByte
  ensures Contains(h, Newline)
{
  assert h[j..j + |Newline|] == Newline;
  assert OccursAt(h, Newline, j);
}

lemma NewlineBytesContained(h: seq<Byte>)
  ensures forall j :: 0 <= j < |h| && h[j] == NewlineByte ==> Contains(h, Newline)
{
  forall j | 0 <= j < |h| && h[j] == NewlineByte
    ensures Contains(h, Newline)
  {
    NewlineByteContained(h, j);
  }
}

lemma ReadLineNoNewline(h: seq<Byte>)
  requires forall j :: 0 <= j < |h| ==> h[j] != NewlineByte
  ensures ReadLine(h + Newline) == h
  decreases |h|
{
  if |h| == 0 {
    assert h + Newline == [NewlineByte];
  } else {
    assert (h + Newline)[1..] == h[1..] + Newline;
    ReadLineNoNewline(h[1..]);
  }
}

lemma GlobIsContains(s: seq<Byte>, w: seq<Byte>)
  ensures GlobStarWordStar(s, w) <==> Contains(s, w)
{
  if GlobStarWordStar(s, w) {
    var i :| 0 <= i <= |s| && OccursAt(s, w, i);
    assert Contains(s, w);
  }
  if Contains(s, w) {
    var i :| 0 <= i <= |s| && OccursAt(s, w, i);
    assert GlobStarWordStar(s, w);
  }
}

lemma OptionsLoopCodes(args: seq<seq<Byte>>, release: seq<Byte>, ns: seq<Byte>)
  ensures OptionsLoop(args, release, ns).Exit? ==> (OptionsLoop(args, release, ns).code == 0 || OptionsLoop(args, release, ns).code == 2)
  decreases |args|
{
  if |args| >= 2 && (args[0] == ReleaseFlag || args[0] == NamespaceFlag) {
    if args[0] == ReleaseFlag {
      OptionsLoopCodes(args[2..], args[1], ns);
    } else {
      OptionsLoopCodes(args[2..], release, args[1]);
    }
  }
}

// ---------------------------------------------------------------------------------------------
// Properties (frozen).
// ---------------------------------------------------------------------------------------------

// Citation: tests (T25 S1 `test_probes.sh`): "edge client, hops 1, is the right-most entry", "hops 2, is the second from the right", "of an empty header is none", "beyond the header is none", generalised
lemma P1_EdgeClient(header: seq<Byte>, hops: nat)
  requires hops == 1 || hops == 2
  requires header == [] || (header[|header| - 1] != CommaByte && !Contains(header, Newline))
  ensures EdgeClient(header, hops) == (if |Entries(header)| >= hops then Some(StripSpaces(Entries(header)[|Entries(header)| - hops])) else None)
{
  NewlineBytesContained(header);
  ReadLineNoNewline(header);
  assert ReadLine(header + Newline) == header;
  var entries := ReadArrayComma(header);
  assert entries == Entries(header);
  var n := |entries|;
  Wrap64Id(hops);
  assert Wrap64(hops) == hops;
  if n < TwoTo63 {
    Wrap64Id(n - hops);
  }
}

// Citation: tests "a plain address matches", "an IPv4-mapped address matches", "another address does not match"
lemma P2_IpIs(a: seq<Byte>, b: seq<Byte>)
  ensures IpIs(a, b) <==> (a == b || a == MappedPrefix + b)
{
}

// Citation: tests "the tls-proxy bind address is loopback", "the port-forward address is loopback", "a pod address is not loopback"
lemma P3_IsLoopback(x: seq<Byte>)
  ensures IsLoopback(x) <==> (IsPrefix(Lo4, x) || x == Lo6 || IsPrefix(MappedLo4, x))
{
}

// Citation: tests "the console URL without its slash is the console", "the login page is not the console", "an empty Location is not the console"; "`= /` redirects (302) to the console URL" (§2.7 line 216)
lemma P4_IsConsole(x: seq<Byte>, consoleUrl: seq<Byte>)
  ensures IsConsole(x, consoleUrl) <==> (x != [] && StripOneSlash(x) == StripOneSlash(consoleUrl))
{
}

// Citation: tests "/setup failing shows no prompt", "redirecting to / shows no prompt", "redirecting to the setup flow shows a prompt", "answering 200 shows a prompt"; "on the in-cluster route, `/setup` and the `initial-setup` executor show no prompt" (§6 line 517)
lemma P5_SetupShowsNoPrompt(status: seq<Byte>, location: seq<Byte>)
  ensures SetupShowsNoPrompt(status, location) <==> (status == S500 || (status == S302 && !Contains(location, InitialSetupWord)))
{
  GlobIsContains(location, InitialSetupWord);
}

// Citation: tests "<script>: --help exits 0", "<script>: an unknown option exits 2" (T25 S1)
lemma P6_Options(args: seq<seq<Byte>>)
  ensures Options(args).Exit? ==> (Options(args).code == 0 || Options(args).code == 2)
  ensures (|args| >= 1 && (args[0] == HelpFlag || args[0] == HFlag)) ==> Options(args) == Exit(0)
  ensures (|args| >= 1 && args[0] != HelpFlag && args[0] != HFlag && args[0] != ReleaseFlag && args[0] != NamespaceFlag) ==> Options(args) == Exit(2)
  ensures (|args| == 1 && (args[0] == ReleaseFlag || args[0] == NamespaceFlag)) ==> Options(args) == Exit(2)
{
  OptionsLoopCodes(args, IversonDefault, IversonDefault);
  assert HelpFlag != ReleaseFlag && HelpFlag != NamespaceFlag;
  assert HFlag != ReleaseFlag && HFlag != NamespaceFlag;
}
