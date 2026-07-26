package iverson

import (
	"context"
	"fmt"
	"io"
	"reflect"
	"strconv"
	"time"

	"google.golang.org/grpc"
	"google.golang.org/protobuf/types/known/structpb"

	pb "github.com/iverson/clients/go/generated"
)

// PersistenceClient is the interface for ObjectPersistenceService stub.
type PersistenceClient interface {
	Post(ctx context.Context, req *pb.PersistRequest) (*pb.PersistResponse, error)
	Update(ctx context.Context, req *pb.PersistRequest) (*pb.PersistResponse, error)
}

// RetrievalClient is the interface for ObjectRetrievalService stub.
type RetrievalClient interface {
	Get(ctx context.Context, req *pb.RetrievalRequest) (*pb.RetrievalResponse, error)
	GetMany(ctx context.Context, req *pb.RetrievalManyRequest) (RetrievalStream, error)
}

// MappingDeleteClient is the interface for delete operations via ObjectMappingService.
type MappingDeleteClient interface {
	Delete(ctx context.Context, req *pb.MappingDeleteRequest) (*pb.MappingDeleteResponse, error)
}

// RetrievalStream is the interface for the streaming GetMany response.
type RetrievalStream interface {
	Recv() (*pb.RetrievalResponse, error)
}

// SearchClient is the interface for ObjectSearchService stub.
type SearchClient interface {
	Search(ctx context.Context, req *pb.SearchRequest) (SearchStream, error)
	SearchSimilar(ctx context.Context, req *pb.SearchSimilarRequest) (SearchStream, error)
	SearchChunks(ctx context.Context, req *pb.SearchChunksRequest) (ChunkSearchStream, error)
	Aggregate(ctx context.Context, req *pb.AggregateRequest) (*pb.AggregateResponse, error)
	GroupBy(ctx context.Context, req *pb.GroupByRequest) (SearchStream, error)
	Pipeline(ctx context.Context, req *pb.PipelineRequest) (SearchStream, error)
}

// SearchStream is the interface for the streaming Search/SearchSimilar/GroupBy/Pipeline
// response, all of which stream *pb.SearchResponse.
type SearchStream interface {
	Recv() (*pb.SearchResponse, error)
}

// ChunkSearchStream is the interface for the streaming SearchChunks response.
type ChunkSearchStream interface {
	Recv() (*pb.ChunkSearchResponse, error)
}

// IversonClient holds gRPC connections to the Iverson server services.
type IversonClient struct {
	mappingConn     *grpc.ClientConn
	persistenceConn *grpc.ClientConn
	retrievalConn   *grpc.ClientConn
	searchConn      *grpc.ClientConn

	MappingStub     pb.ObjectMappingServiceClient
	PersistenceStub pb.ObjectPersistenceServiceClient
	RetrievalStub   pb.ObjectRetrievalServiceClient
	SearchStub      pb.ObjectSearchServiceClient
}

// NewIversonClient creates an IversonClient pointing at a single gRPC endpoint.
// The same connection is reused for all services.
func NewIversonClient(target string, opts ...grpc.DialOption) (*IversonClient, error) {
	if len(opts) == 0 {
		opts = []grpc.DialOption{grpc.WithInsecure()} //nolint:staticcheck
	}
	conn, err := grpc.Dial(target, opts...) //nolint:staticcheck
	if err != nil {
		return nil, fmt.Errorf("grpc.Dial(%q): %w", target, err)
	}
	return &IversonClient{
		mappingConn:     conn,
		persistenceConn: conn,
		retrievalConn:   conn,
		searchConn:      conn,
		MappingStub:     pb.NewObjectMappingServiceClient(conn),
		PersistenceStub: pb.NewObjectPersistenceServiceClient(conn),
		RetrievalStub:   pb.NewObjectRetrievalServiceClient(conn),
		SearchStub:      pb.NewObjectSearchServiceClient(conn),
	}, nil
}

// Close closes all underlying gRPC connections.
func (c *IversonClient) Close() error {
	return c.mappingConn.Close()
}

// coordinatorDeps holds injectable service clients (real or mock).
type coordinatorDeps struct {
	persistence PersistenceClient
	retrieval   RetrievalClient
	mapping     MappingDeleteClient
	search      SearchClient
}

