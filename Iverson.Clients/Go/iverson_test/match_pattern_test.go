package iverson_test

import (
	"encoding/json"
	"os"
	"path/filepath"
	"reflect"
	"testing"

	pb "github.com/iverson/clients/go/generated"
	"github.com/iverson/clients/go/iverson"
	"google.golang.org/protobuf/encoding/protojson"
)

// assertMatchesMatchPatternFixture compares the request's protojson against a checked-in
// golden fixture as parsed JSON, the same way TestPipelineBuild_MatchesGoldenFixture_Contract1 does.
func assertMatchesMatchPatternFixture(t *testing.T, req *pb.MatchPatternRequest, fixture string) {
	t.Helper()
	actualBytes, err := protojson.Marshal(req)
	if err != nil {
		t.Fatalf("protojson.Marshal: %v", err)
	}
	var actual map[string]interface{}
	if err := json.Unmarshal(actualBytes, &actual); err != nil {
		t.Fatalf("unmarshal actual: %v", err)
	}

	goldenBytes, err := os.ReadFile(filepath.Join("..", "..", "Common", "testdata", fixture))
	if err != nil {
		t.Fatalf("read golden fixture: %v", err)
	}
	var expected map[string]interface{}
	if err := json.Unmarshal(goldenBytes, &expected); err != nil {
		t.Fatalf("unmarshal golden fixture: %v", err)
	}

	if !reflect.DeepEqual(actual, expected) {
		t.Errorf("golden fixture mismatch:\n  actual:   %s\n  expected: %s", actualBytes, goldenBytes)
	}
}

// ── Cross-language golden-fixture contract ─────────────────────────────────
// Fixtures generated from the C# builder (the reference implementation), checked in at
// Iverson.Clients/Common/testdata/match-pattern-contract-{1,2}.json. The same call sequence,
// built here via Go's iverson.NewMatchPattern(...), must serialize to the same JSON structure.

func TestMatchPatternBuild_MatchesGoldenFixture_Contract1(t *testing.T) {
	req := iverson.NewMatchPattern("PatternDoc").
		Where("Marker", pb.SearchOperator_EQUALS, strVal("m1")).
		Not("Label", pb.SearchOperator_EQUALS, strVal("skip")).
		WithLogic(pb.SearchLogic_OR).
		PartitionBy("Label").
		OrderBy("Seq", false).
		OrderBy("Id", true).
		Pattern("A B+").
		Subset("AB", "A", "B").
		Define("B", "Seq > PREV(Seq)").
		Measure("n", "COUNT(*)").
		Measure("last_seq", "LAST(B.Seq)").
		RowsPerMatch(pb.RowsPerMatch_ALL_ROWS_SHOW_EMPTY).
		AfterMatch(pb.AfterMatchSkipKind_TO_FIRST, "B").
		Limit(100).
		Build("trace-1")

	assertMatchesMatchPatternFixture(t, req, "match-pattern-contract-1.json")
}

func TestMatchPatternBuild_MatchesGoldenFixture_Contract2(t *testing.T) {
	req := iverson.NewMatchPattern("VectorDoc").
		Chunks("Body").
		Pattern("A").
		Define("A", "SIMILARITY(text, 'refund') > 0.5").
		Build("")

	assertMatchesMatchPatternFixture(t, req, "match-pattern-contract-2.json")
}

// ── Focused builder facts ──────────────────────────────────────────────────

func TestMatchPatternChunks_SetsSourceAndChunkProperty(t *testing.T) {
	req := iverson.NewMatchPattern("VectorDoc").Chunks("Body").Build()

	if req.Source != pb.PatternRowSource_CHUNKS {
		t.Errorf("source = %v, want CHUNKS", req.Source)
	}
	if req.ChunkProperty != "Body" {
		t.Errorf("chunkProperty = %q, want %q", req.ChunkProperty, "Body")
	}
}

func TestMatchPatternBuild_LeavesAfterMatchAndLimitUnsetByDefault(t *testing.T) {
	req := iverson.NewMatchPattern("PatternDoc").OrderBy("Seq", false).Pattern("A").Build()

	if req.AfterMatch != nil {
		t.Errorf("afterMatch = %+v, want nil (server default PAST_LAST_ROW)", req.AfterMatch)
	}
	if req.Limit != 0 {
		t.Errorf("limit = %d, want 0 (server default)", req.Limit)
	}
}

func TestMatchPatternAfterMatch_WithoutVariable_SetsKindOnly(t *testing.T) {
	req := iverson.NewMatchPattern("PatternDoc").AfterMatch(pb.AfterMatchSkipKind_TO_NEXT_ROW).Build()

	if req.AfterMatch == nil || req.AfterMatch.Kind != pb.AfterMatchSkipKind_TO_NEXT_ROW || req.AfterMatch.Variable != "" {
		t.Errorf("afterMatch = %+v, want {TO_NEXT_ROW, \"\"}", req.AfterMatch)
	}
}

func TestMatchPatternBuild_DefaultTraceIdIsEmpty(t *testing.T) {
	req := iverson.NewMatchPattern("PatternDoc").Build()

	if req.TraceId != "" {
		t.Errorf("traceId = %q, want empty", req.TraceId)
	}
}
