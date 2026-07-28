package iverson_test

import (
	"strings"
	"testing"
	"time"

	"github.com/iverson/clients/go/iverson"
)

// ── ParseTag tests ─────────────────────────────────────────────────────────────

func TestParseTag_Empty(t *testing.T) {
	fm, err := iverson.ParseTag("Title", "")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if fm.Kind != "" {
		t.Errorf("expected empty kind, got %q", fm.Kind)
	}
	if fm.Name != "Title" {
		t.Errorf("expected Name=Title, got %q", fm.Name)
	}
}

func TestParseTag_Key(t *testing.T) {
	fm, err := iverson.ParseTag("Id", "key")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if fm.Kind != iverson.KindKey {
		t.Errorf("expected kind=%q, got %q", iverson.KindKey, fm.Kind)
	}
}

func TestParseTag_SearchKey(t *testing.T) {
	fm, err := iverson.ParseTag("Category", "search_key:0")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if fm.Kind != iverson.KindSearchKey {
		t.Errorf("expected kind=%q, got %q", iverson.KindSearchKey, fm.Kind)
	}
	if fm.SearchKeyOrder != 0 {
		t.Errorf("expected order=0, got %d", fm.SearchKeyOrder)
	}

	fm2, err := iverson.ParseTag("PublishedAt", "search_key:1")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if fm2.SearchKeyOrder != 1 {
		t.Errorf("expected order=1, got %d", fm2.SearchKeyOrder)
	}
}

func TestParseTag_LargeField(t *testing.T) {
	fm, err := iverson.ParseTag("Body", "large_field")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if fm.Kind != iverson.KindLargeField {
		t.Errorf("expected kind=%q, got %q", iverson.KindLargeField, fm.Kind)
	}
}

func TestParseTag_Embedding(t *testing.T) {
	fm, err := iverson.ParseTag("Title", "embedding")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if fm.Kind != iverson.KindEmbedding {
		t.Errorf("expected kind=%q, got %q", iverson.KindEmbedding, fm.Kind)
	}
}

func TestParseTag_Chunk_Defaults(t *testing.T) {
	fm, err := iverson.ParseTag("Summary", "chunk")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if fm.Kind != iverson.KindChunk {
		t.Errorf("expected kind=%q, got %q", iverson.KindChunk, fm.Kind)
	}
	if fm.ChunkMaxTokens != 512 {
		t.Errorf("expected default ChunkMaxTokens=512, got %d", fm.ChunkMaxTokens)
	}
	if fm.ChunkOverlap != 64 {
		t.Errorf("expected default ChunkOverlap=64, got %d", fm.ChunkOverlap)
	}
}

func TestParseTag_Chunk_CustomParams(t *testing.T) {
	fm, err := iverson.ParseTag("Summary", "chunk:256:32")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if fm.ChunkMaxTokens != 256 {
		t.Errorf("expected ChunkMaxTokens=256, got %d", fm.ChunkMaxTokens)
	}
	if fm.ChunkOverlap != 32 {
		t.Errorf("expected ChunkOverlap=32, got %d", fm.ChunkOverlap)
	}
}

func TestParseTag_ManyToOne(t *testing.T) {
	fm, err := iverson.ParseTag("AuthorId", "many_to_one:Author")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if fm.Kind != iverson.KindManyToOne {
		t.Errorf("expected kind=%q, got %q", iverson.KindManyToOne, fm.Kind)
	}
	if fm.RelatedType != "Author" {
		t.Errorf("expected RelatedType=Author, got %q", fm.RelatedType)
	}
}

func TestParseTag_AllRelationKinds(t *testing.T) {
	cases := []struct {
		tag  string
		kind string
	}{
		{"many_to_one:Author", iverson.KindManyToOne},
		{"many_to_many:Tag", iverson.KindManyToMany},
		{"one_to_many:Article", iverson.KindOneToMany},
		{"one_to_one:Profile", iverson.KindOneToOne},
	}
	for _, tc := range cases {
		fm, err := iverson.ParseTag("Field", tc.tag)
		if err != nil {
			t.Errorf("tag=%q: unexpected error: %v", tc.tag, err)
			continue
		}
		if fm.Kind != tc.kind {
			t.Errorf("tag=%q: expected kind=%q, got %q", tc.tag, tc.kind, fm.Kind)
		}
	}
}