// EntityCoordinator[T] is a high-level coordinator for a single entity type T.
// T must be a struct whose fields carry `iverson` struct tags.
type EntityCoordinator[T any] struct {
	deps     coordinatorDeps
	typeName string
	keyField string
}

// NewEntityCoordinator creates an EntityCoordinator using an IversonClient.
// entity is used only for type reflection — pass a zero value (e.g. Article{}).
func NewEntityCoordinator[T any](client *IversonClient, entity T) (*EntityCoordinator[T], error) {
	meta, err := InspectType(entity)
	if err != nil {
		return nil, err
	}
	keyField := ""
	for _, f := range meta.Fields {
		if f.Kind == KindKey {
			keyField = f.Name
			break
		}
	}

	return &EntityCoordinator[T]{
		deps: coordinatorDeps{
			persistence: &persistenceAdapter{client.PersistenceStub},
			retrieval:   &retrievalAdapter{client.RetrievalStub},
			mapping:     &mappingDeleteAdapter{client.MappingStub},
			search:      &searchAdapter{client.SearchStub},
		},
		typeName: meta.TypeName,
		keyField: keyField,
	}, nil
}

// newEntityCoordinatorWithDeps creates an EntityCoordinator with injected deps (for testing).
func newEntityCoordinatorWithDeps[T any](deps coordinatorDeps, entity T) (*EntityCoordinator[T], error) {
	meta, err := InspectType(entity)
	if err != nil {
		return nil, err
	}
	keyField := ""
	for _, f := range meta.Fields {
		if f.Kind == KindKey {
			keyField = f.Name
			break
		}
	}
	return &EntityCoordinator[T]{
		deps:     deps,
		typeName: meta.TypeName,
		keyField: keyField,
	}, nil
}

// Persist persists a new entity and returns the assigned key.
func (c *EntityCoordinator[T]) Persist(ctx context.Context, entity T) (string, error) {
	payload, err := entityToStruct(entity)
	if err != nil {
		return "", err
	}
	resp, err := c.deps.persistence.Post(ctx, &pb.PersistRequest{
		TypeName: c.typeName,
		Payload:  payload,
	})
	if err != nil {
		return "", fmt.Errorf("Persist: %w", err)
	}
	if !resp.Success {
		return "", fmt.Errorf("Persist: %s", resp.Error)
	}
	return resp.Key, nil
}

// Update updates an existing entity.
func (c *EntityCoordinator[T]) Update(ctx context.Context, entity T) error {
	payload, err := entityToStruct(entity)
	if err != nil {
		return err
	}
	resp, err := c.deps.persistence.Update(ctx, &pb.PersistRequest{
		TypeName: c.typeName,
		Payload:  payload,
	})
	if err != nil {
		return fmt.Errorf("Update: %w", err)
	}
	if !resp.Success {
		return fmt.Errorf("Update: %s", resp.Error)
	}
	return nil
}

// Delete deletes an entity by key.
func (c *EntityCoordinator[T]) Delete(ctx context.Context, id string) error {
	resp, err := c.deps.mapping.Delete(ctx, &pb.MappingDeleteRequest{
		TypeName: c.typeName,
		Key:      id,
	})
	if err != nil {
		return fmt.Errorf("Delete: %w", err)
	}
	if !resp.Success {
		return fmt.Errorf("Delete: %s", resp.Error)
	}
	return nil
}

// Get retrieves an entity by key. Returns an error if not found.
func (c *EntityCoordinator[T]) Get(ctx context.Context, id string) (T, error) {
	var zero T
	resp, err := c.deps.retrieval.Get(ctx, &pb.RetrievalRequest{
		TypeName: c.typeName,
		Key:      id,
	})
	if err != nil {
		return zero, fmt.Errorf("Get: %w", err)
	}
	if !resp.Found {
		return zero, fmt.Errorf("entity not found: %s", id)
	}
	entity, err := structToEntity[T](resp.Data)
	if err != nil {
		return zero, err
	}
	return entity, nil
}

// GetMany retrieves multiple entities by key. Entities not found are omitted.
func (c *EntityCoordinator[T]) GetMany(ctx context.Context, ids []string) ([]T, error) {
	stream, err := c.deps.retrieval.GetMany(ctx, &pb.RetrievalManyRequest{
		TypeName: c.typeName,
		Keys:     ids,
	})
	if err != nil {
		return nil, fmt.Errorf("GetMany: %w", err)
	}

	var results []T
	for {
		resp, err := stream.Recv()
		if err == io.EOF {
			break
		}
		if err != nil {
			return nil, fmt.Errorf("GetMany stream: %w", err)
		}
		if resp.Found {
			entity, err := structToEntity[T](resp.Data)
			if err != nil {
				return nil, err
			}
			results = append(results, entity)
		}
	}
	return results, nil
}

