package iverson

// This is a white-box (package iverson) test file, unlike every other Go client test file
// (all of which live in the separate iverson_test/ directory as black-box tests using only
// exported API). It needs direct access to the unexported coordinatorDeps/
// newEntityCoordinatorWithDeps mock-injection scaffolding to test the 6 new search-family
// EntityCoordinator[T] methods against a mock SearchClient — Go's package visibility is
// scoped per import path (directory), so an external test package genuinely cannot reach
// unexported symbols regardless of naming convention, and _test.go files are only compiled
// into their own package's test binary, not into packages that merely import it. Exporting
// a test-only constructor just to keep this test in the external directory would widen the
// public API for no real benefit, so this test lives here instead.

import (
	"context"
	"errors"
	"io"
	"strings"
	"testing"

	pb "github.com/iverson/clients/go/generated"
	"google.golang.org/protobuf/types/known/structpb"
)

// ── Test entity type ─────────────────────────────────────────────────────────

type coordinatorArticle struct {
	Id       string `iverson_key:"true"`
	TenantId string `iverson_tenant:"true"`
	Category string
}

// ── Relation write/read fixtures ───────────────────────────────────────────────

type Article struct {
	Id       string `iverson_key:"true"`
	TenantId string `iverson_tenant:"true"`
	Title    string
	AuthorId string `iverson:"many_to_one:Author"`
}

type Author struct {
	Id       string `iverson_key:"true"`
	TenantId string `iverson_tenant:"true"`
	Name     string
	// []string mirrors sample/models/author.go:8, the real production one_to_many
	// declaration — a Go relation field holding structs is omitted as a nav
	// property by a separate type guard, so this keeps the KindOneToMany kind
	// check the only thing gating emission here.
	Articles []string `iverson:"one_to_many:Article"`
}

type WriterAuthor struct {
	Id       string `iverson_key:"true"`
	TenantId string `iverson_tenant:"true"`
	WriterId string `iverson:"many_to_one:Author"`
}

type Profile struct {
	Id       string `iverson_key:"true"`
	TenantId string `iverson_tenant:"true"`
	UserId   string `iverson:"one_to_one:User"`
}

type Tag struct {
	Id       string `iverson_key:"true"`
	TenantId string `iverson_tenant:"true"`
	Name     string
	Articles []string `iverson:"many_to_many:Article"`
}

func TestEntityToStruct_ManyToOne_WritesForeignKey(t *testing.T) {
	a := Article{Id: "1", TenantId: "t1", Title: "hello", AuthorId: "author-1"}
	s, err := entityToStruct(a)
	if err != nil {
		t.Fatalf("entityToStruct: %v", err)
	}
	got, ok := s.Fields["AuthorId"]
	if !ok {
		t.Fatalf("expected AuthorId in payload, fields: %+v", s.Fields)
	}
	if got.GetStringValue() != "author-1" {
		t.Errorf("AuthorId = %q, want %q", got.GetStringValue(), "author-1")
	}
}

func TestEntityToStruct_ManyToMany_WritesIdListUnderRelatedTypeIds(t *testing.T) {
	tag := Tag{Id: "1", TenantId: "t1", Name: "go", Articles: []string{"a1", "a2"}}
	s, err := entityToStruct(tag)
	if err != nil {
		t.Fatalf("entityToStruct: %v", err)
	}
	got, ok := s.Fields["ArticleIds"]
	if !ok {
		t.Fatalf("expected ArticleIds in payload, fields: %+v", s.Fields)
	}
	lv := got.GetListValue()
	if lv == nil {
		t.Fatalf("ArticleIds is not a ListValue: %+v", got)
	}
	if len(lv.Values) != 2 || lv.Values[0].GetStringValue() != "a1" || lv.Values[1].GetStringValue() != "a2" {
		t.Errorf("unexpected ArticleIds: %+v", lv.Values)
	}
	if _, ok := s.Fields["Articles"]; ok {
		t.Errorf("Articles should not appear in payload under its own name")
	}
}

func TestEntityToStruct_OneToMany_EmitsNothing(t *testing.T) {
	author := Author{Id: "1", TenantId: "t1", Name: "Ben", Articles: []string{"a1"}}
	s, err := entityToStruct(author)
	if err != nil {
		t.Fatalf("entityToStruct: %v", err)
	}
	if _, ok := s.Fields["Articles"]; ok {
		t.Errorf("Articles (nav property) must not appear in payload")
	}
	if _, ok := s.Fields["AuthorId"]; ok {
		t.Errorf("AuthorId must not appear in payload: one_to_many has no FK on this side")
	}
}

func TestEntityToStruct_RoundTrip_ManyToMany(t *testing.T) {
	tag := Tag{Id: "1", TenantId: "t1", Name: "go", Articles: []string{"a1", "a2"}}
	s, err := entityToStruct(tag)
	if err != nil {
		t.Fatalf("entityToStruct: %v", err)
	}
	got, err := structToEntity[Tag](s)
	if err != nil {
		t.Fatalf("structToEntity: %v", err)
	}
	if len(got.Articles) != 2 || got.Articles[0] != "a1" || got.Articles[1] != "a2" {
		t.Errorf("round-trip Articles = %+v, want [a1 a2]", got.Articles)
	}
}

