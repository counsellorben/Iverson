package iverson_test

import (
	"context"
	"errors"
	"strings"
	"testing"
	"time"

	"google.golang.org/grpc"

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

// mockObjectMappingServiceClient is a full pb.ObjectMappingServiceClient mock, needed
// because IversonClient.GetSchema calls through the exported MappingStub field, whose
// type is the full generated interface (not the narrower MappingClient interface above
// used by SchemaRegistrar). Only GetSchema is exercised; the rest are unused by this
// test and simply return zero values.
type mockObjectMappingServiceClient struct {
	capturedGetSchemaReq *pb.GetSchemaRequest
	getSchemaResp        *pb.GetSchemaResponse
	getSchemaErr         error
}

func (m *mockObjectMappingServiceClient) Get(context.Context, *pb.MappingGetRequest, ...grpc.CallOption) (*pb.MappingResponse, error) {
	return nil, nil
}

func (m *mockObjectMappingServiceClient) Post(context.Context, *pb.MappingWriteRequest, ...grpc.CallOption) (*pb.MappingResponse, error) {
	return nil, nil
}

func (m *mockObjectMappingServiceClient) Update(context.Context, *pb.MappingWriteRequest, ...grpc.CallOption) (*pb.MappingResponse, error) {
	return nil, nil
}

func (m *mockObjectMappingServiceClient) Delete(context.Context, *pb.MappingDeleteRequest, ...grpc.CallOption) (*pb.MappingDeleteResponse, error) {
	return nil, nil
}

func (m *mockObjectMappingServiceClient) RegisterSchema(context.Context, *pb.SchemaRequest, ...grpc.CallOption) (*pb.SchemaResponse, error) {
	return nil, nil
}

func (m *mockObjectMappingServiceClient) GetSchema(_ context.Context, req *pb.GetSchemaRequest, _ ...grpc.CallOption) (*pb.GetSchemaResponse, error) {
	m.capturedGetSchemaReq = req
	if m.getSchemaErr != nil {
		return nil, m.getSchemaErr
	}
	return m.getSchemaResp, nil
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

	if err := registrar.RegisterAll(context.Background(), "trace-1", nil); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if mock.capturedReq == nil {
		t.Fatal("no request captured")
	}
}

func TestSchemaRegistrar_RegisterAll_TypeName(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, registrarArticle{})
	_ = registrar.RegisterAll(context.Background(), "", nil)

	if mock.capturedReq.RootType.TypeName != "registrarArticle" {
		t.Errorf("expected TypeName=registrarArticle, got %q", mock.capturedReq.RootType.TypeName)
	}
}

func TestSchemaRegistrar_RegisterAll_TraceId(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, registrarArticle{})
	_ = registrar.RegisterAll(context.Background(), "my-trace", nil)

	if mock.capturedReq.TraceId != "my-trace" {
		t.Errorf("expected trace_id=my-trace, got %q", mock.capturedReq.TraceId)
	}
}

func TestSchemaRegistrar_RegisterAll_Properties(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, registrarArticle{})
	_ = registrar.RegisterAll(context.Background(), "", nil)

	props := mock.capturedReq.RootType.Properties
	// Should have 9 properties (Id, TenantId, Title, Body, Category, WordCount, PublishedAt,
	// Summary, plus the synthesized AuthorId foreign-key property for the many_to_one relation).
	if len(props) != 9 {
		t.Errorf("expected 9 properties, got %d", len(props))
	}
}

func TestSchemaRegistrar_RegisterAll_KeyField(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, registrarArticle{})
	_ = registrar.RegisterAll(context.Background(), "", nil)

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
	_ = registrar.RegisterAll(context.Background(), "", nil)

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
	_ = registrar.RegisterAll(context.Background(), "", nil)

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
	_ = registrar.RegisterAll(context.Background(), "", nil)

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
	_ = registrar.RegisterAll(context.Background(), "", nil)

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
	_ = registrar.RegisterAll(context.Background(), "", nil)

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
	err := registrar.RegisterAll(context.Background(), "", nil)
	if err == nil {
		t.Fatal("expected error, got nil")
	}
}

