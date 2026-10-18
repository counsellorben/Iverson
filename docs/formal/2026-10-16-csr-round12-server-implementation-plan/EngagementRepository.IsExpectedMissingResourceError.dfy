// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T10 S3
// Source: Iverson.Server/Iverson.StarRocks/EngagementRepository.cs `IsExpectedMissingResourceError`
// Code ref: f7a0842fc0e707359e777ddf44f7c2e23050c968
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/EngagementRepository.IsExpectedMissingResourceError.dfy

// C# `string` (UTF-16 code units under --unicode-char false); Length is an int.
type CsString = s: seq<char> | |s| < 0x8000_0000

// Approved axiom: string.Contains(sub, StringComparison.OrdinalIgnoreCase), a library-boundary call.
predicate ContainsIgnoreCase(s: CsString, sub: seq<char>)

// The thrown exception: a MySqlConnector MySqlException (ErrorCode as its int value, Message
// non-nullable) or any other exception type.
datatype Ex = MySqlEx(code: int, message: CsString) | OtherEx

// EngagementRepository.cs:288-291
//   private static bool IsExpectedMissingResourceError(Exception ex) =>
//       ex is MySqlException mex &&
//       mex.ErrorCode == (MySqlErrorCode)5502 &&
//       mex.Message.Contains("Unknown table", StringComparison.OrdinalIgnoreCase);
predicate IsExpectedMissingResource(e: Ex)
{
  e.MySqlEx? &&
  e.code == 5502 &&
  ContainsIgnoreCase(e.message, "Unknown table")
}

// Citation: "`IsExpectedMissingResourceError` loses its role arm ("cannot find role", "is not granted to") and keeps its 5502 "Unknown table" arm." (spec §7.3)
lemma P1_AnUnknownTableIsStillAnExpectedEmptyTenant(m: CsString)
  requires ContainsIgnoreCase(m, "Unknown table")
  ensures IsExpectedMissingResource(MySqlEx(5502, m))
{
}

// Citation: "`IsExpectedMissingResourceError` loses its role arm ("cannot find role", "is not granted to") and keeps its 5502 "Unknown table" arm." (spec §7.3)
lemma P2_NothingElseIsAnExpectedEmptyTenant(e: Ex)
  requires IsExpectedMissingResource(e)
  ensures e.MySqlEx? && e.code == 5502 && ContainsIgnoreCase(e.message, "Unknown table")
{
}