// ── Object Search ──────────────────────────────────────────────────────────
// DSL-driven; returns streamed results with relevance scores.

// SearchResult pairs an entity with its relevance score, as returned by Search and
// SearchSimilar.
type SearchResult[T any] struct {
	Entity T
	Score  float32
}

// Search executes a DSL-driven search request and returns matching entities with
// relevance scores.
func (c *EntityCoordinator[T]) Search(ctx context.Context, req *pb.SearchRequest) ([]SearchResult[T], error) {
	stream, err := c.deps.search.Search(ctx, req)
	if err != nil {
		return nil, fmt.Errorf("Search: %w", err)
	}

	var results []SearchResult[T]
	for {
		resp, err := stream.Recv()
		if err == io.EOF {
			break
		}
		if err != nil {
			return nil, fmt.Errorf("Search stream: %w", err)
		}
		entity, err := structToEntity[T](resp.Data)
		if err != nil {
			return nil, err
		}
		results = append(results, SearchResult[T]{Entity: entity, Score: resp.Score})
	}
	return results, nil
}

// SearchSimilar executes a semantic (vector embedding) similarity search and returns
// matching entities with relevance scores.
func (c *EntityCoordinator[T]) SearchSimilar(ctx context.Context, req *pb.SearchSimilarRequest) ([]SearchResult[T], error) {
	stream, err := c.deps.search.SearchSimilar(ctx, req)
	if err != nil {
		return nil, fmt.Errorf("SearchSimilar: %w", err)
	}

	var results []SearchResult[T]
	for {
		resp, err := stream.Recv()
		if err == io.EOF {
			break
		}
		if err != nil {
			return nil, fmt.Errorf("SearchSimilar stream: %w", err)
		}
		entity, err := structToEntity[T](resp.Data)
		if err != nil {
			return nil, err
		}
		results = append(results, SearchResult[T]{Entity: entity, Score: resp.Score})
	}
	return results, nil
}

// SearchChunks executes a chunk/RAG search and returns matching passage chunks,
// unconverted (the response is already a flat, typed message).
func (c *EntityCoordinator[T]) SearchChunks(ctx context.Context, req *pb.SearchChunksRequest) ([]*pb.ChunkSearchResponse, error) {
	stream, err := c.deps.search.SearchChunks(ctx, req)
	if err != nil {
		return nil, fmt.Errorf("SearchChunks: %w", err)
	}

	var results []*pb.ChunkSearchResponse
	for {
		resp, err := stream.Recv()
		if err == io.EOF {
			break
		}
		if err != nil {
			return nil, fmt.Errorf("SearchChunks stream: %w", err)
		}
		results = append(results, resp)
	}
	return results, nil
}

// GroupBy executes a compound GROUP BY aggregation and returns one untyped row per output
// group. Columns are aggregated/aliased and don't match T's own fields, so results come
// back as maps rather than typed entities.
func (c *EntityCoordinator[T]) GroupBy(ctx context.Context, req *pb.GroupByRequest) ([]map[string]any, error) {
	stream, err := c.deps.search.GroupBy(ctx, req)
	if err != nil {
		return nil, fmt.Errorf("GroupBy: %w", err)
	}

	var results []map[string]any
	for {
		resp, err := stream.Recv()
		if err == io.EOF {
			break
		}
		if err != nil {
			return nil, fmt.Errorf("GroupBy stream: %w", err)
		}
		results = append(results, structToMap(resp.Data))
	}
	return results, nil
}

// Pipeline executes a CTE-chain pipeline and returns one untyped row per output row.
// Columns depend on the pipeline's final step, so results come back as maps rather than
// typed entities, same as GroupBy.
func (c *EntityCoordinator[T]) Pipeline(ctx context.Context, req *pb.PipelineRequest) ([]map[string]any, error) {
	stream, err := c.deps.search.Pipeline(ctx, req)
	if err != nil {
		return nil, fmt.Errorf("Pipeline: %w", err)
	}

	var results []map[string]any
	for {
		resp, err := stream.Recv()
		if err == io.EOF {
			break
		}
		if err != nil {
			return nil, fmt.Errorf("Pipeline stream: %w", err)
		}
		results = append(results, structToMap(resp.Data))
	}
	return results, nil
}