func TestStructToEntity_OneToMany_HydratedChildStructsLeaveFieldEmpty(t *testing.T) {
	// Mirrors what EntityRelationResolver injects on a depth-resolved read: the
	// server puts hydrated child STRUCTS under the field's own name ("Articles"),
	// not a list of ids. Without the KindOneToMany read-side skip, the new
	// ListValue case in protoValueToGoValue would try to parse each child struct
	// as a string element and silently fill the []string with one empty string
	// per child, instead of leaving it nil/empty.
	childStruct, err := structpb.NewStruct(map[string]interface{}{"Id": "a1", "Title": "hello"})
	if err != nil {
		t.Fatalf("structpb.NewStruct: %v", err)
	}
	s := &structpb.Struct{
		Fields: map[string]*structpb.Value{
			"Id":       structpb.NewStringValue("1"),
			"TenantId": structpb.NewStringValue("t1"),
			"Name":     structpb.NewStringValue("Ben"),
			"Articles": structpb.NewListValue(&structpb.ListValue{
				Values: []*structpb.Value{structpb.NewStructValue(childStruct)},
			}),
		},
	}

	got, err := structToEntity[Author](s)
	if err != nil {
		t.Fatalf("structToEntity: %v", err)
	}
	if len(got.Articles) != 0 {
		t.Errorf("Articles = %+v, want empty/nil (hydrated child structs must not be parsed as an id list)", got.Articles)
	}
}

// ── Hydrated-carrier fixtures ───────────────────────────────────────────────
//
// Mirrors conformance/models.go's GoAuthor/GoArticle/GoTag triple (same relation
// shapes: many_to_one, many_to_many, one_to_one via a second singular FK to the
// many_to_many's own related type, and the reverse one_to_many), but declares a
// Hydrated map[string]any carrier on every type so structToEntity's population
// path and entityToStruct's write-path exclusion can be exercised directly.

type HydTag struct {
	Id       string `iverson_key:"true"`
	TenantId string `iverson_tenant:"true"`
	Label    string
	Hydrated map[string]any
}

type HydAuthor struct {
	Id          string `iverson_key:"true"`
	TenantId    string `iverson_tenant:"true"`
	Name        string
	HydArticles []string `iverson:"one_to_many:HydArticle"`
	Hydrated    map[string]any
}

type HydArticle struct {
	Id          string `iverson_key:"true"`
	TenantId    string `iverson_tenant:"true"`
	Title       string
	HydAuthorId string   `iverson:"many_to_one:HydAuthor"`
	HydTagIds   []string `iverson:"many_to_many:HydTag"`
	HydTagId    string   `iverson:"one_to_one:HydTag"`
	Hydrated    map[string]any
}

// HydUnregistered relates to a type that is never passed through buildRequest, so
// registeredTypes never gets an entry for it — the fallback path for an unregistered
// related type.
type HydUnregistered struct {
	Id                string `iverson_key:"true"`
	TenantId          string `iverson_tenant:"true"`
	NeverSeenAuthorId string `iverson:"many_to_one:NeverSeenAuthor"`
}

// registerHydFixtures registers HydAuthor, HydArticle, and HydTag in the package-level
// registeredTypes registry by calling buildRequest directly (no RPC involved — the
// registration side effect happens purely from reflecting on the type), so hydration
// tests can resolve HydAuthor/HydTag by name via lookupRegisteredType.
func registerHydFixtures(t *testing.T) {
	t.Helper()
	r := NewSchemaRegistrar(nil)
	for _, e := range []interface{}{HydAuthor{}, HydArticle{}, HydTag{}} {
		if _, err := r.buildRequest(e, "trace", nil); err != nil {
			t.Fatalf("buildRequest(%T): %v", e, err)
		}
	}
}

func TestBuildRequest_HydratedCarrier_RegistersSuccessfully(t *testing.T) {
	// This is what Step 1's InspectType exclusion buys. Without it, the Hydrated
	// map[string]any field still reaches this point (goTypeToClr's default case
	// falls back to CLR_STRING for an unrecognized kind rather than erroring), but
	// it is wrongly registered as a bogus scalar "Hydrated" property on the
	// server-side schema — a silent corruption, not a client-side failure. Assert
	// its absence from the built properties, which is the part that actually
	// reddens when the exclusion is reverted.
	r := NewSchemaRegistrar(nil, HydArticle{})
	req, err := r.buildRequest(HydArticle{}, "trace", nil)
	if err != nil {
		t.Fatalf("expected registration to succeed with a Hydrated carrier field, got: %v", err)
	}
	for _, p := range req.RootType.Properties {
		if p.Name == HydratedFieldName {
			t.Fatalf("Hydrated must not be registered as a schema property, got: %+v", req.RootType.Properties)
		}
	}
}

