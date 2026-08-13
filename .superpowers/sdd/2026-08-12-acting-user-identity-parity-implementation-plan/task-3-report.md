# Task 3 report: Python identity relocation

Commit: `610d28c025842c345ebd16cf294b94e9379e68d6` on branch `acting-user-identity-parity`
Worktree: `/home/ben/repositories/Iverson-serverids`

## What changed and where

### `Iverson.Clients/Python/iverson_client/core.py`

- Import block (`:14-19`): replaced `_ActingUserAuthPlugin` import with `ACTING_USER_METADATA_KEY`; added `import copy` (`:4`).
- `IversonClient.__init__` (`:739`): narrowed the channel-credentials guard from
  `if credentials is not None or acting_user_token is not None:` to `if credentials is not None:`.
  Removed the `if acting_user_token is not None: call_creds_list.append(grpc.metadata_call_credentials(_ActingUserAuthPlugin(...)))`
  block that previously composed the token onto channel credentials (was `:722`).
- `IversonClient.__init__` (`:765`): added `self._acting_user_token = acting_user_token` (the assignment the brief's
  code block omitted from its shown signature — required, per the task instructions, for anything to work).
- `IversonClient._acting_user_metadata` (`:767-770`, new): returns `()` when falsy, else the single
  `(ACTING_USER_METADATA_KEY, f"Bearer {token}")` tuple.
- `IversonClient.get_schema` (`:786-790`): now passes `metadata=self._acting_user_metadata()`.
- `IversonClient.coordinator` (`:782`): now threads `self._acting_user_token` into `EntityCoordinator(entity_class, self._channel, self._acting_user_token)`.
- `EntityCoordinator.__init__` (`:478-497`): gained optional third parameter `acting_user_token: str | None = None`
  and `self._acting_user_token = acting_user_token`.
- `EntityCoordinator.with_acting_user` (`:499-503`, new): `copy.copy(self)`, overwrite `_acting_user_token` on the
  copy, return it; receiver untouched.
- `EntityCoordinator._acting_user_metadata` (`:505-508`, new): same falsy-check pattern as the client's.
- All 14 coordinator stub call sites now pass `metadata=self._acting_user_metadata()`:
  `persist`/`Post` (`:527`), `update`/`Update` (`:542`), `delete`/`Delete` (`:555`), `get_mapped`/`Get` (`:570`),
  `post_mapped`/`Post` (`:586`), `update_mapped`/`Update` (`:600`), `get`/`Get` (`:614`), `get_many`/`GetMany` (`:629`),
  `search`/`Search` (`:643`), `search_similar`/`SearchSimilar` (`:652`), `search_chunks`/`SearchChunks` (`:657`),
  `group_by`/`GroupBy` (`:663`), `aggregate`/`Aggregate` (`:668`), `pipeline`/`Pipeline` (`:674`).

### `Iverson.Clients/Python/tests/test_auth.py`

- Added imports: `iverson_entity`, `iverson_key` from `iverson_client.annotations`; `object_retrieval_pb2 as retrieval_pb`;
  a small `CoordSchemaEntity` test entity.
- Replaced the three now-obsolete channel-credentials tests
  (`test_client_with_acting_user_token_only_uses_secure_channel_and_survives_first_call`,
  `test_client_with_use_tls_and_acting_user_token_uses_ssl_channel_credentials`,
  `test_client_without_use_tls_and_acting_user_token_uses_local_channel_credentials`) — these asserted
  behavior (`acting_user_token` alone triggering `secure_channel`/composed call-credentials) that Step 2
  explicitly removes, so they would fail against the new, intended behavior — with
  `test_client_with_acting_user_token_only_uses_insecure_channel`, confirming `acting_user_token` alone now
  falls through to the plain `insecure_channel` path.
