package iverson_test

import (
	"strings"
	"testing"
	"time"

	"github.com/iverson/clients/go/iverson"
)

// ── ParseTag tests (relation kinds + untagged) ──────────────────────────────────

func TestParseTag_Empty(t *testing.T) {
	fm, err := iverson.ParseTag("Title", "")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if fm.RelationKind != "" {
		t.Errorf("expected empty relation kind, got %q", fm.RelationKind)
	}
	if fm.IsKey || fm.IsSearchKey || fm.IsLargeField || fm.IsEmbedding || fm.IsChunk {
		t.Errorf("expected no scalar flags set for an untagged field, got %+v", fm)
	}
	if fm.Name != "Title" {
		t.Errorf("expected Name=Title, got %q", fm.Name)
	}
}

func TestParseTag_ManyToOne(t *testing.T) {
	fm, err := iverson.ParseTag("AuthorId", "many_to_one:Author")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if fm.RelationKind != iverson.KindManyToOne {
		t.Errorf("expected relation kind=%q, got %q", iverson.KindManyToOne, fm.RelationKind)
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
		if fm.RelationKind != tc.kind {
			t.Errorf("tag=%q: expected relation kind=%q, got %q", tc.tag, tc.kind, fm.RelationKind)
		}
	}
}

func TestParseTag_UnknownKind(t *testing.T) {
	_, err := iverson.ParseTag("Field", "unknown_kind")
	if err == nil {
		t.Error("expected error for unknown kind, got nil")
	}
}

func TestParseTag_RelationMissingType(t *testing.T) {
	_, err := iverson.ParseTag("Field", "many_to_one")
	if err == nil {
		t.Error("expected error for relation without type, got nil")
	}
}

// ── InspectType tests: scalar tag keys ──────────────────────────────────────────
//
// The five scalar declarations are read at the InspectType assembly point, not
// via ParseTag, so their coverage lives here rather than in the ParseTag tests
// above.

type keyFixture struct {
	Id       string `iverson_key:"true"`
	TenantId string `iverson_tenant:"true"`
}

func TestInspectType_Key(t *testing.T) {
	meta, err := iverson.InspectType(keyFixture{})
	if err != nil {
		t.Fatalf("InspectType: %v", err)
	}
	var fm iverson.FieldMeta
	for _, f := range meta.Fields {
		if f.Name == "Id" {
			fm = f
		}
	}
	if !fm.IsKey {
		t.Error("expected IsKey=true")
	}
}

type searchKeyFixture struct {
	Id          string    `iverson_key:"true"`
	TenantId    string    `iverson_tenant:"true"`
	Category    string    `iverson_search_key:"0"`
	PublishedAt time.Time `iverson_search_key:"1"`
}

func TestInspectType_SearchKey(t *testing.T) {
	meta, err := iverson.InspectType(searchKeyFixture{})
	if err != nil {
		t.Fatalf("InspectType: %v", err)
	}
	byName := map[string]iverson.FieldMeta{}
	for _, f := range meta.Fields {
		byName[f.Name] = f
	}
	cat := byName["Category"]
	if !cat.IsSearchKey {
		t.Error("expected Category IsSearchKey=true")
	}
	if cat.SearchKeyOrder != 0 {
		t.Errorf("expected order=0, got %d", cat.SearchKeyOrder)
	}

	pub := byName["PublishedAt"]
	if !pub.IsSearchKey {
		t.Error("expected PublishedAt IsSearchKey=true")
	}
	if pub.SearchKeyOrder != 1 {
		t.Errorf("expected order=1, got %d", pub.SearchKeyOrder)
	}
}

type largeFieldFixture struct {
	Id       string `iverson_key:"true"`
	TenantId string `iverson_tenant:"true"`
	Body     string `iverson_large_field:"true"`
}

func TestInspectType_LargeFieldKey(t *testing.T) {
	meta, err := iverson.InspectType(largeFieldFixture{})
	if err != nil {
		t.Fatalf("InspectType: %v", err)
	}
	var fm iverson.FieldMeta
	for _, f := range meta.Fields {
		if f.Name == "Body" {
			fm = f
		}
	}
	if !fm.IsLargeField {
		t.Error("expected IsLargeField=true")
	}
}

type embeddingFixture struct {
	Id       string `iverson_key:"true"`
	TenantId string `iverson_tenant:"true"`
	Title    string `iverson_embedding:"true"`
}