func TestSchemaRegistrar_RegisterAll_ServerError(t *testing.T) {
	mock := &mockMappingClient{
		response: &pb.SchemaResponse{Success: false, Error: "table already exists"},
	}
	registrar := iverson.NewSchemaRegistrar(mock, registrarArticle{})
	err := registrar.RegisterAll(context.Background(), "", nil)
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
	if err := registrar.RegisterAll(context.Background(), "", nil); err != nil {
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
	if err := registrar.RegisterAll(context.Background(), "", nil); err != nil {
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
	if err := registrar.RegisterAll(context.Background(), "", nil); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got := mock.capturedReq.RootType.Description; got != "" {
		t.Errorf("expected empty type description, got %q", got)
	}
}

func TestSchemaRegistrar_TypeDescriptionFromPointerEntity(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, &describedArticle{})
	if err := registrar.RegisterAll(context.Background(), "", nil); err != nil {
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
	if err := registrar.RegisterAll(context.Background(), "", nil); err != nil {
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
	err := registrar.RegisterAll(context.Background(), "", nil)
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
	err := registrar.RegisterAll(context.Background(), "", nil)
	if err == nil {
		t.Fatal("expected error for contextual on non-chunk field, got nil")
	}
}

// ── tenant field registrar tests ────────────────────────────────────────────────

func TestSchemaRegistrar_TenantField_Set(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, registrarArticle{})
	if err := registrar.RegisterAll(context.Background(), "", nil); err != nil {
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
	if err := registrar.RegisterAll(context.Background(), "", nil); err != nil {
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
	err := registrar.RegisterAll(context.Background(), "", nil)
	if err == nil {
		t.Fatal("expected error for missing tenant field, got nil")
	}
	if !strings.Contains(err.Error(), "noTenantArticle") {
		t.Errorf("expected error to name the type noTenantArticle, got %q", err.Error())
	}
}

// ── declaration composability registrar tests ───────────────────────────────────

type ComposedDeclArticle struct {
	Id       string `iverson_key:"true"`
	TenantId string `iverson_tenant:"true"`
	Body     string `iverson_large_field:"true" iverson_chunk:"256:32"`
}

// TestSchemaRegistrar_ComposedDeclarations_LargeFieldAndChunk asserts that
// iverson_large_field and iverson_chunk, both present on the same field, survive
// together through to the built PropertyDescriptor. Both flag assertions are the
// point — a test asserting only IsChunk would pass while large_field was silently
// dropped. The windowing values are non-default so they cannot pass vacuously
// against the 512/64 defaults.
func TestSchemaRegistrar_ComposedDeclarations_LargeFieldAndChunk(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, ComposedDeclArticle{})
	if err := registrar.RegisterAll(context.Background(), "", nil); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	req := mock.capturedReq

	body := propByName(t, req, "Body")
	if !body.IsLargeField {
		t.Error("expected Body.IsLargeField=true")
	}
	if !body.IsChunk {
		t.Error("expected Body.IsChunk=true")
	}
	if body.ChunkMaxTokens != 256 {
		t.Errorf("expected ChunkMaxTokens=256, got %d", body.ChunkMaxTokens)
	}
	if body.ChunkOverlap != 32 {
		t.Errorf("expected ChunkOverlap=32, got %d", body.ChunkOverlap)
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
	err := registrar.RegisterAll(context.Background(), "", nil)
	if err == nil {
		t.Fatal("expected error for multiple tenant fields, got nil")
	}
	if !strings.Contains(err.Error(), "TenantId") || !strings.Contains(err.Error(), "OrgId") {
		t.Errorf("expected error to name both fields TenantId and OrgId, got %q", err.Error())
	}
}

type arrayFieldsArticle struct {
	Id       string `iverson_key:"true"`
	TenantId string `iverson_tenant:"true"`
	Tags     []string
	Counts   []int
	Blob     []byte
}

func TestSchemaRegistrar_RegisterAll_ArrayFields(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, arrayFieldsArticle{})
	if err := registrar.RegisterAll(context.Background(), "", nil); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	props := make(map[string]*pb.PropertyDescriptor)
	for _, p := range mock.capturedReq.RootType.Properties {
		props[p.Name] = p
	}

	tags, ok := props["Tags"]
	if !ok {
		t.Fatal("no Tags property found")
	}
	if !tags.IsArray {
		t.Error("expected Tags.IsArray=true")
	}
	if tags.ClrType != pb.ClrType_CLR_STRING {
		t.Errorf("expected Tags.ClrType=CLR_STRING, got %v", tags.ClrType)
	}

	counts, ok := props["Counts"]
	if !ok {
		t.Fatal("no Counts property found")
	}
	if !counts.IsArray {
		t.Error("expected Counts.IsArray=true")
	}
	if counts.ClrType != pb.ClrType_CLR_INT64 {
		t.Errorf("expected Counts.ClrType=CLR_INT64, got %v", counts.ClrType)
	}

	blob, ok := props["Blob"]
	if !ok {
		t.Fatal("no Blob property found")
	}
	if blob.IsArray {
		t.Error("expected Blob.IsArray=false")
	}
	if blob.ClrType != pb.ClrType_CLR_BYTES {
		t.Errorf("expected Blob.ClrType=CLR_BYTES, got %v", blob.ClrType)
	}
}

type nestedArrayArticle struct {
	Id       string `iverson_key:"true"`
	TenantId string `iverson_tenant:"true"`
	Matrix   [][]string
}

type customElement struct {
	Name string
}

type unsupportedElementArticle struct {
	Id       string `iverson_key:"true"`
	TenantId string `iverson_tenant:"true"`
	Widgets  []customElement
}

// Silently collapsing [][]string to a 1-D TEXT[] column would register a schema the server
// accepts and json_populate_record then fails on at the first insert.
func TestSchemaRegistrar_RegisterAll_NestedArrayRejected(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, nestedArrayArticle{})
	err := registrar.RegisterAll(context.Background(), "", nil)
	if err == nil {
		t.Fatal("expected registration to fail for a nested array field")
	}
	if !strings.Contains(err.Error(), "Matrix") || !strings.Contains(err.Error(), "nested array") {
		t.Errorf("expected error naming Matrix and nested array, got %q", err.Error())
	}
}

type byteArrayArticle struct {
	Id       string `iverson_key:"true"`
	TenantId string `iverson_tenant:"true"`
	Blobs    [][]byte
}

// [][]byte is the one nested shape that is legitimate: its inner slice is the scalar
// bytes type, not a nested array, so it must map to BYTEA[] rather than be rejected.
func TestSchemaRegistrar_RegisterAll_NestedByteArrayAllowed(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, byteArrayArticle{})
	if err := registrar.RegisterAll(context.Background(), "", nil); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	props := make(map[string]*pb.PropertyDescriptor)
	for _, p := range mock.capturedReq.RootType.Properties {
		props[p.Name] = p
	}

	blobs, ok := props["Blobs"]
	if !ok {
		t.Fatal("no Blobs property found")
	}
	if !blobs.IsArray {
		t.Error("expected Blobs.IsArray=true")
	}
	if blobs.ClrType != pb.ClrType_CLR_BYTES {
		t.Errorf("expected Blobs.ClrType=CLR_BYTES, got %v", blobs.ClrType)
	}
}

func TestSchemaRegistrar_RegisterAll_UnsupportedElementTypeRejected(t *testing.T) {
	mock := &mockMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, unsupportedElementArticle{})
	err := registrar.RegisterAll(context.Background(), "", nil)
	if err == nil {
		t.Fatal("expected registration to fail for an unsupported array element type")
	}
	if !strings.Contains(err.Error(), "Widgets") || !strings.Contains(err.Error(), "customElement") {
		t.Errorf("expected error naming Widgets and customElement, got %q", err.Error())
	}
}