func TestStructToEntity_HydratesManyToOneManyToManyAndOneToOne(t *testing.T) {
	registerHydFixtures(t)

	authorStruct, err := structpb.NewStruct(map[string]interface{}{"Id": "auth-1", "Name": "Ben"})
	if err != nil {
		t.Fatalf("structpb.NewStruct: %v", err)
	}
	tag1Struct, err := structpb.NewStruct(map[string]interface{}{"Id": "tag-1", "Label": "go"})
	if err != nil {
		t.Fatalf("structpb.NewStruct: %v", err)
	}
	tag2Struct, err := structpb.NewStruct(map[string]interface{}{"Id": "tag-2", "Label": "grpc"})
	if err != nil {
		t.Fatalf("structpb.NewStruct: %v", err)
	}
	singleTagStruct, err := structpb.NewStruct(map[string]interface{}{"Id": "tag-3", "Label": "singular"})
	if err != nil {
		t.Fatalf("structpb.NewStruct: %v", err)
	}

	s := &structpb.Struct{
		Fields: map[string]*structpb.Value{
			"Id":          structpb.NewStringValue("art-1"),
			"TenantId":    structpb.NewStringValue("t1"),
			"Title":       structpb.NewStringValue("hello"),
			"HydAuthorId": structpb.NewStringValue("auth-1"),
			// "HydAuthor" is the wire (nav-property) name relationPropertyName derives
			// for a many_to_one field named HydAuthorId.
			"HydAuthor": structpb.NewStructValue(authorStruct),
			"HydTagIds": structpb.NewListValue(&structpb.ListValue{
				Values: []*structpb.Value{structpb.NewStringValue("tag-1"), structpb.NewStringValue("tag-2")},
			}),
			"HydTags": structpb.NewListValue(&structpb.ListValue{
				Values: []*structpb.Value{structpb.NewStructValue(tag1Struct), structpb.NewStructValue(tag2Struct)},
			}),
			"HydTagId": structpb.NewStringValue("tag-3"),
			"HydTag":   structpb.NewStructValue(singleTagStruct),
		},
	}

	got, err := structToEntity[HydArticle](s)
	if err != nil {
		t.Fatalf("structToEntity: %v", err)
	}

	author, ok := got.Hydrated["HydAuthor"].(*HydAuthor)
	if !ok {
		t.Fatalf("Hydrated[HydAuthor] = %T, want *HydAuthor", got.Hydrated["HydAuthor"])
	}
	if author.Id != "auth-1" || author.Name != "Ben" {
		t.Errorf("hydrated author = %+v, want Id=auth-1 Name=Ben", author)
	}

	tags, ok := got.Hydrated["HydTags"].([]*HydTag)
	if !ok {
		t.Fatalf("Hydrated[HydTags] = %T, want []*HydTag", got.Hydrated["HydTags"])
	}
	if len(tags) != 2 || tags[0].Id != "tag-1" || tags[1].Id != "tag-2" {
		t.Errorf("hydrated tags = %+v, want [tag-1 tag-2]", tags)
	}

	singleTag, ok := got.Hydrated["HydTag"].(*HydTag)
	if !ok {
		t.Fatalf("Hydrated[HydTag] = %T, want *HydTag", got.Hydrated["HydTag"])
	}
	if singleTag.Id != "tag-3" || singleTag.Label != "singular" {
		t.Errorf("hydrated single tag = %+v, want Id=tag-3 Label=singular", singleTag)
	}
}

func TestStructToEntity_OneToMany_HydratesCarrierWhileDeclaredMemberStaysEmpty(t *testing.T) {
	registerHydFixtures(t)

	child1, err := structpb.NewStruct(map[string]interface{}{"Id": "art-1", "Title": "one"})
	if err != nil {
		t.Fatalf("structpb.NewStruct: %v", err)
	}
	child2, err := structpb.NewStruct(map[string]interface{}{"Id": "art-2", "Title": "two"})
	if err != nil {
		t.Fatalf("structpb.NewStruct: %v", err)
	}

	s := &structpb.Struct{
		Fields: map[string]*structpb.Value{
			"Id":       structpb.NewStringValue("auth-1"),
			"TenantId": structpb.NewStringValue("t1"),
			"Name":     structpb.NewStringValue("Ben"),
			"HydArticles": structpb.NewListValue(&structpb.ListValue{
				Values: []*structpb.Value{structpb.NewStructValue(child1), structpb.NewStructValue(child2)},
			}),
		},
	}

	got, err := structToEntity[HydAuthor](s)
	if err != nil {
		t.Fatalf("structToEntity: %v", err)
	}

	if len(got.HydArticles) != 0 {
		t.Errorf("HydArticles = %+v, want empty: the declared []string member has no struct case in protoValueToGoValue", got.HydArticles)
	}

	articles, ok := got.Hydrated["HydArticles"].([]*HydArticle)
	if !ok {
		t.Fatalf("Hydrated[HydArticles] = %T, want []*HydArticle", got.Hydrated["HydArticles"])
	}
	if len(articles) != 2 || articles[0].Id != "art-1" || articles[1].Id != "art-2" {
		t.Errorf("hydrated articles = %+v, want [art-1 art-2]", articles)
	}
}

func TestEntityToStruct_HydratedCarrier_ExcludedFromWrite(t *testing.T) {
	entity := HydArticle{
		Id:          "art-1",
		TenantId:    "t1",
		Title:       "hello",
		HydAuthorId: "auth-1",
		Hydrated:    map[string]any{"HydAuthor": &HydAuthor{Id: "auth-1"}},
	}
	s, err := entityToStruct(entity)
	if err != nil {
		t.Fatalf("entityToStruct: %v", err)
	}
	if _, ok := s.Fields["Hydrated"]; ok {
		t.Errorf("Hydrated must not appear in the write-path payload, fields: %+v", s.Fields)
	}
	if got := s.Fields["HydAuthorId"].GetStringValue(); got != "auth-1" {
		t.Errorf("HydAuthorId = %q, want auth-1", got)
	}
}

