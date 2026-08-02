package iverson_test

import (
	"context"
	"errors"
	"strings"
	"testing"
	"time"

	pb "github.com/iverson/clients/go/generated"
	"github.com/iverson/clients/go/iverson"
)

// ── Mock MappingClient ─────────────────────────────────────────────────────────

type mockMappingClient struct {
	capturedReq *pb.SchemaRequest
	response    *pb.SchemaResponse
	err         error
}

func (m *mockMappingClient) RegisterSchema(_ context.Context, req *pb.SchemaRequest) (*pb.SchemaResponse, error) {
	m.capturedReq = req
	if m.err != nil {
		return nil, m.err
	}
	return m.response, nil
}

// ── Test entity types ──────────────────────────────────────────────────────────

type registrarArticle struct {
	Id          string `iverson_key:"true"`
	TenantId    string `iverson_search_key:"2" iverson_tenant:"true"`
	Title       string `iverson_embedding:"true"`
	Body        string `iverson_large_field:"true"`
	Category    string `iverson_search_key:"0"`
	WordCount   int
	PublishedAt time.Time `iverson_search_key:"1"`
	AuthorId    string    `iverson:"many_to_one:Author"`
	Summary     string    `iverson_chunk:"256:32"`
}

// ── Tests ─────────────────────────────────────────────────────────────────────

func TestSchemaRegistrar_RegisterAll_Success(t *testing.T) {
	mock := &mockMappingClient{
		response: &pb.SchemaResponse{Success: true},
	}
	registrar := iverson.NewSchemaRegistrar(mock, registrarArticle{})

	if err := registrar.RegisterAll(context.Background(), "trace-1"); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if mock.capturedReq == nil {
		t.Fatal("no request captured")
	}
}

func TestSchemaRegistrar_RegisterAll_TypeName(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, registrarArticle{})
	_ = registrar.RegisterAll(context.Background(), "")

	if mock.capturedReq.RootType.TypeName != "registrarArticle" {
		t.Errorf("expected TypeName=registrarArticle, got %q", mock.capturedReq.RootType.TypeName)
	}
}

func TestSchemaRegistrar_RegisterAll_TraceId(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, registrarArticle{})
	_ = registrar.RegisterAll(context.Background(), "my-trace")

	if mock.capturedReq.TraceId != "my-trace" {
		t.Errorf("expected trace_id=my-trace, got %q", mock.capturedReq.TraceId)
	}
}

func TestSchemaRegistrar_RegisterAll_Properties(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, registrarArticle{})
	_ = registrar.RegisterAll(context.Background(), "")

	props := mock.capturedReq.RootType.Properties
	// Should have 8 properties (Id, TenantId, Title, Body, Category, WordCount, PublishedAt, Summary)
	if len(props) != 8 {
		t.Errorf("expected 8 properties, got %d", len(props))
	}
}

func TestSchemaRegistrar_RegisterAll_KeyField(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, registrarArticle{})
	_ = registrar.RegisterAll(context.Background(), "")

	var keyProp *pb.PropertyDescriptor
	for _, p := range mock.capturedReq.RootType.Properties {
		if p.IsKey {
			keyProp = p
			break
		}
	}
	if keyProp == nil {
		t.Fatal("no key property found")
	}
	if keyProp.Name != "Id" {
		t.Errorf("expected key Name=Id, got %q", keyProp.Name)
	}
}

func TestSchemaRegistrar_RegisterAll_SearchKey(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, registrarArticle{})
	_ = registrar.RegisterAll(context.Background(), "")

	searchKeys := map[string]int32{}
	for _, p := range mock.capturedReq.RootType.Properties {
		if p.IsSearchKey {
			searchKeys[p.Name] = p.SearchKeyOrder
		}
	}
	if len(searchKeys) != 3 {
		t.Fatalf("expected 3 search keys, got %d", len(searchKeys))
	}
	if searchKeys["Category"] != 0 {
		t.Errorf("Category order should be 0, got %d", searchKeys["Category"])
	}
	if searchKeys["PublishedAt"] != 1 {
		t.Errorf("PublishedAt order should be 1, got %d", searchKeys["PublishedAt"])
	}
	if searchKeys["TenantId"] != 2 {
		t.Errorf("TenantId order should be 2, got %d", searchKeys["TenantId"])
	}
}