// ── GetSchema ─────────────────────────────────────────────────────────────────

func TestIversonClient_GetSchema_ReturnsTypes(t *testing.T) {
	mock := &mockObjectMappingServiceClient{
		getSchemaResp: &pb.GetSchemaResponse{
			Types: []*pb.SchemaType{
				{
					Name: "Article",
					Fields: []*pb.SchemaField{
						{
							Name:           "Category",
							ClrType:        pb.ClrType_CLR_STRING,
							IsSearchKey:    true,
							SearchKeyOrder: 0,
						},
						{
							Name:           "PublishedAt",
							ClrType:        pb.ClrType_CLR_DATETIME,
							IsSearchKey:    true,
							SearchKeyOrder: 1,
						},
					},
				},
			},
		},
	}
	client := &iverson.IversonClient{MappingStub: mock}

	types, err := client.GetSchema(context.Background(), "trace-1")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(types) != 1 {
		t.Fatalf("expected 1 type, got %d", len(types))
	}
	if types[0].Name != "Article" {
		t.Errorf("expected type name Article, got %q", types[0].Name)
	}
	if len(types[0].Fields) != 2 {
		t.Fatalf("expected 2 fields, got %d", len(types[0].Fields))
	}

	category := types[0].Fields[0]
	if category.Name != "Category" {
		t.Errorf("expected field name Category, got %q", category.Name)
	}
	if category.ClrType != pb.ClrType_CLR_STRING {
		t.Errorf("expected ClrType CLR_STRING, got %v", category.ClrType)
	}
	if !category.IsSearchKey {
		t.Error("expected Category.IsSearchKey=true")
	}
	if category.SearchKeyOrder != 0 {
		t.Errorf("expected Category.SearchKeyOrder=0, got %d", category.SearchKeyOrder)
	}

	publishedAt := types[0].Fields[1]
	if publishedAt.Name != "PublishedAt" {
		t.Errorf("expected field name PublishedAt, got %q", publishedAt.Name)
	}
	if publishedAt.ClrType != pb.ClrType_CLR_DATETIME {
		t.Errorf("expected ClrType CLR_DATETIME, got %v", publishedAt.ClrType)
	}
	if publishedAt.SearchKeyOrder != 1 {
		t.Errorf("expected PublishedAt.SearchKeyOrder=1, got %d", publishedAt.SearchKeyOrder)
	}

	if mock.capturedGetSchemaReq == nil {
		t.Fatal("no request captured")
	}
	if mock.capturedGetSchemaReq.TraceId != "trace-1" {
		t.Errorf("expected TraceId=trace-1, got %q", mock.capturedGetSchemaReq.TraceId)
	}
}

