/**
 * Reads the OIDC `groups` claim off a token profile.
 *
 * **Why this is a helper and not `profile.groups.includes(name)` at each call site.**
 * Two call sites ask "is this user in that group?" — the `RequireGroup` route guard and the
 * sidebar's conditional nav items — and they are the same question, so they must not be able
 * to drift into two different answers. They also mirror a third, server-side answer:
 * `OperatorAuthorizationPolicy.IsSatisfiedBy` / `TenantAdminAuthorizationPolicy` in
 * `Iverson.Api`, which read `context.User.FindAll("groups")` — a sequence of individual claim
 * VALUES — and call `Enumerable.Contains`, i.e. exact whole-string equality per group name.
 * This helper is the browser's copy of that rule and matches the same way.
 *
 * **The claim's shape, established from the issuer.** Authentik's `groups` scope mapping
 * (`Iverson.Server/deploy/helm/iverson/charts/authentik/.../service-clients.yaml`) evaluates
 * `return {"groups": [group.name for group in user.ak_groups.all()]}` — a Python list of names,
 * which Authentik serialises into the token as a JSON ARRAY of strings. So `profile.groups`
 * arrives here as `string[]`, and `.includes("operators")` on it is genuinely exact matching.
 *
 * **Why the string branch below still exists.** `.includes` on an array is exact; `.includes`
 * on a STRING is substring matching, under which a member of `non-operators` would satisfy a
 * check for `operators` — a privilege escalation from a one-character difference in the claim's
 * encoding. Some issuers (and some Authentik reconfigurations) emit `groups` space-joined, and
 * an OIDC profile claim is typed `unknown`, so nothing in the type system would catch the swap.
 * Normalising to a list FIRST and matching whole elements makes both encodings safe, so this
 * guard cannot be broken by a change made in the identity provider.
 */

/** Splits a delimited claim string. Space-joined is the common form; commas are tolerated. */
const CLAIM_DELIMITERS = /[\s,]+/;

/**
 * Normalises whatever the `groups` claim turned out to be into a list of group names.
 * Anything that is neither an array nor a string — absent, null, a number, an object —
 * yields no groups, so an unrecognised claim shape fails CLOSED.
 */
export function normalizeGroups(claim: unknown): string[] {
  if (Array.isArray(claim)) {
    return claim
      .filter((entry): entry is string => typeof entry === "string")
      .map((entry) => entry.trim())
      .filter((entry) => entry.length > 0);
  }

  if (typeof claim === "string") {
    return claim.split(CLAIM_DELIMITERS).filter((entry) => entry.length > 0);
  }

  return [];
}

/**
 * True when the token profile carries `group` as a WHOLE group name. Takes the profile as
 * `unknown` because `oidc-client-ts` types every profile claim through an index signature —
 * there is no narrower type available at the call sites, and a missing or malformed profile
 * must answer `false` rather than throw.
 */
export function hasGroup(profile: unknown, group: string): boolean {
  if (typeof profile !== "object" || profile === null) {
    return false;
  }

  return normalizeGroups((profile as { groups?: unknown }).groups).includes(group);
}