func TestSchemaRegistrar_RegisterAll_LargeField(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, registrarArticle{})
	_ = registrar.RegisterAll(context.Background(), "")

	found := false
	for _, p := range mock.capturedReq.RootType.Properties {
		if p.Name == "Body" {
			found = true
			if !p.IsLargeField {
				t.Error("Body.IsLargeField should be true")
			}
		}
	}
	if !found {
		t.Error("Body property not found")
	}
}

func TestSchemaRegistrar_RegisterAll_Embedding(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, registrarArticle{})
	_ = registrar.RegisterAll(context.Background(), "")

	found := false
	for _, p := range mock.capturedReq.RootType.Properties {
		if p.Name == "Title" {
			found = true
			if !p.IsEmbedding {
				t.Error("Title.IsEmbedding should be true")
			}
		}
	}
	if !found {
		t.Error("Title property not found")
	}
}

func TestSchemaRegistrar_RegisterAll_Chunk(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, registrarArticle{})
	_ = registrar.RegisterAll(context.Background(), "")

	found := false
	for _, p := range mock.capturedReq.RootType.Properties {
		if p.Name == "Summary" {
			found = true
			if !p.IsChunk {
				t.Error("Summary.IsChunk should be true")
			}
			if p.ChunkMaxTokens != 256 {
				t.Errorf("expected ChunkMaxTokens=256, got %d", p.ChunkMaxTokens)
			}
			if p.ChunkOverlap != 32 {
				t.Errorf("expected ChunkOverlap=32, got %d", p.ChunkOverlap)
			}
		}
	}
	if !found {
		t.Error("Summary property not found")
	}
}

func TestSchemaRegistrar_RegisterAll_Relation(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, registrarArticle{})
	_ = registrar.RegisterAll(context.Background(), "")

	rels := mock.capturedReq.RootType.Relations
	if len(rels) != 1 {
		t.Fatalf("expected 1 relation, got %d", len(rels))
	}
	rel := rels[0]
	if rel.PropertyName != "Author" {
		t.Errorf("expected PropertyName=Author, got %q", rel.PropertyName)
	}
	if rel.Kind != pb.RelationKind_MANY_TO_ONE {
		t.Errorf("expected MANY_TO_ONE, got %v", rel.Kind)
	}
	if rel.RelatedType != "Author" {
		t.Errorf("expected RelatedType=Author, got %q", rel.RelatedType)
	}
	if rel.ForeignKey != "AuthorId" {
		t.Errorf("expected ForeignKey=AuthorId, got %q", rel.ForeignKey)
	}
}

func TestSchemaRegistrar_RegisterAll_RPCError(t *testing.T) {
	mock := &mockMappingClient{err: errors.New("connection refused")}
	registrar := iverson.NewSchemaRegistrar(mock, registrarArticle{})
	err := registrar.RegisterAll(context.Background(), "")
	if err == nil {
		t.Fatal("expected error, got nil")
	}
}

func TestSchemaRegistrar_RegisterAll_ServerError(t *testing.T) {
	mock := &mockMappingClient{
		response: &pb.SchemaResponse{Success: false, Error: "table already exists"},
	}
	registrar := iverson.NewSchemaRegistrar(mock, registrarArticle{})
	err := registrar.RegisterAll(context.Background(), "")
	if err == nil {
		t.Fatal("expected error, got nil")
	}
}

func TestSchemaRegistrar_RegisterAll_MultipleEntities(t *testing.T) {
	type secondEntity struct {
		Id       string `iverson_key:"true"`
		TenantId string `iverson_tenant:"true"`
		Name     string
	}

	callCount := 0
	mock := &mockMappingClient{}
	mock.response = &pb.SchemaResponse{Success: true}

	// We need a way to count calls — use a counting wrapper
	type countingClient struct {
		inner *mockMappingClient
		count *int
	}
	cc := &struct {
		inner *mockMappingClient
		count int
	}{inner: mock}

	countMock := &countingMappingClient{inner: mock, count: &cc.count}
	registrar := iverson.NewSchemaRegistrar(countMock, registrarArticle{}, secondEntity{})
	_ = callCount
	if err := registrar.RegisterAll(context.Background(), ""); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if cc.count != 2 {
		t.Errorf("expected 2 RegisterSchema calls, got %d", cc.count)
	}
}