func TestStructToEntity_UnregisteredRelatedType_FallsBackToUntypedChild(t *testing.T) {
	r := NewSchemaRegistrar(nil, HydUnregistered{})
	if _, err := r.buildRequest(HydUnregistered{}, "trace", nil); err != nil {
		t.Fatalf("buildRequest: %v", err)
	}
	// HydUnregistered itself has no Hydrated field, so structToEntity[HydUnregistered]
	// can't demonstrate the fallback; the fallback lives in populateHydrated, keyed on
	// whether the RELATED type ("NeverSeenAuthor") was ever registered, which it never
	// is here. Exercise it through HydArticle's HydAuthor relation by pointing the wire
	// payload's related-type name at one that was never registered.
	//
	// Simplest faithful reproduction: hydrate an HydArticle whose registered
	// HydAuthor relation resolves fine, but assert the *unregistered* case directly by
	// clearing the registry entry for HydAuthor first.
	registeredTypesMu.Lock()
	delete(registeredTypes, "HydAuthor")
	registeredTypesMu.Unlock()
	t.Cleanup(func() {
		registerHydFixtures(t)
	})

	authorStruct, err := structpb.NewStruct(map[string]interface{}{"Id": "auth-1", "Name": "Ben"})
	if err != nil {
		t.Fatalf("structpb.NewStruct: %v", err)
	}
	s := &structpb.Struct{
		Fields: map[string]*structpb.Value{
			"Id":          structpb.NewStringValue("art-1"),
			"TenantId":    structpb.NewStringValue("t1"),
			"Title":       structpb.NewStringValue("hello"),
			"HydAuthorId": structpb.NewStringValue("auth-1"),
			"HydAuthor":   structpb.NewStructValue(authorStruct),
		},
	}

	got, err := structToEntity[HydArticle](s)
	if err != nil {
		t.Fatalf("structToEntity: %v", err)
	}

	if _, ok := got.Hydrated["HydAuthor"].(*HydAuthor); ok {
		t.Fatalf("HydAuthor should not be a typed *HydAuthor once unregistered")
	}
	m, ok := got.Hydrated["HydAuthor"].(map[string]interface{})
	if !ok {
		t.Fatalf("Hydrated[HydAuthor] = %T, want untyped map[string]interface{} fallback", got.Hydrated["HydAuthor"])
	}
	if m["Id"] != "auth-1" {
		t.Errorf("untyped fallback = %+v, want Id=auth-1", m)
	}
}

// propsByName indexes a built request's synthesized properties for assertion.
func propsByName(t *testing.T, e interface{}) map[string]*pb.PropertyDescriptor {
	t.Helper()
	r := NewSchemaRegistrar(nil, e)
	req, err := r.buildRequest(e, "trace", nil)
	if err != nil {
		t.Fatalf("buildRequest: %v", err)
	}
	out := make(map[string]*pb.PropertyDescriptor, len(req.RootType.Properties))
	for _, p := range req.RootType.Properties {
		out[p.Name] = p
	}
	return out
}

func assertFkProperty(t *testing.T, props map[string]*pb.PropertyDescriptor, name string, wantArray bool) {
	t.Helper()
	p, ok := props[name]
	if !ok {
		t.Fatalf("expected %s property in schema, got: %v", name, props)
	}
	if p.ClrType != pb.ClrType_CLR_GUID {
		t.Errorf("%s.ClrType = %v, want CLR_GUID", name, p.ClrType)
	}
	if p.IsArray != wantArray {
		t.Errorf("%s.IsArray = %v, want %v", name, p.IsArray, wantArray)
	}
	if !p.IsNullable {
		t.Errorf("%s.IsNullable = false, want true", name)
	}
	if p.IsKey {
		t.Errorf("%s.IsKey = true, want false", name)
	}
}

func TestBuildRequest_ManyToOne_DeclaresScalarForeignKeyProperty(t *testing.T) {
	assertFkProperty(t, propsByName(t, Article{}), "AuthorId", false)
}

func TestBuildRequest_OneToOne_DeclaresScalarForeignKeyProperty(t *testing.T) {
	assertFkProperty(t, propsByName(t, Profile{}), "UserId", false)
}

func TestBuildRequest_ManyToMany_DeclaresArrayForeignKeyProperty(t *testing.T) {
	assertFkProperty(t, propsByName(t, Tag{}), "ArticleIds", true)
}

type NavTagArticle struct {
	Id        string   `iverson_key:"true"`
	TenantId  string   `iverson_tenant:"true"`
	RegTagIds []string `iverson:"many_to_many:RegTag"`
}

func TestBuildRequest_ManyToMany_PropertyNameDiffersFromForeignKey(t *testing.T) {
	r := NewSchemaRegistrar(nil, NavTagArticle{})
	req, err := r.buildRequest(NavTagArticle{}, "trace", nil)
	if err != nil {
		t.Fatalf("buildRequest: %v", err)
	}
	if len(req.RootType.Relations) != 1 {
		t.Fatalf("expected 1 relation, got %d: %+v", len(req.RootType.Relations), req.RootType.Relations)
	}
	rel := req.RootType.Relations[0]
	// The navigation property name must NOT collide with the FK column, or a
	// depth-resolved read overwrites the FK value with the hydrated entity.
	if rel.PropertyName == rel.ForeignKey {
		t.Errorf("PropertyName (%s) must differ from ForeignKey (%s)", rel.PropertyName, rel.ForeignKey)
	}
	if rel.PropertyName != "RegTags" {
		t.Errorf("PropertyName = %q, want %q", rel.PropertyName, "RegTags")
	}
	if rel.ForeignKey != "RegTagIds" {
		t.Errorf("ForeignKey = %q, want %q", rel.ForeignKey, "RegTagIds")
	}

	assertFkProperty(t, propsByName(t, NavTagArticle{}), "RegTagIds", true)
}