func TestInspectType_Embedding(t *testing.T) {
	meta, err := iverson.InspectType(embeddingFixture{})
	if err != nil {
		t.Fatalf("InspectType: %v", err)
	}
	var fm iverson.FieldMeta
	for _, f := range meta.Fields {
		if f.Name == "Title" {
			fm = f
		}
	}
	if !fm.IsEmbedding {
		t.Error("expected IsEmbedding=true")
	}
}

type chunkDefaultsFixture struct {
	Id       string `iverson_key:"true"`
	TenantId string `iverson_tenant:"true"`
	Summary  string `iverson_chunk:"true"`
}

func TestInspectType_Chunk_Defaults(t *testing.T) {
	meta, err := iverson.InspectType(chunkDefaultsFixture{})
	if err != nil {
		t.Fatalf("InspectType: %v", err)
	}
	var fm iverson.FieldMeta
	for _, f := range meta.Fields {
		if f.Name == "Summary" {
			fm = f
		}
	}
	if !fm.IsChunk {
		t.Error("expected IsChunk=true")
	}
	if fm.ChunkMaxTokens != 512 {
		t.Errorf("expected default ChunkMaxTokens=512, got %d", fm.ChunkMaxTokens)
	}
	if fm.ChunkOverlap != 64 {
		t.Errorf("expected default ChunkOverlap=64, got %d", fm.ChunkOverlap)
	}
}

type chunkCustomFixture struct {
	Id       string `iverson_key:"true"`
	TenantId string `iverson_tenant:"true"`
	Summary  string `iverson_chunk:"256:32"`
}

func TestInspectType_Chunk_CustomParams(t *testing.T) {
	meta, err := iverson.InspectType(chunkCustomFixture{})
	if err != nil {
		t.Fatalf("InspectType: %v", err)
	}
	var fm iverson.FieldMeta
	for _, f := range meta.Fields {
		if f.Name == "Summary" {
			fm = f
		}
	}
	if fm.ChunkMaxTokens != 256 {
		t.Errorf("expected ChunkMaxTokens=256, got %d", fm.ChunkMaxTokens)
	}
	if fm.ChunkOverlap != 32 {
		t.Errorf("expected ChunkOverlap=32, got %d", fm.ChunkOverlap)
	}
}

type searchKeyBadOrderFixture struct {
	Id       string `iverson_key:"true"`
	TenantId string `iverson_tenant:"true"`
	Field    string `iverson_search_key:"abc"`
}

func TestInspectType_SearchKeyBadOrder(t *testing.T) {
	_, err := iverson.InspectType(searchKeyBadOrderFixture{})
	if err == nil {
		t.Fatal("expected error for non-integer search_key order, got nil")
	}
	if !strings.Contains(err.Error(), "Field") {
		t.Errorf("expected error to name the field, got %q", err.Error())
	}
}

// ── InspectType tests: general shape ────────────────────────────────────────────