- Added `test_get_schema_sends_exactly_one_acting_user_metadata_entry` (Step 1) and
  `test_coordinator_call_sends_exactly_one_acting_user_metadata_entry` (Step 5). Both capture any
  `grpc.metadata_call_credentials` plugin actually composed onto the channel (via the same
  capture-and-replay pattern the pre-existing tests used) *and* the `metadata=` kwarg the mocked stub
  received, then combine and assert exactly one `x-acting-user-authorization` entry total. This matters:
  an earlier draft of these tests mocked the stub directly and never exercised real channel construction,
  so a re-added `_ActingUserAuthPlugin` composition passed silently (see Mutation 1 below, first attempt).
  The final version invokes any captured plugin's callback to pull its metadata into the count, which
  correctly fails when the plugin is re-added.

### `Iverson.Clients/Python/tests/test_entity_coordinator.py`

- Added `object_retrieval_pb2 as retrieval_pb` import.
- Added `TestEntityCoordinatorActingUserIdentity` with four tests per Step 3:
  - `test_a_per_call_bound_token_takes_precedence_over_the_ambient_default`
  - `test_the_clients_ambient_identity_applies_when_nothing_is_bound`
  - `test_no_token_anywhere_emits_no_acting_user_header`
  - `test_with_acting_user_does_not_mutate_the_receiver`

## Step 4 mechanical verification

Line-scoped grep would false-negative on these multi-line calls, so verification used a small Python script
walking matched-paren spans starting from each call site, rather than a single regex (regex can't balance
nested parens). Head-matching regex used to enumerate call sites:

```
self\._(mapping|persistence|retrieval|search)\.[A-Za-z]+\(
```

Script (paraphrased): for each match, scan forward tracking paren depth to find the call's closing `)`,
then check `"metadata="` appears in that full span.

Output:
```
call sites found: 14
missing metadata=: []
```

All 14 call sites confirmed to carry `metadata=`.

## `python3 -m pytest -q` output (final, baseline)

```
........................................................................ [ 39%]
........................................................................ [ 78%]
........................................                                 [100%]
184 passed in 0.38s
```

## Mutation testing (Step 7)

All four mutations applied by hand-editing (no revert/checkout commands used), confirmed to fail the named
test, then hand-reverted to the exact original text.

| # | Mutation | Test(s) that failed | Assertion message |
|---|---|---|---|
| 1 | Re-added `_ActingUserAuthPlugin` composition at the (now-removed) `core.py:722` site, plus widened the guard back to `credentials is not None or acting_user_token is not None` (required for the mutation to actually be reachable) | `test_get_schema_sends_exactly_one_acting_user_metadata_entry`, `test_coordinator_call_sends_exactly_one_acting_user_metadata_entry` | `AssertionError: assert 2 == 1` — `+  where 2 = len([('x-acting-user-authorization', 'Bearer user-token-123'), ('x-acting-user-authorization', 'Bearer user-token-123')])` (both tests) |
| 2 | `get_schema` drops its `metadata=` argument | `test_get_schema_sends_exactly_one_acting_user_metadata_entry` | `KeyError: 'metadata'` at `client._mapping_stub.GetSchema.call_args.kwargs["metadata"]` |
| 3 | `EntityCoordinator._acting_user_metadata` returns `()` unconditionally | `test_the_clients_ambient_identity_applies_when_nothing_is_bound` (also failed `test_a_per_call_bound_token_takes_precedence_over_the_ambient_default`) | `AssertionError: assert () == (('x-acting-user-authorization', 'Bearer ambient-token'),)` — `Right contains one more item` |
| 4 | `with_acting_user` returns `self` instead of `bound` | `test_with_acting_user_does_not_mutate_the_receiver` (also failed `test_a_per_call_bound_token_takes_precedence_over_the_ambient_default`) | `assert <EntityCoordinator object at 0x...> is not <EntityCoordinator object at 0x...>` (same address both sides) |

All four mutations were killed by their named test (or a superset including it). After each, the file was
restored by hand to the exact pre-mutation text, and `python3 -m pytest -q` was re-run to confirm 184 passed
before moving to the next mutation.

## Concerns

