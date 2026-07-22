# Iverson MCP Server — Design

**Goal:** let an AI agent (Claude Desktop, Claude Code, or any other MCP client) query and write Iverson data conversationally, using the same tenant/row/field authorization the gRPC API already enforces for every other caller.

**Capability scope:** read + write data — Search, GroupBy, Aggregate, Pipeline, SearchSimilar, SearchChunks, Get, Post, Update, Delete. Explicitly excludes schema registration and admin operations (reconcile, DLQ, tenant lifecycle) — those are a larger trust boundary than this design takes on.

---

## Architecture

Two-phase project:

- **Phase A** extends `@iverson/client` (`Iverson.Clients/TypeScript/`) with real execution support for the six search-family RPCs (Search/GroupBy/Aggregate/Pipeline/SearchSimilar/SearchChunks), which the client currently only builds requests for but never calls, and with acting-user-header support across every RPC — a gap that affects any TS client caller today, not just this MCP server.
- **Phase B** adds a new sibling package, `Iverson.Clients/McpServer/`, depending on `@iverson/client` via `file:../TypeScript`. It's a stdio MCP server: one process, one acting-user identity, one deployer-supplied entity module.

```
Iverson.Clients/
  DotNet/ Go/ Java/ Python/ TypeScript/   (existing)
  McpServer/                              (new)
    package.json   (@iverson/client via file:../TypeScript, @modelcontextprotocol/sdk)
    tsconfig.json
    src/
      index.ts       — stdio server bootstrap
      config.ts       — env var parsing
      entities.ts     — loads the deployer's @IversonEntity classes
      tools.ts        — registers the 10 generic MCP tools
    README.md         — setup + Claude Desktop/Code config snippet
```

---

## Phase A: `@iverson/client` additions

### A1. Make the package consumable as a dependency

Today `@iverson/client`'s `package.json` has no `main`/`exports`/`types` field and no `build` script — it's only ever imported via relative source paths within its own folder (`sample/main.ts` imports `../src/core.js` directly). `Iverson.Clients/McpServer`'s `"@iverson/client": "file:../TypeScript"` dependency needs a real entry point to resolve against. Add:

- `"main": "dist/index.js"`, `"types": "dist/index.d.ts"`, an `"exports"` field
- `"build": "tsc"` script

### A2. Acting-user metadata support (all RPCs, not just search)

`callUnary` (`core.ts`) hardcodes `new grpc.Metadata()` on every call — there is currently no way for any caller (`IversonClient`, `EntityCoordinator`, or the new search methods) to attach the `x-acting-user-authorization` header, even though `createActingUserMetadata` already exists in `auth.ts` to build it. This is a pre-existing gap in the TS client, not something specific to MCP.

`IversonClient`'s constructor gains an optional acting-user token source (a string or a `() => Promise<string>` provider, mirroring the shape of the `dataPlaneTokenProvider` parameter recently added to the .NET client's `AddIversonClient`). Every call site — the existing `persist`/`update`/`delete`/`get`/`getMany` and the six new search-family methods — merges this into the call's metadata via `createActingUserMetadata` when configured.

### A3. Search-family execution

Six new methods on `IversonClient`, each taking an already-built request (from the existing `QueryBuilder`/`GroupByBuilder`/`PipelineBuilder`/`SimilarBuilder`/`ChunksBuilder`, none of which change) and executing it against the (also new) `ObjectSearchServiceClient`:

- `search`, `groupBy`, `pipeline`, `searchSimilar` — all stream `SearchResponse{data: Struct, score, trace_id}`. Share one conversion helper reusing the existing `payloadToEntity` pattern; each takes an optional entity class for typed conversion, defaulting to plain `Record<string, unknown>` when omitted.
- `aggregate` — unary, returns the typed `AggregateResponse` (buckets/metrics) as-is; no Struct conversion applies.
- `searchChunks` — streams `ChunkSearchResponse{parent_key, chunk_text, score}` as-is; no Struct conversion applies.

### A4. Export the auth helpers

`createOAuth2ClientCredentials` and `createActingUserMetadata` (`auth.ts`) are implemented but missing from `index.ts`'s public exports — add them.

### A5. Share the entity-reflection logic

`SchemaRegistrar._buildRequest` (private) already reflects an `@IversonEntity` class's fields/keys/relations into a `TypeDescriptor`-shaped structure. Promote that extraction into a small shared helper both `SchemaRegistrar` and the MCP server's entity loader call, rather than duplicating the reflection logic.

---

## Phase B: MCP server (`Iverson.Clients/McpServer`)

### Transport & process model