func TestBuildRequest_OneToMany_DeclaresNoForeignKeyProperty(t *testing.T) {
	props := propsByName(t, Author{})
	// Author.Articles is one_to_many:Article — its FK lives on the Article row,
	// so no column may be synthesized on this side.
	if _, ok := props["AuthorId"]; ok {
		t.Errorf("one_to_many must not synthesize AuthorId, got: %v", props)
	}
	if _, ok := props["ArticleId"]; ok {
		t.Errorf("one_to_many must not synthesize ArticleId, got: %v", props)
	}
	if _, ok := props["Articles"]; ok {
		t.Errorf("one_to_many relation field must not become a property, got: %v", props)
	}
}

func TestGuidTagYieldsClrGuid(t *testing.T) {
	type GuidTagEntity struct {
		Id       string `iverson_key:"true" iverson_guid:"true"`
		Name     string
		TenantId string `iverson_tenant:"true"`
	}

	props := propsByName(t, &GuidTagEntity{})

	if got := props["Id"].ClrType; got != pb.ClrType_CLR_GUID {
		t.Errorf("Id.ClrType = %v, want CLR_GUID", got)
	}
	if got := props["Name"].ClrType; got != pb.ClrType_CLR_STRING {
		t.Errorf("Name.ClrType = %v, want CLR_STRING (untagged string stays a string)", got)
	}
}

func TestGuidTagOnStringSliceYieldsClrGuidArray(t *testing.T) {
	type GuidSliceEntity struct {
		Id       string   `iverson_key:"true"`
		TagIds   []string `iverson_guid:"true"`
		TenantId string   `iverson_tenant:"true"`
	}

	props := propsByName(t, &GuidSliceEntity{})

	p, ok := props["TagIds"]
	if !ok {
		t.Fatalf("expected TagIds property, got: %v", props)
	}
	if p.ClrType != pb.ClrType_CLR_GUID {
		t.Errorf("TagIds.ClrType = %v, want CLR_GUID", p.ClrType)
	}
	if !p.IsArray {
		t.Errorf("TagIds.IsArray = false, want true")
	}
}

func TestGuidTagOnNonStringFieldRejected(t *testing.T) {
	type GuidOnIntEntity struct {
		Id        string `iverson_key:"true"`
		WordCount int    `iverson_guid:"true"`
		TenantId  string `iverson_tenant:"true"`
	}

	r := NewSchemaRegistrar(nil, GuidOnIntEntity{})
	_, err := r.buildRequest(GuidOnIntEntity{}, "trace", nil)
	if err == nil {
		t.Fatal("expected error for iverson_guid on a non-string field")
	}
	msg := err.Error()
	if !strings.Contains(msg, "WordCount") {
		t.Errorf("error should name the field WordCount, got: %v", err)
	}
	if !strings.Contains(msg, "iverson_guid") {
		t.Errorf("error should name the iverson_guid tag, got: %v", err)
	}
}

func TestGuidTagOnNonStringSliceRejected(t *testing.T) {
	type GuidOnIntSliceEntity struct {
		Id       string `iverson_key:"true"`
		Counts   []int  `iverson_guid:"true"`
		TenantId string `iverson_tenant:"true"`
	}

	r := NewSchemaRegistrar(nil, GuidOnIntSliceEntity{})
	_, err := r.buildRequest(GuidOnIntSliceEntity{}, "trace", nil)
	if err == nil {
		t.Fatal("expected error for iverson_guid on a []int field")
	}
	msg := err.Error()
	if !strings.Contains(msg, "Counts") {
		t.Errorf("error should name the field Counts, got: %v", err)
	}
}

func TestBuildRequest_ManyToOne_CorrectlyNamedFieldRegisters(t *testing.T) {
	r := NewSchemaRegistrar(nil, Article{})
	_, err := r.buildRequest(Article{}, "trace", nil)
	if err != nil {
		t.Fatalf("expected no error for correctly-named AuthorId field: %v", err)
	}
}

func TestBuildRequest_ManyToOne_WronglyNamedFieldRejected(t *testing.T) {
	r := NewSchemaRegistrar(nil, WriterAuthor{})
	_, err := r.buildRequest(WriterAuthor{}, "trace", nil)
	if err == nil {
		t.Fatal("expected error for WriterId field on a many_to_one Author relation")
	}
	msg := err.Error()
	if !strings.Contains(msg, "WriterId") || !strings.Contains(msg, "AuthorId") {
		t.Errorf("error should name both WriterId and AuthorId, got: %v", err)
	}
}

// ── Mock SearchClient ────────────────────────────────────────────────────────

type mockSearchStream struct {
	responses []*pb.SearchResponse
	idx       int
	streamErr error
}

