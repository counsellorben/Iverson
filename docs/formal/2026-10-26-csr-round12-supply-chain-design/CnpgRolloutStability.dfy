// Spec: /home/ben/repositories/Iverson/docs/specs/2026-10-26-csr-round12-supply-chain-design.md @ f6f5db2cbea0425d938739f73a8d6e9147479682
// Source: §3 Operator-managed workloads, lines 124-124
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-26-csr-round12-supply-chain-design/CnpgRolloutStability.dfy
// Round: 1

datatype Option<T> = None | Some(value: T)
datatype Ref = Ref(repo: string, tag: string, digest: Option<string>)

// The webhook (line 117, VA 45): a tag-only image is rewritten to tag@digest;
// an image already carrying a digest is written back unchanged (line 124).
function Rewrite(r: Ref, resolved: string): Ref
{
  if r.digest.Some? then r else Ref(r.repo, r.tag, Some(resolved))
}

// CNPG's two exact comparisons (line 124): an instance's stored image string
// vs its target (the Cluster's imageName), and the stored bootstrap-controller
// init-container image vs OPERATOR_IMAGE_NAME (the operator's env, the chart's image.tag).
predicate NeedsRollout(pgTarget: Ref, opImage: Ref, storedPg: Ref, storedInit: Ref)
{
  storedPg != pgTarget || storedInit != opImage
}

// Citation: "which the webhook writes back unchanged" … "the operator's env and the init container then carry the `tag@digest` string the webhook writes back unchanged" (line 124)
lemma P1_PinnedImagesNoRollout(pg: Ref, op: Ref, r1: string, r2: string)
  requires pg.digest.Some? && op.digest.Some?
  ensures !NeedsRollout(pg, op, Rewrite(pg, r1), Rewrite(op, r2))
{
}

// Citation: "the rewrite reaches every image in the namespace, and CNPG rolls an instance whose stored image string differs from its target" (line 124)
lemma P2_TagOnlyPostgresRollsOut(pg: Ref, op: Ref, r1: string, r2: string)
  requires pg.digest.None?
  ensures NeedsRollout(pg, op, Rewrite(pg, r1), Rewrite(op, r2))
{
}

// Citation: "CNPG also compares each instance's `bootstrap-controller` init-container image with its `OPERATOR_IMAGE_NAME`" (line 124)
lemma P3_TagOnlyOperatorRollsOut(pg: Ref, op: Ref, r1: string, r2: string)
  requires op.digest.None?
  ensures NeedsRollout(pg, op, Rewrite(pg, r1), Rewrite(op, r2))
{
}