- The brief listed `test_auth.py`/`test_entity_coordinator.py` as files to modify but didn't call out that
  three pre-existing tests in `test_auth.py` assert the exact channel-composition behavior Step 2 removes
  (`acting_user_token` alone → `secure_channel` + composed call-credentials). Left as-is they fail against
  the new intended behavior, so I replaced them with one test confirming the new fallback
  (`acting_user_token` alone → plain `insecure_channel`). This is a judgment call within task scope (the
  brief does say the guard narrows and the plugin composition goes away) but is worth a second look since
  it wasn't explicitly spelled out as "update these three tests."
  - `test_client_with_acting_user_token_only_uses_secure_channel_and_survives_first_call`
  - `test_client_with_use_tls_and_acting_user_token_uses_ssl_channel_credentials`
  - `test_client_without_use_tls_and_acting_user_token_uses_local_channel_credentials`
- No client-side validation of token shape/contents/expiry was added anywhere, per the global constraint —
  only the falsy/empty check in `_acting_user_metadata`.
- `auth.py` was left untouched except being read (the now-unused `_ActingUserAuthPlugin` class and the
  already-existing module-level `acting_user_metadata()` helper both remain, as instructed).

## Fix round 1

Coordinator flagged that removing the three obsolete `test_auth.py` tests had thrown away two live
regression guards along with the one that was genuinely obsolete. The `base_creds = ssl_channel_credentials()
if use_tls else local_channel_credentials()` branch in `core.py` (the whole-branch-review TLS fix) is still
fully reachable — Step 2 only changed which *argument* (`credentials=` instead of `acting_user_token=`) gets
a caller into that branch. The bug the two deleted tests guarded against (silently downgrading to unencrypted
`local_channel_credentials()` under `use_tls=True`) is unchanged and still live, so removing their guards was
a real regression in test coverage.

### Required fix

Restored both tests in `Iverson.Clients/Python/tests/test_auth.py`, re-expressed to enter the composite-
channel-credentials branch via `credentials=IversonClientCredentials(...)` (same construction
`test_client_with_credentials_uses_secure_channel` already uses) instead of the now-removed
`acting_user_token=` entry point:

- `test_client_with_use_tls_and_credentials_uses_ssl_channel_credentials` — asserts
  `captured["base_creds"] is ssl_sentinel` / `is not local_sentinel` under `use_tls=True`.
- `test_client_without_use_tls_and_credentials_uses_local_channel_credentials` — asserts the inverse under
  the `use_tls=False` default.

Docstrings preserve the original finding (whole-branch review; `use_tls=True` silently ignored; unencrypted
`local_channel_credentials()` used instead of `ssl_channel_credentials()`) and add a note that the entry
point moved from `acting_user_token=` to `credentials=` as a consequence of this initiative, so the next
reader isn't confused about why the test changed shape.

### Optional tidy — applied

Collapsed the dead inner `if credentials is not None:` in `core.py`'s `__init__` (redundant after Step 2's
outer-guard narrowing) — removed the inner guard, de-indented its body by one level. `core.py:739-745`.

### Test run after the fix

```
........................................................................ [ 38%]
........................................................................ [ 77%]
..........................................                               [100%]
186 passed in 0.30s
```

(186 = 184 from the original submission + 2 restored TLS tests.)

### Falsifiability check (by hand, no revert commands used)

Changed `core.py`'s `base_creds = grpc.ssl_channel_credentials() if use_tls else grpc.local_channel_credentials()`
to `... if False else ...` (always picks `local_channel_credentials()`, simulating the exact bug the guards
protect against). Ran the ssl test alone:

```
FAILED tests/test_auth.py::test_client_with_use_tls_and_credentials_uses_ssl_channel_credentials
AssertionError: assert <object object at 0x7e61853819f0> is <object object at 0x7e61853819d0>
  (captured["base_creds"] is ssl_sentinel failed — base_creds was local_sentinel instead)
```

Confirmed falsifiable. Restored `if use_tls` by hand (edited the literal text back, no `git checkout`/
`git restore`/other revert command), then re-ran the full suite: 186 passed.

### Commit

`907c0bb568ede2511e45f0f68603f2b37e5aa9a3` on top of `610d28c`, not amended. Files touched:
`Iverson.Clients/Python/iverson_client/core.py`, `Iverson.Clients/Python/tests/test_auth.py`.
