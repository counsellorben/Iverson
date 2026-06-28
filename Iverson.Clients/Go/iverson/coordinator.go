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

// Get retrieves an entity by key. Returns a zero value and false if not found.
func (c *EntityCoordinator[T]) Get(ctx context.Context, id string) (T, bool, error) {
	var zero T
	resp, err := c.deps.retrieval.Get(ctx, &pb.RetrievalRequest{
		TypeName: c.typeName,
		Key:      id,
	})
	if err != nil {
		return zero, false, fmt.Errorf("Get: %w", err)
	}
	if !resp.Found {
		return zero, false, nil
	}
	entity, err := structToEntity[T](resp.Data)
	if err != nil {
		return zero, false, err
	}
	return entity, true, nil
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
			parts := len(tag)
			_ = parts
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