func (m *mockSearchStream) Recv() (*pb.SearchResponse, error) {
	if m.idx < len(m.responses) {
		r := m.responses[m.idx]
		m.idx++
		return r, nil
	}
	if m.streamErr != nil {
		return nil, m.streamErr
	}
	return nil, io.EOF
}

type mockChunkSearchStream struct {
	responses []*pb.ChunkSearchResponse
	idx       int
	streamErr error
}

func (m *mockChunkSearchStream) Recv() (*pb.ChunkSearchResponse, error) {
	if m.idx < len(m.responses) {
		r := m.responses[m.idx]
		m.idx++
		return r, nil
	}
	if m.streamErr != nil {
		return nil, m.streamErr
	}
	return nil, io.EOF
}

type mockSearchClient struct {
	searchStream   *mockSearchStream
	searchErr      error
	similarStream  *mockSearchStream
	similarErr     error
	chunksStream   *mockChunkSearchStream
	chunksErr      error
	groupByStream  *mockSearchStream
	groupByErr     error
	pipelineStream *mockSearchStream
	pipelineErr    error
	aggregateResp  *pb.AggregateResponse
	aggregateErr   error

	capturedSearch        *pb.SearchRequest
	capturedSearchSimilar *pb.SearchSimilarRequest
	capturedSearchChunks  *pb.SearchChunksRequest
	capturedGroupBy       *pb.GroupByRequest
	capturedPipeline      *pb.PipelineRequest
	capturedAggregate     *pb.AggregateRequest
}

func (m *mockSearchClient) Search(_ context.Context, req *pb.SearchRequest) (SearchStream, error) {
	m.capturedSearch = req
	if m.searchErr != nil {
		return nil, m.searchErr
	}
	return m.searchStream, nil
}

func (m *mockSearchClient) SearchSimilar(_ context.Context, req *pb.SearchSimilarRequest) (SearchStream, error) {
	m.capturedSearchSimilar = req
	if m.similarErr != nil {
		return nil, m.similarErr
	}
	return m.similarStream, nil
}

func (m *mockSearchClient) SearchChunks(_ context.Context, req *pb.SearchChunksRequest) (ChunkSearchStream, error) {
	m.capturedSearchChunks = req
	if m.chunksErr != nil {
		return nil, m.chunksErr
	}
	return m.chunksStream, nil
}

func (m *mockSearchClient) Aggregate(_ context.Context, req *pb.AggregateRequest) (*pb.AggregateResponse, error) {
	m.capturedAggregate = req
	if m.aggregateErr != nil {
		return nil, m.aggregateErr
	}
	return m.aggregateResp, nil
}

func (m *mockSearchClient) GroupBy(_ context.Context, req *pb.GroupByRequest) (SearchStream, error) {
	m.capturedGroupBy = req
	if m.groupByErr != nil {
		return nil, m.groupByErr
	}
	return m.groupByStream, nil
}

func (m *mockSearchClient) Pipeline(_ context.Context, req *pb.PipelineRequest) (SearchStream, error) {
	m.capturedPipeline = req
	if m.pipelineErr != nil {
		return nil, m.pipelineErr
	}
	return m.pipelineStream, nil
}

// ── Helpers ───────────────────────────────────────────────────────────────────

func mustStruct(t *testing.T, fields map[string]interface{}) *structpb.Struct {
	t.Helper()
	s, err := structpb.NewStruct(fields)
	if err != nil {
		t.Fatalf("structpb.NewStruct: %v", err)
	}
	return s
}

func newTestCoordinator(t *testing.T, search *mockSearchClient) *EntityCoordinator[coordinatorArticle] {
	t.Helper()
	c, err := newEntityCoordinatorWithDeps(coordinatorDeps{search: search}, coordinatorArticle{})
	if err != nil {
		t.Fatalf("newEntityCoordinatorWithDeps: %v", err)
	}
	return c
}

// ── Search ────────────────────────────────────────────────────────────────────

func TestCoordinatorSearch_ReturnsEntitiesWithScores(t *testing.T) {
	search := &mockSearchClient{
		searchStream: &mockSearchStream{responses: []*pb.SearchResponse{
			{Data: mustStruct(t, map[string]interface{}{"Id": "1", "Category": "tech"}), Score: 0.9},
			{Data: mustStruct(t, map[string]interface{}{"Id": "2", "Category": "news"}), Score: 0.5},
		}},
	}
	c := newTestCoordinator(t, search)

	results, err := c.Search(context.Background(), &pb.SearchRequest{TypeName: "coordinatorArticle"})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(results) != 2 {
		t.Fatalf("expected 2 results, got %d", len(results))
	}
	if results[0].Entity.Id != "1" || results[0].Entity.Category != "tech" || results[0].Score != 0.9 {
		t.Errorf("unexpected result 0: %+v", results[0])
	}
	if results[1].Entity.Id != "2" || results[1].Score != 0.5 {
		t.Errorf("unexpected result 1: %+v", results[1])
	}
	if search.capturedSearch == nil || search.capturedSearch.TypeName != "coordinatorArticle" {
		t.Errorf("request not passed through: %+v", search.capturedSearch)
	}
}

func TestCoordinatorSearch_PropagatesInitialError(t *testing.T) {
	search := &mockSearchClient{searchErr: errors.New("boom")}
	c := newTestCoordinator(t, search)

	_, err := c.Search(context.Background(), &pb.SearchRequest{})
	if err == nil {
		t.Fatal("expected error")
	}
}

