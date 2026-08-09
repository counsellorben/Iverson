package iverson

import (
	"context"
	"fmt"
	"math"
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
		clrType, isArray, err := goTypeToClr(sf.Type)
		if err != nil {
			return nil, fmt.Errorf("field %s: %w", fm.Name, err)
		}
		if fm.IsGuid {
			clrType = pb.ClrType_CLR_GUID
		}
		searchKeyOrder, err := int32FromInt(fm.SearchKeyOrder)
		if err != nil {
			return nil, fmt.Errorf("field %s: SearchKeyOrder %w", fm.Name, err)
		}
		chunkMaxTokens, err := int32FromInt(fm.ChunkMaxTokens)
		if err != nil {
			return nil, fmt.Errorf("field %s: ChunkMaxTokens %w", fm.Name, err)
		}
		chunkOverlap, err := int32FromInt(fm.ChunkOverlap)
		if err != nil {
			return nil, fmt.Errorf("field %s: ChunkOverlap %w", fm.Name, err)
		}
		prop := &pb.PropertyDescriptor{
			Name:             fm.Name,
			ClrType:          clrType,
			IsArray:          isArray,
			IsKey:            fm.IsKey,
			IsNullable:       !fm.IsKey,
			IsSearchKey:      fm.IsSearchKey,
			SearchKeyOrder:   searchKeyOrder,
			IsLargeField:     fm.IsLargeField,
			IsEmbedding:      fm.IsEmbedding,
			IsChunk:          fm.IsChunk,
			ChunkMaxTokens:   chunkMaxTokens,
			ChunkOverlap:     chunkOverlap,
			IsMetadata:       fm.Metadata,
			Description:      fm.Description,
			IsSummaryTarget:  fm.IsSummaryTarget,
			IsKeywordsTarget: fm.IsKeywordsTarget,
			ExtractHint:      fm.ExtractHint,
			ChunkContextual:  fm.ChunkContextual,
		}
		properties = append(properties, prop)
	}

	relations := make([]*pb.RelationDescriptor, 0, len(meta.Relations))
	for _, fm := range meta.Relations {
		if (fm.RelationKind == KindManyToOne || fm.RelationKind == KindOneToOne) && fm.Name != fm.RelatedType+"Id" {
			return nil, fmt.Errorf("field %s is a %s relation to %s but is named %q; it must be named %q, since the field itself is the foreign key", fm.Name, fm.RelationKind, fm.RelatedType, fm.Name, fm.RelatedType+"Id")
		}

		kind := relationKindToProto(fm.RelationKind)
		fk := inferFK(fm, meta.TypeName)
		propName := relationPropertyName(fm)
		rel := &pb.RelationDescriptor{
			PropertyName: propName,
			Kind:         kind,
			RelatedType:  fm.RelatedType,
			ForeignKey:   fk,
		}
		relations = append(relations, rel)

		if fm.RelationKind != KindOneToMany {
			properties = append(properties, &pb.PropertyDescriptor{
				Name:       fk,
				ClrType:    pb.ClrType_CLR_GUID,
				IsArray:    fm.RelationKind == KindManyToMany,
				IsNullable: true,
				IsKey:      false,
			})
		}
	}

	var tenantField string
	for _, fm := range meta.Fields {
		if fm.Tenant {
			tenantField = fm.Name
			break
		}
	}

	typeDesc := &pb.TypeDescriptor{
		TypeName:    meta.TypeName,
		Properties:  properties,
		Relations:   relations,
		Description: typeDescription(e),
		TenantField: tenantField,
	}
	return &pb.SchemaRequest{
		RootType: typeDesc,
		TraceId:  traceID,
	}, nil
}

// DescribedEntity is the optional interface an entity struct may implement to
// supply a type-level description.
type DescribedEntity interface {
	IversonDescription() string
}