func TestParseTag_UnknownKind(t *testing.T) {
	_, err := iverson.ParseTag("Field", "unknown_kind")
	if err == nil {
		t.Error("expected error for unknown kind, got nil")
	}
}

func TestParseTag_SearchKeyBadOrder(t *testing.T) {
	_, err := iverson.ParseTag("Field", "search_key:abc")
	if err == nil {
		t.Error("expected error for non-integer search_key order, got nil")
	}
}

func TestParseTag_RelationMissingType(t *testing.T) {
	_, err := iverson.ParseTag("Field", "many_to_one")
	if err == nil {
		t.Error("expected error for relation without type, got nil")
	}
}

// ── InspectType tests ─────────────────────────────────────────────────────────

type articleFixture struct {
	Id          string `iverson:"key"`
	TenantId    string `iverson_tenant:"true"`
	Title       string
	Body        string `iverson:"large_field"`
	Category    string `iverson:"search_key:0"`
	WordCount   int
	PublishedAt time.Time `iverson:"search_key:1"`
	AuthorId    string    `iverson:"many_to_one:Author"`
}

func TestInspectType_Fields(t *testing.T) {
	meta, err := iverson.InspectType(articleFixture{})
	if err != nil {
		t.Fatalf("InspectType: %v", err)
	}

	if meta.TypeName != "articleFixture" {
		t.Errorf("expected TypeName=articleFixture, got %q", meta.TypeName)
	}

	// Expect 7 non-relation fields: Id, TenantId, Title, Body, Category, WordCount, PublishedAt
	if len(meta.Fields) != 7 {
		t.Errorf("expected 7 fields, got %d: %+v", len(meta.Fields), meta.Fields)
	}

	// Expect 1 relation
	if len(meta.Relations) != 1 {
		t.Errorf("expected 1 relation, got %d", len(meta.Relations))
	}
}

func TestInspectType_KeyField(t *testing.T) {
	meta, err := iverson.InspectType(articleFixture{})
	if err != nil {
		t.Fatalf("InspectType: %v", err)
	}
	var keyField *iverson.FieldMeta
	for i := range meta.Fields {
		if meta.Fields[i].Kind == iverson.KindKey {
			keyField = &meta.Fields[i]
			break
		}
	}
	if keyField == nil {
		t.Fatal("expected a key field")
	}
	if keyField.Name != "Id" {
		t.Errorf("expected key field Name=Id, got %q", keyField.Name)
	}
}

func TestInspectType_SearchKeys(t *testing.T) {
	meta, err := iverson.InspectType(articleFixture{})
	if err != nil {
		t.Fatalf("InspectType: %v", err)
	}
	var keys []iverson.FieldMeta
	for _, f := range meta.Fields {
		if f.Kind == iverson.KindSearchKey {
			keys = append(keys, f)
		}
	}
	if len(keys) != 2 {
		t.Fatalf("expected 2 search keys, got %d", len(keys))
	}
	// Category is order 0, PublishedAt is order 1
	orders := map[string]int{}
	for _, k := range keys {
		orders[k.Name] = k.SearchKeyOrder
	}
	if orders["Category"] != 0 {
		t.Errorf("Category search_key order should be 0, got %d", orders["Category"])
	}
	if orders["PublishedAt"] != 1 {
		t.Errorf("PublishedAt search_key order should be 1, got %d", orders["PublishedAt"])
	}
}

func TestInspectType_LargeField(t *testing.T) {
	meta, err := iverson.InspectType(articleFixture{})
	if err != nil {
		t.Fatalf("InspectType: %v", err)
	}
	found := false
	for _, f := range meta.Fields {
		if f.Name == "Body" {
			if f.Kind != iverson.KindLargeField {
				t.Errorf("Body should be large_field, got %q", f.Kind)
			}
			found = true
		}
	}
	if !found {
		t.Error("Body field not found")
	}
}

func TestInspectType_Relations(t *testing.T) {
	meta, err := iverson.InspectType(articleFixture{})
	if err != nil {
		t.Fatalf("InspectType: %v", err)
	}
	if len(meta.Relations) != 1 {
		t.Fatalf("expected 1 relation, got %d", len(meta.Relations))
	}
	rel := meta.Relations[0]
	if rel.Name != "AuthorId" {
		t.Errorf("expected relation Name=AuthorId, got %q", rel.Name)
	}
	if rel.Kind != iverson.KindManyToOne {
		t.Errorf("expected kind=many_to_one, got %q", rel.Kind)
	}
	if rel.RelatedType != "Author" {
		t.Errorf("expected RelatedType=Author, got %q", rel.RelatedType)
	}
}