func TestCoordinatorSearch_PropagatesStreamError(t *testing.T) {
	search := &mockSearchClient{
		searchStream: &mockSearchStream{streamErr: errors.New("stream boom")},
	}
	c := newTestCoordinator(t, search)

	_, err := c.Search(context.Background(), &pb.SearchRequest{})
	if err == nil {
		t.Fatal("expected stream error")
	}
}

func TestCoordinatorSearch_EmptyStream_ReturnsNoResults(t *testing.T) {
	search := &mockSearchClient{searchStream: &mockSearchStream{}}
	c := newTestCoordinator(t, search)

	results, err := c.Search(context.Background(), &pb.SearchRequest{})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(results) != 0 {
		t.Errorf("expected 0 results, got %d", len(results))
	}
}

// ── SearchSimilar ─────────────────────────────────────────────────────────────

func TestCoordinatorSearchSimilar_ReturnsEntitiesWithScores(t *testing.T) {
	search := &mockSearchClient{
		similarStream: &mockSearchStream{responses: []*pb.SearchResponse{
			{Data: mustStruct(t, map[string]interface{}{"Id": "1", "Category": "tech"}), Score: 0.87},
		}},
	}
	c := newTestCoordinator(t, search)

	results, err := c.SearchSimilar(context.Background(), &pb.SearchSimilarRequest{TypeName: "coordinatorArticle"})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(results) != 1 || results[0].Entity.Id != "1" || results[0].Score != 0.87 {
		t.Errorf("unexpected results: %+v", results)
	}
	if search.capturedSearchSimilar == nil {
		t.Error("request not passed through")
	}
}

func TestCoordinatorSearchSimilar_PropagatesInitialError(t *testing.T) {
	search := &mockSearchClient{similarErr: errors.New("boom")}
	c := newTestCoordinator(t, search)

	_, err := c.SearchSimilar(context.Background(), &pb.SearchSimilarRequest{})
	if err == nil {
		t.Fatal("expected error")
	}
}

// ── SearchChunks ──────────────────────────────────────────────────────────────

func TestCoordinatorSearchChunks_ReturnsRawResponses(t *testing.T) {
	search := &mockSearchClient{
		chunksStream: &mockChunkSearchStream{responses: []*pb.ChunkSearchResponse{
			{ParentKey: "1", ChunkText: "hello", Score: 0.6},
			{ParentKey: "1", ChunkText: "world", Score: 0.4},
		}},
	}
	c := newTestCoordinator(t, search)

	results, err := c.SearchChunks(context.Background(), &pb.SearchChunksRequest{TypeName: "coordinatorArticle"})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(results) != 2 {
		t.Fatalf("expected 2 results, got %d", len(results))
	}
	if results[0].ChunkText != "hello" || results[1].ChunkText != "world" {
		t.Errorf("unexpected results: %+v", results)
	}
}

func TestCoordinatorSearchChunks_PropagatesInitialError(t *testing.T) {
	search := &mockSearchClient{chunksErr: errors.New("boom")}
	c := newTestCoordinator(t, search)

	_, err := c.SearchChunks(context.Background(), &pb.SearchChunksRequest{})
	if err == nil {
		t.Fatal("expected error")
	}
}

// ── GroupBy ───────────────────────────────────────────────────────────────────

func TestCoordinatorGroupBy_ReturnsMaps(t *testing.T) {
	search := &mockSearchClient{
		groupByStream: &mockSearchStream{responses: []*pb.SearchResponse{
			{Data: mustStruct(t, map[string]interface{}{"Category": "tech", "n": 3.0})},
		}},
	}
	c := newTestCoordinator(t, search)

	rows, err := c.GroupBy(context.Background(), &pb.GroupByRequest{TypeName: "coordinatorArticle"})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(rows) != 1 {
		t.Fatalf("expected 1 row, got %d", len(rows))
	}
	if rows[0]["Category"] != "tech" || rows[0]["n"] != 3.0 {
		t.Errorf("unexpected row: %+v", rows[0])
	}
}

func TestCoordinatorGroupBy_PropagatesInitialError(t *testing.T) {
	search := &mockSearchClient{groupByErr: errors.New("boom")}
	c := newTestCoordinator(t, search)

	_, err := c.GroupBy(context.Background(), &pb.GroupByRequest{})
	if err == nil {
		t.Fatal("expected error")
	}
}

// ── Pipeline ──────────────────────────────────────────────────────────────────

func TestCoordinatorPipeline_ReturnsMaps(t *testing.T) {
	search := &mockSearchClient{
		pipelineStream: &mockSearchStream{responses: []*pb.SearchResponse{
			{Data: mustStruct(t, map[string]interface{}{"rank": 1.0})},
		}},
	}
	c := newTestCoordinator(t, search)

	rows, err := c.Pipeline(context.Background(), &pb.PipelineRequest{TypeName: "coordinatorArticle"})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(rows) != 1 || rows[0]["rank"] != 1.0 {
		t.Errorf("unexpected rows: %+v", rows)
	}
}

func TestCoordinatorPipeline_PropagatesInitialError(t *testing.T) {
	search := &mockSearchClient{pipelineErr: errors.New("boom")}
	c := newTestCoordinator(t, search)

	_, err := c.Pipeline(context.Background(), &pb.PipelineRequest{})
	if err == nil {
		t.Fatal("expected error")
	}
}