func TestIversonClient_GetSchema_PropagatesError(t *testing.T) {
	mock := &mockObjectMappingServiceClient{getSchemaErr: errors.New("boom")}
	client := &iverson.IversonClient{MappingStub: mock}

	_, err := client.GetSchema(context.Background(), "")
	if err == nil {
		t.Fatal("expected error")
	}
	if !strings.Contains(err.Error(), "boom") {
		t.Errorf("expected error to wrap %q, got %q", "boom", err.Error())
	}
}

// ── RegisterAll authorization rules ────────────────────────────────────────────────

// recordingMappingClient records every SchemaRequest it receives, keyed by TypeName,
// so a test can assert what each of several registered types was sent — a plain
// "last captured request" field (as mockMappingClient above uses) can't distinguish
// per-type payloads when RegisterAll iterates over multiple entities.
type recordingMappingClient struct {
	response *pb.SchemaResponse
	byType   map[string]*pb.SchemaRequest
}

func (m *recordingMappingClient) RegisterSchema(_ context.Context, req *pb.SchemaRequest) (*pb.SchemaResponse, error) {
	if m.byType == nil {
		m.byType = make(map[string]*pb.SchemaRequest)
	}
	m.byType[req.RootType.TypeName] = req
	return m.response, nil
}

type ruleArticle struct {
	Id       string `iverson_key:"true"`
	TenantId string `iverson_tenant:"true"`
}

type ruleAuthor struct {
	Id       string `iverson_key:"true"`
	TenantId string `iverson_tenant:"true"`
}

type ruleUnrestricted struct {
	Id       string `iverson_key:"true"`
	TenantId string `iverson_tenant:"true"`
}

func TestSchemaRegistrar_RegisterAll_PerTypeRules(t *testing.T) {
	mock := &recordingMappingClient{response: &pb.SchemaResponse{Success: true}}
	registrar := iverson.NewSchemaRegistrar(mock, ruleArticle{}, ruleAuthor{}, ruleUnrestricted{})

	articleRules := &pb.AuthorizationRules{OwnerField: "AuthorId"}
	authorRules := &pb.AuthorizationRules{OwnerField: "EditorId"}

	err := registrar.RegisterAll(context.Background(), "", map[string]*pb.AuthorizationRules{
		"ruleArticle": articleRules,
		"ruleAuthor":  authorRules,
	})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	articleReq, ok := mock.byType["ruleArticle"]
	if !ok {
		t.Fatal("no request captured for ruleArticle")
	}
	if articleReq.RootType.Authorization.GetOwnerField() != "AuthorId" {
		t.Errorf("ruleArticle authorization OwnerField = %q, want %q", articleReq.RootType.Authorization.GetOwnerField(), "AuthorId")
	}

	authorReq, ok := mock.byType["ruleAuthor"]
	if !ok {
		t.Fatal("no request captured for ruleAuthor")
	}
	if authorReq.RootType.Authorization.GetOwnerField() != "EditorId" {
		t.Errorf("ruleAuthor authorization OwnerField = %q, want %q", authorReq.RootType.Authorization.GetOwnerField(), "EditorId")
	}

	unrestrictedReq, ok := mock.byType["ruleUnrestricted"]
	if !ok {
		t.Fatal("no request captured for ruleUnrestricted")
	}
	if unrestrictedReq.RootType.Authorization != nil {
		t.Errorf("ruleUnrestricted authorization = %v, want nil", unrestrictedReq.RootType.Authorization)
	}
}