type countingMappingClient struct {
	inner *mockMappingClient
	count *int
}

func (c *countingMappingClient) RegisterSchema(ctx context.Context, req *pb.SchemaRequest) (*pb.SchemaResponse, error) {
	*c.count++
	return c.inner.RegisterSchema(ctx, req)
}

// ── metadata / description registrar tests ─────────────────────────────────────

type describedArticle struct {
	Id       string `iverson_key:"true" iverson_desc:"Primary identifier"`
	TenantId string `iverson_tenant:"true"`
	Status   string `iverson_meta:"true" iverson_desc:"Publication status"`
	Title    string `iverson_desc:"Headline"`
	Body     string
}

func (describedArticle) IversonDescription() string { return "An article with metadata" }

type undescribedArticle struct {
	Id       string `iverson_key:"true"`
	TenantId string `iverson_tenant:"true"`
}

func propByName(t *testing.T, req *pb.SchemaRequest, name string) *pb.PropertyDescriptor {
	t.Helper()
	for _, p := range req.RootType.Properties {
		if p.Name == name {
			return p
		}
	}
	t.Fatalf("property %q not found", name)
	return nil
}

func TestSchemaRegistrar_MetadataAndDescriptions(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, describedArticle{})
	if err := registrar.RegisterAll(context.Background(), ""); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	req := mock.capturedReq

	if req.RootType.Description != "An article with metadata" {
		t.Errorf("type description: got %q", req.RootType.Description)
	}

	// Description declared on the KEY field must be carried.
	id := propByName(t, req, "Id")
	if !id.IsKey {
		t.Error("expected Id to be the key")
	}
	if id.Description != "Primary identifier" {
		t.Errorf("key description: got %q", id.Description)
	}
	if id.IsMetadata {
		t.Error("key field must not be marked metadata")
	}

	status := propByName(t, req, "Status")
	if !status.IsMetadata {
		t.Error("expected Status IsMetadata=true")
	}
	if status.Description != "Publication status" {
		t.Errorf("metadata description: got %q", status.Description)
	}

	title := propByName(t, req, "Title")
	if title.IsMetadata {
		t.Error("expected Title IsMetadata=false")
	}
	if title.Description != "Headline" {
		t.Errorf("plain-field description: got %q", title.Description)
	}

	body := propByName(t, req, "Body")
	if body.Description != "" {
		t.Errorf("expected empty description, got %q", body.Description)
	}
}

func TestSchemaRegistrar_NoTypeDescriptionWhenInterfaceAbsent(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, undescribedArticle{})
	if err := registrar.RegisterAll(context.Background(), ""); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got := mock.capturedReq.RootType.Description; got != "" {
		t.Errorf("expected empty type description, got %q", got)
	}
}

func TestSchemaRegistrar_TypeDescriptionFromPointerEntity(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, &describedArticle{})
	if err := registrar.RegisterAll(context.Background(), ""); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got := mock.capturedReq.RootType.Description; got != "An article with metadata" {
		t.Errorf("type description via pointer: got %q", got)
	}
}

// ── enrichment target registrar tests ───────────────────────────────────────────

type enrichedArticle struct {
	Id       string `iverson_key:"true"`
	TenantId string `iverson_tenant:"true"`
	Summary  string `iverson_summary:"true"`
	Keywords string `iverson_keywords:"true"`
	Topic    string `iverson_extract:"the article's primary topic"`
	Body     string `iverson_chunk:"256:32" iverson_contextual:"true"`
	Title    string
}