type articleFixture struct {
	Id          string `iverson_key:"true"`
	TenantId    string `iverson_tenant:"true"`
	Title       string
	Body        string `iverson_large_field:"true"`
	Category    string `iverson_search_key:"0"`
	WordCount   int
	PublishedAt time.Time `iverson_search_key:"1"`
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
		if meta.Fields[i].IsKey {
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
		if f.IsSearchKey {
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
			if !f.IsLargeField {
				t.Errorf("Body should be IsLargeField=true, got %v", f.IsLargeField)
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
	if rel.RelationKind != iverson.KindManyToOne {
		t.Errorf("expected relation kind=many_to_one, got %q", rel.RelationKind)
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
	Id       string `iverson_key:"true" iverson_desc:"The unique identifier"`
	TenantId string `iverson_tenant:"true"`
	Status   string `iverson_meta:"true" iverson_desc:"Publication status"`
	Region   string `iverson_search_key:"0" iverson_meta:"true" iverson_desc:"Publication region."`
	Plain    string `iverson_desc:"A plain field"`
	Untagged string
}

// Metadata is an independent tag key, so it composes with a scalar declaration —
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
	if !region.IsSearchKey {
		t.Errorf("expected Region IsSearchKey=true")
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
	if !byName["Id"].IsKey {
		t.Errorf("key field IsKey changed: %v", byName["Id"].IsKey)
	}
	if !byName["Status"].Metadata {
		t.Error("expected Status Metadata=true")
	}
	if byName["Status"].IsKey || byName["Status"].IsSearchKey || byName["Status"].IsLargeField ||
		byName["Status"].IsEmbedding || byName["Status"].IsChunk || byName["Status"].RelationKind != "" {
		t.Errorf("metadata tag must not set any scalar flag or relation kind: %+v", byName["Status"])
	}
	if got := byName["Status"].Description; got != "Publication status" {
		t.Errorf("metadata field description: got %q", got)
	}
	if got := byName["Plain"].Description; got != "A plain field" {
		t.Errorf("plain field description: got %q", got)
	}
	if byName["Plain"].IsKey || byName["Plain"].IsSearchKey || byName["Plain"].IsLargeField ||
		byName["Plain"].IsEmbedding || byName["Plain"].IsChunk || byName["Plain"].RelationKind != "" {
		t.Errorf("expected no scalar flags for desc-only field, got %+v", byName["Plain"])
	}
	if got := byName["Untagged"].Description; got != "" {
		t.Errorf("expected empty description, got %q", got)
	}
}

// A tenant marker on a relation field is not a tenant declaration: relations
// never reach meta.Fields, which is where the registrar looks the tenant field
// up, so accepting it would put an empty TenantField on the wire.
type relationTenantFixture struct {
	Id       string `iverson_key:"true"`
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

// ── Declarations the server silently discards on the key field ───────────────

type metadataOnKeyFixture struct {
	Id       string `iverson_key:"true" iverson_meta:"true"`
	TenantId string `iverson_tenant:"true"`
}

type summaryOnKeyFixture struct {
	Id       string `iverson_key:"true" iverson_summary:"true"`
	TenantId string `iverson_tenant:"true"`
}

type multiDeclarationKeyFixture struct {
	Id       string `iverson_key:"true" iverson_search_key:"0" iverson_large_field:"true" iverson_embedding:"true" iverson_chunk:"true" iverson_meta:"true" iverson_summary:"true" iverson_keywords:"true" iverson_extract:"hint"`
	TenantId string `iverson_tenant:"true"`
}

type describedKeyFixture struct {
	Id       string `iverson_key:"true" iverson_desc:"Stable identifier."`
	TenantId string `iverson_tenant:"true"`
}

func TestInspectType_MetadataOnKeyRejected(t *testing.T) {
	_, err := iverson.InspectType(metadataOnKeyFixture{})
	if err == nil {
		t.Fatal("expected an error for iverson_meta on the key field")
	}
	if !strings.Contains(err.Error(), "metadataOnKeyFixture.Id is the primary key and also declares") {
		t.Errorf("error must name the type and field: %q", err.Error())
	}
	if !strings.Contains(err.Error(), "iverson_meta") {
		t.Errorf("error must name the offending declaration: %q", err.Error())
	}
	if !strings.Contains(err.Error(), "silently discarded") {
		t.Errorf("error must explain why it is dropped: %q", err.Error())
	}
}

func TestInspectType_SummaryOnKeyRejected(t *testing.T) {
	_, err := iverson.InspectType(summaryOnKeyFixture{})
	if err == nil {
		t.Fatal("expected an error for iverson_summary on the key field")
	}
	if !strings.Contains(err.Error(), "summaryOnKeyFixture.Id is the primary key and also declares") {
		t.Errorf("error must name the type and field: %q", err.Error())
	}
	if !strings.Contains(err.Error(), "iverson_summary") {
		t.Errorf("error must name the offending declaration: %q", err.Error())
	}
	if !strings.Contains(err.Error(), "silently discarded") {
		t.Errorf("error must explain why it is dropped: %q", err.Error())
	}
}

func TestInspectType_KeyErrorNamesEveryRejectedDeclaration(t *testing.T) {
	_, err := iverson.InspectType(multiDeclarationKeyFixture{})
	if err == nil {
		t.Fatal("expected an error for multiple declarations on the key field")
	}
	for _, want := range []string{
		"iverson_search_key", "iverson_large_field", "iverson_embedding", "iverson_chunk",
		"iverson_meta", "iverson_summary", "iverson_keywords", "iverson_extract",
	} {
		if !strings.Contains(err.Error(), want) {
			t.Errorf("error must name %s in one message, got %q", want, err.Error())
		}
	}
}

func TestInspectType_DescriptionOnKeyAccepted(t *testing.T) {
	meta, err := iverson.InspectType(describedKeyFixture{})
	if err != nil {
		t.Fatalf("a description on the key must stay legal: %v", err)
	}
	for _, fm := range meta.Fields {
		if fm.Name == "Id" {
			if !fm.IsKey || fm.Description != "Stable identifier." {
				t.Errorf("key field lost its declarations: %+v", fm)
			}
			return
		}
	}
	t.Fatal("key field missing from meta.Fields")
}
