package iverson

import (
	"context"
	"fmt"
	"reflect"

	pb "github.com/iverson/clients/go/generated"
)

// MappingClient is the interface for the ObjectMappingService stub.
// Defined as an interface so tests can provide a mock.
type MappingClient interface {
	RegisterSchema(ctx context.Context, req *pb.SchemaRequest) (*pb.SchemaResponse, error)
}

// SchemaRegistrar reflects on Go struct types and registers their schemas
// with the Iverson server via ObjectMappingService.RegisterSchema.
type SchemaRegistrar struct {
	client MappingClient
	types  []interface{}
}

// NewSchemaRegistrar creates a SchemaRegistrar for the given entity values or types.
// Each entry in entities should be a struct value or pointer-to-struct whose type
// carries `iverson` struct tags.
func NewSchemaRegistrar(client MappingClient, entities ...interface{}) *SchemaRegistrar {
	return &SchemaRegistrar{client: client, types: entities}
}

// RegisterAll synchronously registers all entity schemas.
func (r *SchemaRegistrar) RegisterAll(ctx context.Context, traceID string) error {
	for _, e := range r.types {
		req, err := r.buildRequest(e, traceID)
		if err != nil {
			return err
		}
		resp, err := r.client.RegisterSchema(ctx, req)
		if err != nil {
			return fmt.Errorf("RegisterSchema RPC failed: %w", err)
		}
		if !resp.Success {
			return fmt.Errorf("schema registration failed: %s", resp.Error)
		}
	}
	return nil
}

// buildRequest reflects on entity e and constructs a SchemaRequest proto.
func (r *SchemaRegistrar) buildRequest(e interface{}, traceID string) (*pb.SchemaRequest, error) {
	meta, err := InspectType(e)
	if err != nil {
		return nil, err
	}

	// Determine Go type for field type mapping
	t := reflect.TypeOf(e)
	if t.Kind() == reflect.Ptr {
		t = t.Elem()
	}

	properties := make([]*pb.PropertyDescriptor, 0, len(meta.Fields))
	for _, fm := range meta.Fields {
		sf, ok := t.FieldByName(fm.Name)
		if !ok {
			continue
		}
		clrType := goTypeToClr(sf.Type)
		prop := &pb.PropertyDescriptor{
			Name:           fm.Name,
			ClrType:        clrType,
			IsKey:          fm.Kind == KindKey,
			IsNullable:     fm.Kind != KindKey,
			IsSearchKey:    fm.Kind == KindSearchKey,
			SearchKeyOrder: int32(fm.SearchKeyOrder),
			IsLargeField:   fm.Kind == KindLargeField,
			IsEmbedding:    fm.Kind == KindEmbedding,
			IsChunk:        fm.Kind == KindChunk,
			ChunkMaxTokens: int32(fm.ChunkMaxTokens),
			ChunkOverlap:   int32(fm.ChunkOverlap),
		}
		properties = append(properties, prop)
	}

	relations := make([]*pb.RelationDescriptor, 0, len(meta.Relations))
	for _, fm := range meta.Relations {
		kind := relationKindToProto(fm.Kind)
		fk := inferFK(fm, meta.TypeName)
		propName := relationPropertyName(fm)
		rel := &pb.RelationDescriptor{
			PropertyName: propName,
			Kind:         kind,
			RelatedType:  fm.RelatedType,
			ForeignKey:   fk,
		}
		relations = append(relations, rel)
	}

	typeDesc := &pb.TypeDescriptor{
		TypeName:   meta.TypeName,
		Properties: properties,
		Relations:  relations,
	}
	return &pb.SchemaRequest{
		RootType: typeDesc,
		TraceId:  traceID,
	}, nil
}

// goTypeToClr maps a reflect.Type to a ClrType proto enum value.
func goTypeToClr(t reflect.Type) pb.ClrType {
	if t.Kind() == reflect.Ptr {
		t = t.Elem()
	}
	switch t.Kind() {
	case reflect.String:
		return pb.ClrType_CLR_STRING
	case reflect.Int32:
		return pb.ClrType_CLR_INT32
	case reflect.Int, reflect.Int64:
		return pb.ClrType_CLR_INT64
	case reflect.Float32:
		return pb.ClrType_CLR_FLOAT
	case reflect.Float64:
		return pb.ClrType_CLR_DOUBLE
	case reflect.Bool:
		return pb.ClrType_CLR_BOOL
	case reflect.Slice:
		if t.Elem().Kind() == reflect.Uint8 {
			return pb.ClrType_CLR_BYTES
		}
		return pb.ClrType_CLR_STRING
	case reflect.Struct:
		// time.Time maps to CLR_DATETIME
		if t.PkgPath() == "time" && t.Name() == "Time" {
			return pb.ClrType_CLR_DATETIME
		}
		return pb.ClrType_CLR_STRING
	default:
		return pb.ClrType_CLR_STRING
	}
}

// relationKindToProto converts a tag kind string to the RelationKind proto enum.
func relationKindToProto(kind string) pb.RelationKind {
	switch kind {
	case KindOneToOne:
		return pb.RelationKind_ONE_TO_ONE
	case KindOneToMany:
		return pb.RelationKind_ONE_TO_MANY
	case KindManyToOne:
		return pb.RelationKind_MANY_TO_ONE
	case KindManyToMany:
		return pb.RelationKind_MANY_TO_MANY
	default:
		return pb.RelationKind_MANY_TO_ONE
	}
}

// inferFK derives the FK column name from the relation metadata.
// Convention mirrors the C# server: {RelatedType}Id for many_to_one/one_to_one,
// {RelatedType}Ids for many_to_many, {ThisType}Id for one_to_many.
func inferFK(fm FieldMeta, thisTypeName string) string {
	switch fm.Kind {
	case KindManyToOne, KindOneToOne:
		// The field itself is the FK (e.g. AuthorId field with many_to_one:Author tag).
		// The field name IS the FK column.
		return fm.Name
	case KindManyToMany:
		return fm.RelatedType + "Ids"
	case KindOneToMany:
		return thisTypeName + "Id"
	}
	return ""
}

// relationPropertyName derives the navigation property name from the field name.
// For many_to_one: AuthorId → Author (strip trailing "Id").
// For others: use the field name as-is.
func relationPropertyName(fm FieldMeta) string {
	if fm.Kind == KindManyToOne || fm.Kind == KindOneToOne {
		name := fm.Name
		if len(name) > 2 && name[len(name)-2:] == "Id" {
			return name[:len(name)-2]
		}
	}
	return fm.Name
}
