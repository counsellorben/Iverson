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

// MappingCrudClient is the interface for the ObjectMappingService operations the coordinator
// uses: full CRUD with server-side relation resolution, plus Delete.
type MappingCrudClient interface {
	Get(ctx context.Context, req *pb.MappingGetRequest) (*pb.MappingResponse, error)
	Post(ctx context.Context, req *pb.MappingWriteRequest) (*pb.MappingResponse, error)
	Update(ctx context.Context, req *pb.MappingWriteRequest) (*pb.MappingResponse, error)
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

// GetSchema returns the catalog of registered types the calling identity may read.
// The catalog lists precisely the types the caller can actually query, so an empty
// result is a normal authorization outcome, not an error. It means every registered
// type was denied for this caller. The usual causes are: no acting user attached
// (use WithActingUserToken(ctx, token)); the acting user has no tenant_id claim;
// the registered types declare no authorization rules; or they declare no tenant
// field. All four make a type unreadable through every RPC, not just this one.
func (c *IversonClient) GetSchema(ctx context.Context, traceID string) ([]*pb.SchemaType, error) {
	resp, err := c.MappingStub.GetSchema(ctx, &pb.GetSchemaRequest{TraceId: traceID})
	if err != nil {
		return nil, fmt.Errorf("GetSchema: %w", err)
	}
	return resp.Types, nil
}

// coordinatorDeps holds injectable service clients (real or mock).
type coordinatorDeps struct {
	persistence PersistenceClient
	retrieval   RetrievalClient
	mapping     MappingCrudClient
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
		if f.IsKey {
			keyField = f.Name
			break
		}
	}

	return &EntityCoordinator[T]{
		deps: coordinatorDeps{
			persistence: &persistenceAdapter{client.PersistenceStub},
			retrieval:   &retrievalAdapter{client.RetrievalStub},
			mapping:     &mappingAdapter{client.MappingStub},
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
		if f.IsKey {
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

// GetMapped retrieves an entity by key with server-side relation resolution to the given depth.
func (c *EntityCoordinator[T]) GetMapped(ctx context.Context, id string, depth int32) (T, error) {
	var zero T
	resp, err := c.deps.mapping.Get(ctx, &pb.MappingGetRequest{
		TypeName: c.typeName,
		Key:      id,
		Depth:    depth,
	})
	if err != nil {
		return zero, fmt.Errorf("GetMapped: %w", err)
	}
	if !resp.Success {
		return zero, fmt.Errorf("GetMapped: %s", resp.Error)
	}
	return structToEntity[T](resp.Data)
}

// PostMapped creates an entity through the mapping path, which resolves its relations
// server-side. Returns the entity hydrated from the response, carrying the
// server-assigned key — the caller never assigns one.
func (c *EntityCoordinator[T]) PostMapped(ctx context.Context, entity T) (T, error) {
	var zero T
	payload, err := entityToStruct(entity)
	if err != nil {
		return zero, err
	}
	resp, err := c.deps.mapping.Post(ctx, &pb.MappingWriteRequest{
		TypeName: c.typeName,
		Payload:  payload,
	})
	if err != nil {
		return zero, fmt.Errorf("PostMapped: %w", err)
	}
	if !resp.Success {
		return zero, fmt.Errorf("PostMapped: %s", resp.Error)
	}
	return structToEntity[T](resp.Data)
}

// UpdateMapped updates an existing entity through the mapping path.
func (c *EntityCoordinator[T]) UpdateMapped(ctx context.Context, entity T) (T, error) {
	var zero T
	payload, err := entityToStruct(entity)
	if err != nil {
		return zero, err
	}
	resp, err := c.deps.mapping.Update(ctx, &pb.MappingWriteRequest{
		TypeName: c.typeName,
		Payload:  payload,
	})
	if err != nil {
		return zero, fmt.Errorf("UpdateMapped: %w", err)
	}
	if !resp.Success {
		return zero, fmt.Errorf("UpdateMapped: %s", resp.Error)
	}
	return structToEntity[T](resp.Data)
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
		if sf.Name == HydratedFieldName {
			// The read-path carrier is never a write-side field: it holds pointers
			// to related rows the server injected on a prior depth-resolved read,
			// not data this write should send back under its own name.
			continue
		}
		fv := v.Field(i)

		var fm FieldMeta
		tagged := false
		tag := sf.Tag.Get(TagKey)
		if tag != "" {
			var err error
			fm, err = ParseTag(sf.Name, tag)
			if err != nil {
				return nil, fmt.Errorf("field %s: %w", sf.Name, err)
			}
			tagged = fm.RelationKind != ""
		}

		if tagged {
			// OneToMany is the inverse side: its FK column names a property on the
			// RELATED row, not this one, and its value is a nav slice of hydrated
			// structs — never a write-side field.
			if fm.RelationKind == KindOneToMany {
				continue
			}
			// A relation field's own Go type may still be a nav property (a struct
			// or slice-of-struct) rather than the FK-bearing scalar the contract
			// requires; skip it as a nav property rather than serializing it. None
			// exist today, but the rule should not depend on that.
			ft := sf.Type
			if ft.Kind() == reflect.Ptr {
				ft = ft.Elem()
			}
			if ft.Kind() == reflect.Struct {
				continue
			}
			if ft.Kind() == reflect.Slice {
				elem := ft.Elem()
				if elem.Kind() == reflect.Ptr {
					elem = elem.Elem()
				}
				if elem.Kind() == reflect.Struct {
					continue
				}
			}

			val, err := goValueToProtoValue(fv)
			if err != nil {
				return nil, fmt.Errorf("field %s: %w", sf.Name, err)
			}
			if val != nil {
				fields[inferFK(fm, t.Name())] = val
			}
			continue
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
	case reflect.Slice, reflect.Array:
		// []byte (and [N]byte) are scalar, not a relation id list; leave to default.
		if v.Kind() == reflect.Slice && v.IsNil() {
			return structpb.NewNullValue(), nil
		}
		if v.Type().Elem().Kind() == reflect.Uint8 {
			return structpb.NewNullValue(), nil
		}
		values := make([]*structpb.Value, v.Len())
		for i := 0; i < v.Len(); i++ {
			elemVal, err := goValueToProtoValue(v.Index(i))
			if err != nil {
				return nil, err
			}
			values[i] = elemVal
		}
		return structpb.NewListValue(&structpb.ListValue{Values: values}), nil
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

	v, err := fillEntityValue(s, t)
	if err != nil {
		return zero, err
	}
	return v.Interface().(T), nil
}

// fillEntityValue reflects over struct type t, filling its scalar and foreign-key
// fields from s, then populating its Hydrated carrier (if the type declares one) with
// any related-row children the server attached for a depth-resolved read. Factored out
// of structToEntity so hydration can recurse into related types by reflect.Type alone —
// structToEntity's own type parameter T can't be threaded down to a related type chosen
// at runtime from the registeredTypes registry.
func fillEntityValue(s *structpb.Struct, t reflect.Type) (reflect.Value, error) {
	v := reflect.New(t).Elem()

	for i := 0; i < t.NumField(); i++ {
		sf := t.Field(i)
		if sf.Name == HydratedFieldName {
			continue
		}

		key := sf.Name
		tag := sf.Tag.Get(TagKey)
		if tag != "" {
			fm, err := ParseTag(sf.Name, tag)
			if err != nil {
				return reflect.Value{}, fmt.Errorf("field %s: %w", sf.Name, err)
			}
			// The server injects hydrated child structs under the field's own name
			// on depth-resolved reads for the inverse (OneToMany) side; that isn't
			// a foreign-key list and must not be parsed as one. (Its hydrated
			// children still land in Hydrated, below — this only guards the
			// declared []string-of-ids member, which has no struct case in
			// protoValueToGoValue and would otherwise silently fill with one
			// empty string per related row.)
			if fm.RelationKind == KindOneToMany {
				continue
			}
			if fm.RelationKind == KindManyToMany {
				key = inferFK(fm, t.Name())
			}
		}

		pbVal, ok := s.Fields[key]
		if !ok {
			continue
		}
		fv := v.Field(i)
		if err := protoValueToGoValue(pbVal, fv, sf.Type); err != nil {
			return reflect.Value{}, fmt.Errorf("field %s: %w", sf.Name, err)
		}
	}

	if err := populateHydrated(s, t, v); err != nil {
		return reflect.Value{}, err
	}

	return v, nil
}

// populateHydrated fills t's Hydrated map[string]any carrier (if declared) with typed
// pointers to related rows the server attached for a depth-resolved read, keyed by each
// relation's wire (nav-property) name — the same name the schema registers and the
// server-side conformance verifier looks under. Every relation kind lands here,
// including one_to_many, since Go's declared []string member for that kind cannot hold
// a struct. If the related type was never registered client-side, the raw (untyped)
// wire value is stored instead of failing the read.
func populateHydrated(s *structpb.Struct, t reflect.Type, v reflect.Value) error {
	hf, ok := t.FieldByName(HydratedFieldName)
	if !ok || hf.Type.Kind() != reflect.Map {
		return nil
	}

	meta, err := InspectType(reflect.New(t).Interface())
	if err != nil {
		// Best-effort: a type whose own metadata doesn't parse can't tell us its
		// relations' wire names, but that shouldn't fail an otherwise-successful
		// read of its scalar fields.
		return nil
	}

	hydrated := make(map[string]any, len(meta.Relations))
	for _, fm := range meta.Relations {
		wireKey := relationPropertyName(fm)
		pbVal, ok := s.Fields[wireKey]
		if !ok {
			continue
		}

		relatedType, registered := lookupRegisteredType(fm.RelatedType)

		switch fm.RelationKind {
		case KindManyToMany, KindOneToMany:
			lv := pbVal.GetListValue()
			if lv == nil {
				continue
			}
			if !registered {
				hydrated[wireKey] = pbVal.AsInterface()
				continue
			}
			items := reflect.MakeSlice(reflect.SliceOf(reflect.PointerTo(relatedType)), 0, len(lv.Values))
			for _, elemVal := range lv.Values {
				elemStruct := elemVal.GetStructValue()
				if elemStruct == nil {
					continue
				}
				childVal, err := fillEntityValue(elemStruct, relatedType)
				if err != nil {
					return fmt.Errorf("hydrating %s: %w", wireKey, err)
				}
				ptr := reflect.New(relatedType)
				ptr.Elem().Set(childVal)
				items = reflect.Append(items, ptr)
			}
			hydrated[wireKey] = items.Interface()

		case KindManyToOne, KindOneToOne:
			structVal := pbVal.GetStructValue()
			if structVal == nil {
				continue
			}
			if !registered {
				hydrated[wireKey] = pbVal.AsInterface()
				continue
			}
			childVal, err := fillEntityValue(structVal, relatedType)
			if err != nil {
				return fmt.Errorf("hydrating %s: %w", wireKey, err)
			}
			ptr := reflect.New(relatedType)
			ptr.Elem().Set(childVal)
			hydrated[wireKey] = ptr.Interface()
		}
	}

	if len(hydrated) > 0 {
		v.FieldByName(HydratedFieldName).Set(reflect.ValueOf(hydrated))
	}
	return nil
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
	case *structpb.Value_ListValue:
		if targetType.Kind() != reflect.Slice {
			return nil
		}
		elems := v.ListValue.Values
		slice := reflect.MakeSlice(targetType, len(elems), len(elems))
		for i, elemVal := range elems {
			if err := protoValueToGoValue(elemVal, slice.Index(i), targetType.Elem()); err != nil {
				return err
			}
		}
		target.Set(slice)
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

type mappingAdapter struct {
	stub pb.ObjectMappingServiceClient
}

func (a *mappingAdapter) Get(ctx context.Context, req *pb.MappingGetRequest) (*pb.MappingResponse, error) {
	return a.stub.Get(ctx, req)
}

func (a *mappingAdapter) Post(ctx context.Context, req *pb.MappingWriteRequest) (*pb.MappingResponse, error) {
	return a.stub.Post(ctx, req)
}

func (a *mappingAdapter) Update(ctx context.Context, req *pb.MappingWriteRequest) (*pb.MappingResponse, error) {
	return a.stub.Update(ctx, req)
}

func (a *mappingAdapter) Delete(ctx context.Context, req *pb.MappingDeleteRequest) (*pb.MappingDeleteResponse, error) {
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