// ── Aggregate ─────────────────────────────────────────────────────────────────

func TestCoordinatorAggregate_ReturnsResponse(t *testing.T) {
	search := &mockSearchClient{
		aggregateResp: &pb.AggregateResponse{Total: 42},
	}
	c := newTestCoordinator(t, search)

	resp, err := c.Aggregate(context.Background(), &pb.AggregateRequest{TypeName: "coordinatorArticle"})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if resp.Total != 42 {
		t.Errorf("total = %d, want 42", resp.Total)
	}
	if search.capturedAggregate == nil {
		t.Error("request not passed through")
	}
}

func TestCoordinatorAggregate_PropagatesError(t *testing.T) {
	search := &mockSearchClient{aggregateErr: errors.New("boom")}
	c := newTestCoordinator(t, search)

	_, err := c.Aggregate(context.Background(), &pb.AggregateRequest{})
	if err == nil {
		t.Fatal("expected error")
	}
}

// ── Mapped CRUD ───────────────────────────────────────────────────────────────

type mockMappingClient struct {
	getResp    *pb.MappingResponse
	getErr     error
	postResp   *pb.MappingResponse
	postErr    error
	updateResp *pb.MappingResponse
	updateErr  error
	deleteResp *pb.MappingDeleteResponse
	deleteErr  error

	capturedGet    *pb.MappingGetRequest
	capturedPost   *pb.MappingWriteRequest
	capturedUpdate *pb.MappingWriteRequest
	capturedDelete *pb.MappingDeleteRequest
}

func (m *mockMappingClient) Get(_ context.Context, req *pb.MappingGetRequest) (*pb.MappingResponse, error) {
	m.capturedGet = req
	if m.getErr != nil {
		return nil, m.getErr
	}
	return m.getResp, nil
}

func (m *mockMappingClient) Post(_ context.Context, req *pb.MappingWriteRequest) (*pb.MappingResponse, error) {
	m.capturedPost = req
	if m.postErr != nil {
		return nil, m.postErr
	}
	return m.postResp, nil
}

func (m *mockMappingClient) Update(_ context.Context, req *pb.MappingWriteRequest) (*pb.MappingResponse, error) {
	m.capturedUpdate = req
	if m.updateErr != nil {
		return nil, m.updateErr
	}
	return m.updateResp, nil
}

func (m *mockMappingClient) Delete(_ context.Context, req *pb.MappingDeleteRequest) (*pb.MappingDeleteResponse, error) {
	m.capturedDelete = req
	if m.deleteErr != nil {
		return nil, m.deleteErr
	}
	return m.deleteResp, nil
}

func newTestMappedCoordinator(t *testing.T, mapping *mockMappingClient) *EntityCoordinator[coordinatorArticle] {
	t.Helper()
	c, err := newEntityCoordinatorWithDeps(coordinatorDeps{mapping: mapping}, coordinatorArticle{})
	if err != nil {
		t.Fatalf("newEntityCoordinatorWithDeps: %v", err)
	}
	return c
}

func TestCoordinatorGetMapped_PassesDepthThrough(t *testing.T) {
	mapping := &mockMappingClient{
		getResp: &pb.MappingResponse{
			Success: true,
			Data:    mustStruct(t, map[string]interface{}{"Id": "k", "Category": "tech"}),
		},
	}
	c := newTestMappedCoordinator(t, mapping)

	_, err := c.GetMapped(context.Background(), "k", 2)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if mapping.capturedGet == nil {
		t.Fatal("request not passed through")
	}
	if mapping.capturedGet.Depth != 2 {
		t.Errorf("Depth = %d, want 2", mapping.capturedGet.Depth)
	}
	if mapping.capturedGet.Key != "k" {
		t.Errorf("Key = %q, want %q", mapping.capturedGet.Key, "k")
	}
}

func TestCoordinatorPostMapped_ReturnsEntityHydratedFromData(t *testing.T) {
	mapping := &mockMappingClient{
		postResp: &pb.MappingResponse{
			Success: true,
			Data:    mustStruct(t, map[string]interface{}{"Id": "server-assigned-id", "Category": "tech"}),
		},
	}
	c := newTestMappedCoordinator(t, mapping)

	entity, err := c.PostMapped(context.Background(), coordinatorArticle{Id: "client-supplied-id", Category: "tech"})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if entity.Id != "server-assigned-id" {
		t.Errorf("Id = %q, want %q", entity.Id, "server-assigned-id")
	}
}

func TestCoordinatorUpdateMapped_SendsKeyItWasGiven(t *testing.T) {
	mapping := &mockMappingClient{
		updateResp: &pb.MappingResponse{
			Success: true,
			Data:    mustStruct(t, map[string]interface{}{"Id": "k", "Category": "tech"}),
		},
	}
	c := newTestMappedCoordinator(t, mapping)

	_, err := c.UpdateMapped(context.Background(), coordinatorArticle{Id: "k", Category: "tech"})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if mapping.capturedUpdate == nil {
		t.Fatal("request not passed through")
	}
	gotKey := mapping.capturedUpdate.Payload.Fields["Id"].GetStringValue()
	if gotKey != "k" {
		t.Errorf("Payload Id = %q, want %q", gotKey, "k")
	}
}
