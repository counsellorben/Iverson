package iverson_test

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/iverson/clients/go/iverson"
	pb "github.com/iverson/clients/go/generated"
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
	Id          string    `iverson:"key"`
	Title       string    `iverson:"embedding"`
	Body        string    `iverson:"large_field"`
	Category    string    `iverson:"search_key:0"`
	WordCount   int
	PublishedAt time.Time `iverson:"search_key:1"`
	AuthorId    string    `iverson:"many_to_one:Author"`
	Summary     string    `iverson:"chunk:256:32"`
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
	// Should have 7 properties (Id, Title, Body, Category, WordCount, PublishedAt, Summary)
	if len(props) != 7 {
		t.Errorf("expected 7 properties, got %d", len(props))
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
	if len(searchKeys) != 2 {
		t.Fatalf("expected 2 search keys, got %d", len(searchKeys))
	}
	if searchKeys["Category"] != 0 {
		t.Errorf("Category order should be 0, got %d", searchKeys["Category"])
	}
	if searchKeys["PublishedAt"] != 1 {
		t.Errorf("PublishedAt order should be 1, got %d", searchKeys["PublishedAt"])
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
		Id   string `iverson:"key"`
		Name string
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