func TestInspectType_PointerAccepted(t *testing.T) {
	a := &articleFixture{}
	meta, err := iverson.InspectType(a)
	if err != nil {
		t.Fatalf("InspectType with pointer: %v", err)
	}
	if meta.TypeName != "articleFixture" {
		t.Errorf("expected TypeName=articleFixture, got %q", meta.TypeName)
	}
}

func TestInspectType_NonStruct(t *testing.T) {
	_, err := iverson.InspectType("not a struct")
	if err == nil {
		t.Error("expected error for non-struct type")
	}
}

// ── metadata / description tag tests ───────────────────────────────────────────

type descFixture struct {
	Id       string `iverson:"key" iverson_desc:"The unique identifier"`
	TenantId string `iverson_tenant:"true"`
	Status   string `iverson_meta:"true" iverson_desc:"Publication status"`
	Region   string `iverson:"search_key:0" iverson_meta:"true" iverson_desc:"Publication region."`
	Plain    string `iverson_desc:"A plain field"`
	Untagged string
}

// Metadata is an independent tag key, so it composes with an `iverson` kind —
// matching the server and the other four clients (cf. Python's
// test_metadata_composes_with_search_key).
func TestInspectType_MetadataComposesWithSearchKey(t *testing.T) {
	meta, err := iverson.InspectType(descFixture{})
	if err != nil {
		t.Fatalf("InspectType: %v", err)
	}
	var region iverson.FieldMeta
	for _, fm := range meta.Fields {
		if fm.Name == "Region" {
			region = fm
		}
	}
	if !region.Metadata {
		t.Error("expected Region Metadata=true")
	}
	if region.Kind != iverson.KindSearchKey {
		t.Errorf("expected Region kind=%q, got %q", iverson.KindSearchKey, region.Kind)
	}
	if region.SearchKeyOrder != 0 {
		t.Errorf("expected Region search key order 0, got %d", region.SearchKeyOrder)
	}
	if got := region.Description; got != "Publication region." {
		t.Errorf("Region description: got %q", got)
	}
}

func TestInspectType_DescriptionsAndMetadata(t *testing.T) {
	meta, err := iverson.InspectType(descFixture{})
	if err != nil {
		t.Fatalf("InspectType: %v", err)
	}
	byName := map[string]iverson.FieldMeta{}
	for _, fm := range meta.Fields {
		byName[fm.Name] = fm
	}

	// Description on the KEY field must be carried.
	if got := byName["Id"].Description; got != "The unique identifier" {
		t.Errorf("key field description: got %q", got)
	}
	if byName["Id"].Kind != iverson.KindKey {
		t.Errorf("key field kind changed: %q", byName["Id"].Kind)
	}
	if !byName["Status"].Metadata {
		t.Error("expected Status Metadata=true")
	}
	if byName["Status"].Kind != "" {
		t.Errorf("metadata tag must not set a kind, got %q", byName["Status"].Kind)
	}
	if got := byName["Status"].Description; got != "Publication status" {
		t.Errorf("metadata field description: got %q", got)
	}
	if got := byName["Plain"].Description; got != "A plain field" {
		t.Errorf("plain field description: got %q", got)
	}
	if byName["Plain"].Kind != "" {
		t.Errorf("expected empty kind for desc-only field, got %q", byName["Plain"].Kind)
	}
	if got := byName["Untagged"].Description; got != "" {
		t.Errorf("expected empty description, got %q", got)
	}
}

// A tenant marker on a relation field is not a tenant declaration: relations
// never reach meta.Fields, which is where the registrar looks the tenant field
// up, so accepting it would put an empty TenantField on the wire.
type relationTenantFixture struct {
	Id       string `iverson:"key"`
	AuthorId string `iverson:"many_to_one:Author" iverson_tenant:"true"`
}

func TestInspectType_TenantOnRelationRejected(t *testing.T) {
	_, err := iverson.InspectType(relationTenantFixture{})
	if err == nil {
		t.Fatal("expected the zero-marker error for a tenant tag on a relation field")
	}
	if !strings.Contains(err.Error(), "relationTenantFixture") {
		t.Errorf("expected error to name the type, got %q", err.Error())
	}
}