stdio transport (`@modelcontextprotocol/sdk`, v1.29.0+, ESM) — matches how Claude Desktop/Code configure local MCP servers, and fits a single-fixed-identity-per-process model.

### Configuration (environment variables)

| Var | Purpose |
|---|---|
| `IVERSON_GRPC_URL` | e.g. `http://localhost:8080` — same name/default convention as `Iverson.LoadTest` |
| `IVERSON_CLIENT_ID` / `IVERSON_CLIENT_SECRET` / `IVERSON_TOKEN_ENDPOINT` | OAuth2 client-credentials identity for the base gRPC channel (the `RequireAuthenticatedUser` fallback policy requires *some* authenticated identity here, and it must carry an audience in the default scheme's `ValidAudiences` list — e.g. `iverson-admin-automation`, the same client `Iverson.LoadTest` uses for its own base channel) |
| `IVERSON_ACTING_USER_TOKEN` | A pre-minted Authentik access token (obtained by running `Iverson.Server/deploy/scripts/mint_acting_user_token.py`), sent as the `x-acting-user-authorization` header on every data-plane call — this is what actually drives tenant/row/field authorization |
| `IVERSON_MCP_ENTITIES_PATH` | Path to a compiled JS module the deployer provides |

These two identities are **not interchangeable**: the base-channel credential and the acting-user token are validated against disjoint audience lists on the server (`Authentication:ValidAudiences` vs. `Authentication:ActingUser:ValidAudiences`), so both env-var groups are required.

There is no token-refresh mechanism in this design. When `IVERSON_ACTING_USER_TOKEN` expires, tool calls fail with a clear authentication error telling the user to re-run `mint_acting_user_token.py` and restart the server — the same manual refresh workflow already used for smoke testing.

### Entity loading

The module at `IVERSON_MCP_ENTITIES_PATH` default-exports an array of `@IversonEntity`-decorated classes:

```ts
// my-entities.js
export default [Article, Author, Tag];
```

At startup, the server validates each class is `@IversonEntity`-decorated (fails fast with a clear stderr message otherwise), then derives each type's field/key/relation descriptors via the shared reflection helper (Phase A5). No schema is registered with the server — this is purely local, used to build tool descriptions and construct requests. (There is no RPC to read back a type's already-registered schema; the deployer's local entity classes are the only source of truth the MCP server has.)

### Tool surface

Ten generic tools (not one per entity), each taking `type_name` (constrained to an enum of the loaded entity types) plus operation-specific args:

| Tool | Args | Calls |
|---|---|---|
| `get` | `type_name`, `key` | `EntityCoordinator.get` |
| `post` | `type_name`, `payload` (JSON object) | `EntityCoordinator.persist` |
| `update` | `type_name`, `payload` | `EntityCoordinator.update` |
| `delete` | `type_name`, `key` | `EntityCoordinator.delete` |
| `search` | `type_name`, `filters[]` (`{property, operator, value}`), `logic`, `sort[]`, `page`, `page_size`, `fields[]` | `IversonClient.search` |
| `group_by` | `type_name`, `filters[]`, `keys[]`, `metrics[]`, `having[]`, `order_by[]`, `limit` | `IversonClient.groupBy` |
| `aggregate` | `type_name`, `filters[]`, `aggregations[]`, `having[]` | `IversonClient.aggregate` |
| `pipeline` | `type_name`, `base_where[]`, `steps[]` | `IversonClient.pipeline` |
| `search_similar` | `type_name`, `property`, `query`, `top_k`, `filter[]` | `IversonClient.searchSimilar` |
| `search_chunks` | `type_name`, `property`, `query`, `top_k`, `filter[]` | `IversonClient.searchChunks` |

Design notes:
- Filter/value conversion reuses the existing `toSearchValue` helper (`search.ts`) rather than reinventing value-typing logic.
- Per-type field names aren't encoded in each tool's JSON schema (would require a `oneOf` per loaded type, unwieldy). Instead, each tool's `description` includes a rendered summary of the loaded types and their fields, giving the agent that context without an overly rigid schema.
- `pipeline` is by far the most complex tool — its `steps[]` shape mirrors `PipelineStep` closely (each step can carry windows, group-by+metrics, or joins). Kept in scope per explicit decision, but flagged as the largest, most fiddly tool to implement and get right.

### No local authorization filtering

The MCP server does not maintain its own copy of any type's `AuthorizationRules` and does not pre-filter which fields/types it advertises based on the caller's role. `AuthorizationRules` are supplied out-of-band at schema-registration time by the real application (see `Iverson.LoadTest/Program.cs`'s `BuildAuthorizationRules` for the established pattern) and there is no RPC to read them back — so any local copy would be guesswork, not authoritative. The server already enforces row/field authorization on every call regardless of what the MCP server advertises; a disallowed call surfaces as a normal tool error, same as any other client.

### Error handling

- gRPC errors from any tool call are caught and returned as MCP tool errors (not process crashes) — the agent sees the message and can retry.
- Entity-loading problems fail fast at startup, not lazily on first tool call.
- Acting-user token expiry surfaces as a clear tool error pointing at `mint_acting_user_token.py`.

### Testing

- Phase A: unit tests in the existing `tests/` vitest suite, following the established pattern (`tests/schema-registrar.test.ts`'s style of mocking the generated gRPC service client with `vi.fn()`).
- Phase B: unit tests for pure logic — entity-loading/validation, JSON-args-to-proto-request conversion. The stdio MCP protocol handling and live gRPC calls are integration-level, exercised manually against a running Iverson stack (docker-compose) — the same testing shape `Iverson.LoadTest` already uses (no automated suite; verified by running it).

---

## Explicitly out of scope

- Schema registration from the MCP server
- Admin operations — reconcile, DLQ, tenant lifecycle
- Acting-user token refresh / interactive login flow (a pre-minted token via the existing script is the answer for this design)
- Local authorization-rule filtering (the server enforces it; the MCP server doesn't duplicate it)
- HTTP/SSE transport (stdio only)

---

## Verified assumptions

All verified against the current repository state (`main` at time of writing) before this spec was finalized:

| # | Assumption | Evidence |
|---|---|---|
| 1 | `core.ts` contains `IversonClient`/`EntityCoordinator`/`callUnary`/`SchemaRegistrar` as described, and `EntityCoordinator` already fully implements Get/Post/Update/Delete/GetMany | Full read of `Iverson.Clients/TypeScript/src/core.ts` |
| 2 | `createOAuth2ClientCredentials`/`createActingUserMetadata` exist in `auth.ts` but are missing from `index.ts`'s public exports | Read of `auth.ts` and `index.ts` |
| 3 | `generated/object_search.ts` exports `ObjectSearchServiceClient` with `search`/`searchSimilar`/`groupBy`/`pipeline` as streaming calls and `aggregate` as unary-callback, same construction pattern as the three already-wired clients | Read of `generated/object_search.ts:4320-4368`, compared against `generated/object_mapping.ts` |
| 4 | `payloadToEntity` is generic/simple enough to reuse as-is for search-family response conversion, no extra per-field handling needed | Full read of `payloadToEntity`, `core.ts:231-242` |
| 5 | `callUnary` hardcodes empty `grpc.Metadata()` on every call — no acting-user injection point exists today, for any RPC | Read of `callUnary`, `core.ts` |
| 6 | `@iverson/client`'s `package.json` has no `main`/`exports`/`types`/`build` script — not set up to be depended on via `file:` today | Full read of `package.json`; confirmed `sample/main.ts` imports via relative source path, not the package name |
| 7 | The acting-user token (minted via `mint_acting_user_token.py` against the `iverson-loadtest-human` OAuth2 client, audience `dev-iverson-loadtest-human-client-id`) cannot double as the base-channel credential — the default JWT scheme's `ValidAudiences` (`dev-iverson-human-oidc-client-id`, `dev-iverson-loadtest-client-id`, `dev-iverson-webtest-client-id`, `dev-iverson-admin-automation-client-id`) and the `ActingUser` scheme's `ValidAudiences` (`dev-iverson-loadtest-human-client-id` only) are disjoint | `Iverson.Api/Program.cs:88-138` (two separate `AddJwtBearer` registrations), `docker-compose.yml:272-277` (the actual configured audience lists) |
| 8 | Only one existing call site constructs `IversonClient` — adding a new optional constructor parameter is a safe, non-breaking change | `grep -rn "new IversonClient("` across the repo — one hit, `sample/main.ts:12` |
| 9 | `@modelcontextprotocol/sdk` is a real, current package (v1.29.0), ESM-primary, compatible with this repo's existing `"type": "module"` TS toolchain | npm registry lookup |

Also inherited as ground truth from prior work in this codebase (not re-verified here): tenant/row/field authorization reads only the `x-acting-user-authorization` header, never the base channel identity (confirmed repeatedly across the mandatory-tenant-boundary and row/field-authorization initiatives); `AuthorizationRules` are supplied out-of-band at registration time, never attached to the entity class itself, in any language client (`Iverson.LoadTest/Program.cs:283-298`, `Iverson.Client.Core/SchemaRegistrar.cs:20`).
