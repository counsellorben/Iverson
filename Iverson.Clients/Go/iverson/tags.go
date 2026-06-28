// Package iverson provides the Go client for the Iverson gRPC API.
// Struct tags are used in place of runtime annotations to declare entity metadata.
//
// Tag format:
//
//	`iverson:"key"`                 — primary key field
//	`iverson:"search_key:N"`        — sort key at position N (0-based)
//	`iverson:"large_field"`         — excluded from StarRocks materialized view
//	`iverson:"many_to_one:TypeName"` — FK to TypeName (this entity holds the FK)
//	`iverson:"many_to_many:TypeName"` — join-table FK
//	`iverson:"one_to_many:TypeName"` — inverse of many_to_one
//	`iverson:"one_to_one:TypeName"` — 1:1 FK
package iverson

import (
	"fmt"
	"reflect"
	"strconv"
	"strings"
)

// Tag key for struct tag parsing.
const TagKey = "iverson"

// Kind constants for tag values.
const (
	KindKey        = "key"
	KindSearchKey  = "search_key"
	KindLargeField = "large_field"
	KindManyToOne  = "many_to_one"
	KindManyToMany = "many_to_many"
	KindOneToMany  = "one_to_many"
	KindOneToOne   = "one_to_one"
)

// FieldMeta holds the parsed metadata for a single struct field.
type FieldMeta struct {
	// Name is the struct field name (PascalCase).
	Name string
	// Kind is one of the Kind* constants, or "" for plain fields.
	Kind string
	// SearchKeyOrder is the sort position when Kind == KindSearchKey.
	SearchKeyOrder int
	// RelatedType is the target type name for relation kinds.
	RelatedType string
}

// ParseTag parses an `iverson:"..."` tag value for one field.
// Returns a FieldMeta; Kind is "" for untagged fields.
func ParseTag(fieldName, tagValue string) (FieldMeta, error) {
	meta := FieldMeta{Name: fieldName}
	if tagValue == "" {
		return meta, nil
	}

	// Tags may have the form "kind" or "kind:value"
	parts := strings.SplitN(tagValue, ":", 2)
	kind := parts[0]

	switch kind {
	case KindKey:
		meta.Kind = KindKey

	case KindSearchKey:
		meta.Kind = KindSearchKey
		if len(parts) == 2 {
			order, err := strconv.Atoi(parts[1])
			if err != nil {
				return meta, fmt.Errorf("iverson tag %q: search_key order %q is not an integer", tagValue, parts[1])
			}
			meta.SearchKeyOrder = order
		}

	case KindLargeField:
		meta.Kind = KindLargeField

	case KindManyToOne, KindManyToMany, KindOneToMany, KindOneToOne:
		meta.Kind = kind
		if len(parts) == 2 {
			meta.RelatedType = parts[1]
		} else {
			return meta, fmt.Errorf("iverson tag %q: relation kind requires a type name (e.g. many_to_one:Author)", tagValue)
		}

	default:
		return meta, fmt.Errorf("iverson tag %q: unknown kind %q", tagValue, kind)
	}

	return meta, nil
}

// EntityMeta holds all parsed metadata for a struct type.
type EntityMeta struct {
	// TypeName is the simple struct name.
	TypeName string
	// Fields lists all non-relation fields in declaration order.
	Fields []FieldMeta
	// Relations lists all relation fields.
	Relations []FieldMeta
}

// InspectType reflects on a struct type and extracts EntityMeta from iverson tags.
// Pass a pointer-to-struct or a struct value; both are accepted.
func InspectType(v interface{}) (EntityMeta, error) {
	t := reflect.TypeOf(v)
	if t.Kind() == reflect.Ptr {
		t = t.Elem()
	}
	if t.Kind() != reflect.Struct {
		return EntityMeta{}, fmt.Errorf("InspectType: expected struct, got %s", t.Kind())
	}

	meta := EntityMeta{TypeName: t.Name()}

	for i := 0; i < t.NumField(); i++ {
		sf := t.Field(i)
		tagValue := sf.Tag.Get(TagKey)
		fm, err := ParseTag(sf.Name, tagValue)
		if err != nil {
			return EntityMeta{}, err
		}

		switch fm.Kind {
		case KindManyToOne, KindManyToMany, KindOneToMany, KindOneToOne:
			meta.Relations = append(meta.Relations, fm)
		default:
			meta.Fields = append(meta.Fields, fm)
		}
	}

	return meta, nil
}
