# Chunk Collection Cleanup — Post Chunk-ID-Stability Fix

**Why:** Before the fix in `docs/superpowers/plans/2026-07-06-architectural-review-remediation.md`
(Task 1), chunk point IDs were derived using `string.GetHashCode()`, which .NET randomizes per
process. Every process restart caused subsequent chunk writes for existing articles to land under
new point IDs instead of overwriting the old ones, silently accumulating duplicate/stale passages
in every `{collection}_chunks` collection. There is no way to distinguish a legitimate current
chunk from an orphaned duplicate after the fact.

**Prerequisite:** the fix in Task 1 must already be deployed and running before you do this cleanup
— otherwise the rebuild will immediately start accumulating new duplicates again.

## Steps (per deployment, run once after Task 1 ships)

1. Identify every chunked collection: for each registered type with `ChunkFields.Count > 0`
   (query via `GET /health` won't show this — check `SchemaRegistry` state or simply enumerate
   `{collection}_chunks` for every entity type that has a `[IversonChunk]`-annotated property).
2. For each `{collection}_chunks` collection, drop it entirely:
   ```
   curl -X DELETE http://<qdrant-host>:6333/collections/{collection}_chunks
   ```
3. Trigger a full reconcile for every affected type, which replays every row from Postgres
   through the pipeline and re-creates the chunks collection fresh (with the now-stable IDs):
   ```
   curl -X POST http://<api-host>/admin/reconcile/{TypeName}
   ```
4. Verify: `SearchChunks` results for a known article should return exactly the expected number
   of passages (no duplicates) — spot-check 2-3 articles per type.

## Verifying no future recurrence

After this cleanup, chunk point IDs are stable (FNV-1a, no process-seed dependency) — a process
restart no longer duplicates existing chunks. Confirm no new duplicates appear after a deliberate
pod restart in a staging environment before considering this closed.
