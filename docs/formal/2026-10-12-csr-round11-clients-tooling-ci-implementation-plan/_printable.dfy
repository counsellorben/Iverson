// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-12-csr-round11-clients-tooling-ci-implementation-plan.md, T4 S3
// Source: Iverson.Agents/Python/iverson_agent/__main__.py _printable and _TERMINAL_CONTROLS
// Replay base: 7d4669e5896c4df448b2d45c9a4abbf67926b688
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions docs/formal/2026-10-12-csr-round11-clients-tooling-ci-implementation-plan/_printable.dfy

// Python `str` is a sequence of code points.
newtype CodePoint = x: int | 0 <= x < 0x110000

// _TERMINAL_CONTROLS = re.compile(r"[\x00-\x08\x0b-\x1f\x7f-\x9f]")
// A single-character class of three code-point ranges, compiled without flags:
// a code point matches iff it lies in one of the ranges.
predicate TerminalControlsMatches(c: CodePoint)
{
  (0x00 <= c <= 0x08) || (0x0b <= c <= 0x1f) || (0x7f <= c <= 0x9f)
}

// re.Pattern.sub(repl, string) with count=0: scans left to right, replacing every
// non-overlapping match with repl. Every match of a one-character class is exactly
// one character (never empty), so the scan consumes one character per step: a
// matching character is replaced by repl, any other is copied unchanged.
function TerminalControlsSub(repl: seq<CodePoint>, s: seq<CodePoint>): seq<CodePoint>
{
  if |s| == 0 then []
  else if TerminalControlsMatches(s[0]) then repl + TerminalControlsSub(repl, s[1..])
  else [s[0]] + TerminalControlsSub(repl, s[1..])
}

// def _printable(text: str) -> str:
//     return _TERMINAL_CONTROLS.sub("", text)
function Printable(text: seq<CodePoint>): seq<CodePoint>
{
  TerminalControlsSub([], text)
}

// ---------------------------------------------------------------------------
// Helper lemmas

lemma SubAppend(repl: seq<CodePoint>, s: seq<CodePoint>, t: seq<CodePoint>)
  ensures TerminalControlsSub(repl, s + t) == TerminalControlsSub(repl, s) + TerminalControlsSub(repl, t)
  decreases |s|
{
  if |s| == 0 {
    assert s + t == t;
  } else {
    assert (s + t)[0] == s[0];
    assert (s + t)[1..] == s[1..] + t;
    SubAppend(repl, s[1..], t);
  }
}

lemma PrintableNoControls(text: seq<CodePoint>)
  ensures forall c :: c in Printable(text) ==> !TerminalControlsMatches(c)
  decreases |text|
{
  if |text| > 0 {
    PrintableNoControls(text[1..]);
  }
}

// ---------------------------------------------------------------------------
// Properties

// Citation: "it removes C0 controls except newline and tab, DEL, and C1 controls (`\x80`–`\x9f`)" (spec :63)
lemma P1_RemovesTerminalControls(text: seq<CodePoint>)
  ensures forall c :: c in Printable(text) ==> !((0x00 <= c <= 0x08) || (0x0b <= c <= 0x1f) || (0x7f <= c <= 0x9f))
{
  PrintableNoControls(text);
}

// Citation: "it removes C0 controls except newline and tab, DEL, and C1 controls (`\x80`–`\x9f`)" (spec :63)
lemma P2_KeepsEveryOtherCharacter(c: CodePoint)
  ensures !((0x00 <= c <= 0x08) || (0x0b <= c <= 0x1f) || (0x7f <= c <= 0x9f)) ==> Printable([c]) == [c]
{
  if !TerminalControlsMatches(c) {
    assert [c][1..] == [];
    assert TerminalControlsSub([], [c]) == [c] + TerminalControlsSub([], []);
  }
}

// Citation: "it removes C0 controls except newline and tab, DEL, and C1 controls (`\x80`–`\x9f`)" (spec :63)
lemma P3_WorksCharacterByCharacter(s: seq<CodePoint>, t: seq<CodePoint>)
  ensures Printable(s + t) == Printable(s) + Printable(t)
{
  SubAppend([], s, t);
}
