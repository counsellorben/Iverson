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
	Id       string   `iverson_key:"true"`
	TenantId string   `iverson_tenant:"true"`
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

// propsByName indexes a built request's synthesized properties for assertion.
func propsByName(t *testing.T, e interface{}) map[string]*pb.PropertyDescriptor {
	t.Helper()
	r := NewSchemaRegistrar(nil, e)
	req, err := r.buildRequest(e, "trace")
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
	if p.ClrType != pb.ClrType_CLR_STRING {
		t.Errorf("%s.ClrType = %v, want CLR_STRING", name, p.ClrType)
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

func TestBuildRequest_ManyToOne_CorrectlyNamedFieldRegisters(t *testing.T) {
	r := NewSchemaRegistrar(nil, Article{})
	_, err := r.buildRequest(Article{}, "trace")
	if err != nil {
		t.Fatalf("expected no error for correctly-named AuthorId field: %v", err)
	}
}

func TestBuildRequest_ManyToOne_WronglyNamedFieldRejected(t *testing.T) {
	r := NewSchemaRegistrar(nil, WriterAuthor{})
	_, err := r.buildRequest(WriterAuthor{}, "trace")
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
