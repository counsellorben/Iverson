package iverson_test

import (
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
	Id          string    `iverson:"key"`
	Title       string
	Body        string    `iverson:"large_field"`
	Category    string    `iverson:"search_key:0"`
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

	// Expect 6 non-relation fields: Id, Title, Body, Category, WordCount, PublishedAt
	if len(meta.Fields) != 6 {
		t.Errorf("expected 6 fields, got %d: %+v", len(meta.Fields), meta.Fields)
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