// typeDescription returns the entity's type-level description, or "" when the
// entity does not implement DescribedEntity. A struct value is also checked
// through a pointer, so pointer-receiver implementations are honoured.
func typeDescription(e interface{}) string {
	if d, ok := e.(DescribedEntity); ok {
		return d.IversonDescription()
	}
	v := reflect.ValueOf(e)
	if v.Kind() == reflect.Struct {
		p := reflect.New(v.Type())
		p.Elem().Set(v)
		if d, ok := p.Interface().(DescribedEntity); ok {
			return d.IversonDescription()
		}
	}
	return ""
}

// int32FromInt narrows a platform int to int32, rejecting values that would
// silently truncate (e.g. a chunk/order value from a hand-written struct tag
// that overflows int32).
func int32FromInt(v int) (int32, error) {
	if v < math.MinInt32 || v > math.MaxInt32 {
		return 0, fmt.Errorf("value %d overflows int32", v)
	}
	return int32(v), nil
}

// goTypeToClr maps a reflect.Type to a ClrType proto enum value and whether it is an array.
// An array whose element is itself an array, or is not a supported scalar, is REJECTED rather
// than silently collapsed: the server would register a 1-D TEXT[] column against a payload that
// is a nested/complex JSON array, and json_populate_record fails on the first insert.
func goTypeToClr(t reflect.Type) (pb.ClrType, bool, error) {
	if t.Kind() == reflect.Ptr {
		t = t.Elem()
	}
	if t.Kind() == reflect.Slice && t.Elem().Kind() != reflect.Uint8 {
		elem := t.Elem()
		if elem.Kind() == reflect.Ptr {
			elem = elem.Elem()
		}
		// A [][]byte is the one nested shape that is legitimate: its inner slice is
		// the scalar bytes type, not a nested array, so it maps to BYTEA[].
		if elem.Kind() == reflect.Slice && elem.Elem().Kind() == reflect.Uint8 {
			return pb.ClrType_CLR_BYTES, true, nil
		}
		if elem.Kind() == reflect.Slice || elem.Kind() == reflect.Array {
			return 0, false, fmt.Errorf("nested array type %s is not supported", t)
		}
		clr, supported := goScalarToClr(elem)
		if !supported {
			return 0, false, fmt.Errorf("array element type %s is not a supported scalar", elem)
		}
		return clr, true, nil
	}
	clr, _ := goScalarToClr(t)
	return clr, false, nil
}

// goScalarToClr maps a non-array reflect.Type to a ClrType and reports whether the type is a
// SUPPORTED scalar. Unsupported scalars keep their historical CLR_STRING fallback; only the
// array path acts on the supported flag.
func goScalarToClr(t reflect.Type) (pb.ClrType, bool) {
	if t.Kind() == reflect.Ptr {
		t = t.Elem()
	}
	switch t.Kind() {
	case reflect.String:
		return pb.ClrType_CLR_STRING, true
	case reflect.Int32:
		return pb.ClrType_CLR_INT32, true
	case reflect.Int, reflect.Int64:
		return pb.ClrType_CLR_INT64, true
	case reflect.Float32:
		return pb.ClrType_CLR_FLOAT, true
	case reflect.Float64:
		return pb.ClrType_CLR_DOUBLE, true
	case reflect.Bool:
		return pb.ClrType_CLR_BOOL, true
	case reflect.Slice:
		// []byte is a primitive scalar.
		if t.Elem().Kind() == reflect.Uint8 {
			return pb.ClrType_CLR_BYTES, true
		}
		return pb.ClrType_CLR_STRING, false
	case reflect.Struct:
		// time.Time maps to CLR_DATETIME
		if t.PkgPath() == "time" && t.Name() == "Time" {
			return pb.ClrType_CLR_DATETIME, true
		}
		return pb.ClrType_CLR_STRING, false
	default:
		return pb.ClrType_CLR_STRING, false
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
	switch fm.RelationKind {
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
	if fm.RelationKind == KindManyToOne || fm.RelationKind == KindOneToOne {
		name := fm.Name
		if len(name) > 2 && name[len(name)-2:] == "Id" {
			return name[:len(name)-2]
		}
	}
	return fm.Name
}