// Aggregate executes an aggregation request and returns the full AggregateResponse (one
// AggregationResult per requested AggregationSpec), unconverted.
func (c *EntityCoordinator[T]) Aggregate(ctx context.Context, req *pb.AggregateRequest) (*pb.AggregateResponse, error) {
	resp, err := c.deps.search.Aggregate(ctx, req)
	if err != nil {
		return nil, fmt.Errorf("Aggregate: %w", err)
	}
	return resp, nil
}

// ── Struct <-> entity conversion ──────────────────────────────────────────────

// entityToStruct converts a struct to a google.protobuf.Struct.
// Field names are kept as-is (PascalCase).
func entityToStruct(entity interface{}) (*structpb.Struct, error) {
	v := reflect.ValueOf(entity)
	t := reflect.TypeOf(entity)
	if t.Kind() == reflect.Ptr {
		v = v.Elem()
		t = t.Elem()
	}

	fields := make(map[string]*structpb.Value, t.NumField())

	for i := 0; i < t.NumField(); i++ {
		sf := t.Field(i)
		fv := v.Field(i)

		// Skip relation fields
		tag := sf.Tag.Get(TagKey)
		if tag != "" {
			fm, _ := ParseTag(sf.Name, tag)
			switch fm.Kind {
			case KindManyToOne, KindManyToMany, KindOneToMany, KindOneToOne:
				continue
			}
		}

		val, err := goValueToProtoValue(fv)
		if err != nil {
			return nil, fmt.Errorf("field %s: %w", sf.Name, err)
		}
		if val != nil {
			fields[sf.Name] = val
		}
	}

	return &structpb.Struct{Fields: fields}, nil
}

// goValueToProtoValue converts a reflect.Value to a structpb.Value.
func goValueToProtoValue(v reflect.Value) (*structpb.Value, error) {
	switch v.Kind() {
	case reflect.String:
		return structpb.NewStringValue(v.String()), nil
	case reflect.Bool:
		return structpb.NewBoolValue(v.Bool()), nil
	case reflect.Int, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64:
		return structpb.NewNumberValue(float64(v.Int())), nil
	case reflect.Uint, reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64:
		return structpb.NewNumberValue(float64(v.Uint())), nil
	case reflect.Float32, reflect.Float64:
		return structpb.NewNumberValue(v.Float()), nil
	case reflect.Ptr:
		if v.IsNil() {
			return structpb.NewNullValue(), nil
		}
		return goValueToProtoValue(v.Elem())
	case reflect.Struct:
		// time.Time → RFC3339 string
		if t, ok := v.Interface().(time.Time); ok {
			if t.IsZero() {
				return structpb.NewNullValue(), nil
			}
			return structpb.NewStringValue(t.Format(time.RFC3339Nano)), nil
		}
		return structpb.NewNullValue(), nil
	default:
		return structpb.NewNullValue(), nil
	}
}

// structToEntity converts a google.protobuf.Struct to a Go struct of type T.
func structToEntity[T any](s *structpb.Struct) (T, error) {
	var zero T
	t := reflect.TypeOf(zero)
	if t.Kind() == reflect.Ptr {
		t = t.Elem()
	}

	v := reflect.New(t).Elem()

	for i := 0; i < t.NumField(); i++ {
		sf := t.Field(i)
		pbVal, ok := s.Fields[sf.Name]
		if !ok {
			continue
		}
		fv := v.Field(i)
		if err := protoValueToGoValue(pbVal, fv, sf.Type); err != nil {
			return zero, fmt.Errorf("field %s: %w", sf.Name, err)
		}
	}

	return v.Interface().(T), nil
}