func TestSchemaRegistrar_EnrichmentTargets(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, enrichedArticle{})
	if err := registrar.RegisterAll(context.Background(), ""); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	req := mock.capturedReq

	summary := propByName(t, req, "Summary")
	if !summary.IsSummaryTarget {
		t.Error("expected Summary.IsSummaryTarget=true")
	}

	keywords := propByName(t, req, "Keywords")
	if !keywords.IsKeywordsTarget {
		t.Error("expected Keywords.IsKeywordsTarget=true")
	}

	topic := propByName(t, req, "Topic")
	if topic.ExtractHint != "the article's primary topic" {
		t.Errorf("expected ExtractHint set, got %q", topic.ExtractHint)
	}

	body := propByName(t, req, "Body")
	if !body.ChunkContextual {
		t.Error("expected Body.ChunkContextual=true")
	}
	if !body.IsChunk {
		t.Error("expected Body.IsChunk=true")
	}

	// Negative control: an untagged field must carry none of the four.
	title := propByName(t, req, "Title")
	if title.IsSummaryTarget || title.IsKeywordsTarget || title.ExtractHint != "" || title.ChunkContextual {
		t.Errorf("expected Title to carry no enrichment targets, got %+v", title)
	}
}

type blankExtractHint struct {
	Id    string `iverson_key:"true"`
	Topic string `iverson_extract:"   "`
}

func TestSchemaRegistrar_BlankExtractHint_Rejected(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, blankExtractHint{})
	err := registrar.RegisterAll(context.Background(), "")
	if err == nil {
		t.Fatal("expected error for blank extract hint, got nil")
	}
}

type contextualWithoutChunk struct {
	Id    string `iverson_key:"true"`
	Title string `iverson_contextual:"true"`
}

func TestSchemaRegistrar_ContextualWithoutChunk_Rejected(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, contextualWithoutChunk{})
	err := registrar.RegisterAll(context.Background(), "")
	if err == nil {
		t.Fatal("expected error for contextual on non-chunk field, got nil")
	}
}

// ── tenant field registrar tests ────────────────────────────────────────────────

func TestSchemaRegistrar_TenantField_Set(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, registrarArticle{})
	if err := registrar.RegisterAll(context.Background(), ""); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got := mock.capturedReq.RootType.TenantField; got != "TenantId" {
		t.Errorf("expected TenantField=TenantId, got %q", got)
	}
}

// TestSchemaRegistrar_TenantField_ComposesWithSearchKey guards against the
// e4a77ff regression class: iverson_tenant is an independent tag key, not a
// mutually-exclusive kind, so a field must be able to carry both
// iverson_tenant:"true" and iverson_search_key:"N" at once. registrarArticle's
// TenantId field carries `iverson_search_key:"2" iverson_tenant:"true"`; this
// test asserts BOTH halves survive together — that TenantField is set to the
// field, AND that its search-key metadata (IsSearchKey/SearchKeyOrder) is not
// dropped by the tenant tag.
func TestSchemaRegistrar_TenantField_ComposesWithSearchKey(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, registrarArticle{})
	if err := registrar.RegisterAll(context.Background(), ""); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	req := mock.capturedReq

	if got := req.RootType.TenantField; got != "TenantId" {
		t.Errorf("expected TenantField=TenantId, got %q", got)
	}

	tenantProp := propByName(t, req, "TenantId")
	if !tenantProp.IsSearchKey {
		t.Error("expected TenantId.IsSearchKey=true; iverson_tenant must not suppress the search_key kind")
	}
	if tenantProp.SearchKeyOrder != 2 {
		t.Errorf("expected TenantId.SearchKeyOrder=2, got %d", tenantProp.SearchKeyOrder)
	}
}

type noTenantArticle struct {
	Id    string `iverson_key:"true"`
	Title string
}

func TestSchemaRegistrar_NoTenantField_Rejected(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, noTenantArticle{})
	err := registrar.RegisterAll(context.Background(), "")
	if err == nil {
		t.Fatal("expected error for missing tenant field, got nil")
	}
	if !strings.Contains(err.Error(), "noTenantArticle") {
		t.Errorf("expected error to name the type noTenantArticle, got %q", err.Error())
	}
}

type doubleTenantArticle struct {
	Id       string `iverson_key:"true"`
	TenantId string `iverson_tenant:"true"`
	OrgId    string `iverson_tenant:"true"`
}

func TestSchemaRegistrar_MultipleTenantFields_Rejected(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, doubleTenantArticle{})
	err := registrar.RegisterAll(context.Background(), "")
	if err == nil {
		t.Fatal("expected error for multiple tenant fields, got nil")
	}
	if !strings.Contains(err.Error(), "TenantId") || !strings.Contains(err.Error(), "OrgId") {
		t.Errorf("expected error to name both fields TenantId and OrgId, got %q", err.Error())
	}
}