// protoValueToGoValue sets a struct field from a structpb.Value.
func protoValueToGoValue(pbVal *structpb.Value, target reflect.Value, targetType reflect.Type) error {
	switch v := pbVal.Kind.(type) {
	case *structpb.Value_StringValue:
		switch targetType.Kind() {
		case reflect.String:
			target.SetString(v.StringValue)
		case reflect.Struct:
			if targetType.PkgPath() == "time" && targetType.Name() == "Time" {
				t, err := time.Parse(time.RFC3339Nano, v.StringValue)
				if err != nil {
					// try other formats
					t, err = time.Parse(time.RFC3339, v.StringValue)
					if err != nil {
						return fmt.Errorf("cannot parse time %q: %w", v.StringValue, err)
					}
				}
				target.Set(reflect.ValueOf(t))
			}
		}
	case *structpb.Value_NumberValue:
		switch targetType.Kind() {
		case reflect.Int, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64:
			target.SetInt(int64(v.NumberValue))
		case reflect.Uint, reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64:
			target.SetUint(uint64(v.NumberValue))
		case reflect.Float32, reflect.Float64:
			target.SetFloat(v.NumberValue)
		case reflect.String:
			target.SetString(strconv.FormatFloat(v.NumberValue, 'f', -1, 64))
		}
	case *structpb.Value_BoolValue:
		if targetType.Kind() == reflect.Bool {
			target.SetBool(v.BoolValue)
		}
	}
	return nil
}

// structToMap converts a google.protobuf.Struct to an untyped map, for results whose
// columns are aggregated/aliased and don't correspond to any single entity's fields
// (GroupBy, Pipeline) — unlike structToEntity[T], it isn't driven by a target reflect.Type.
func structToMap(s *structpb.Struct) map[string]any {
	if s == nil {
		return nil
	}
	m := make(map[string]any, len(s.Fields))
	for name, pbVal := range s.Fields {
		switch v := pbVal.Kind.(type) {
		case *structpb.Value_StringValue:
			m[name] = v.StringValue
		case *structpb.Value_NumberValue:
			m[name] = v.NumberValue
		case *structpb.Value_BoolValue:
			m[name] = v.BoolValue
		default:
			m[name] = nil
		}
	}
	return m
}

// ── Adapters wrapping generated stubs to satisfy interfaces ───────────────────

type persistenceAdapter struct {
	stub pb.ObjectPersistenceServiceClient
}

func (a *persistenceAdapter) Post(ctx context.Context, req *pb.PersistRequest) (*pb.PersistResponse, error) {
	return a.stub.Post(ctx, req)
}

func (a *persistenceAdapter) Update(ctx context.Context, req *pb.PersistRequest) (*pb.PersistResponse, error) {
	return a.stub.Update(ctx, req)
}

type retrievalAdapter struct {
	stub pb.ObjectRetrievalServiceClient
}

func (a *retrievalAdapter) Get(ctx context.Context, req *pb.RetrievalRequest) (*pb.RetrievalResponse, error) {
	return a.stub.Get(ctx, req)
}

func (a *retrievalAdapter) GetMany(ctx context.Context, req *pb.RetrievalManyRequest) (RetrievalStream, error) {
	return a.stub.GetMany(ctx, req)
}

type mappingDeleteAdapter struct {
	stub pb.ObjectMappingServiceClient
}

func (a *mappingDeleteAdapter) Delete(ctx context.Context, req *pb.MappingDeleteRequest) (*pb.MappingDeleteResponse, error) {
	return a.stub.Delete(ctx, req)
}

type searchAdapter struct {
	stub pb.ObjectSearchServiceClient
}

func (a *searchAdapter) Search(ctx context.Context, req *pb.SearchRequest) (SearchStream, error) {
	return a.stub.Search(ctx, req)
}

func (a *searchAdapter) SearchSimilar(ctx context.Context, req *pb.SearchSimilarRequest) (SearchStream, error) {
	return a.stub.SearchSimilar(ctx, req)
}

func (a *searchAdapter) SearchChunks(ctx context.Context, req *pb.SearchChunksRequest) (ChunkSearchStream, error) {
	return a.stub.SearchChunks(ctx, req)
}

func (a *searchAdapter) Aggregate(ctx context.Context, req *pb.AggregateRequest) (*pb.AggregateResponse, error) {
	return a.stub.Aggregate(ctx, req)
}

func (a *searchAdapter) GroupBy(ctx context.Context, req *pb.GroupByRequest) (SearchStream, error) {
	return a.stub.GroupBy(ctx, req)
}

func (a *searchAdapter) Pipeline(ctx context.Context, req *pb.PipelineRequest) (SearchStream, error) {
	return a.stub.Pipeline(ctx, req)
}
