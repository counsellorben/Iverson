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
//
// A field may also carry these independent tags, each valid alongside any
// `iverson` kind (including `key`) and on untagged fields:
//
//	`iverson_desc:"Human-readable description"`
//	`iverson_meta:"true"`  — denormalized onto chunk points for chunk-search filtering
//	`iverson_summary:"true"`     — marks the field as the ingest-time summary target
//	`iverson_keywords:"true"`    — marks the field as the ingest-time keywords target
//	`iverson_extract:"<hint>"`   — marks the field as an extraction target; the tag
//	                               value is the extraction hint. A blank hint is
//	                               rejected: the server treats an empty hint as
//	                               "not an extraction target" and would silently
//	                               drop the declaration.
//	`iverson_contextual:"true"`  — the "contextual" option for a chunk field; valid
//	                               only alongside `iverson:"chunk..."` on the same
//	                               field.
//
// `iverson_meta` is a separate tag key rather than an `iverson` kind because it
// composes with the kinds: a field can be both a search key and metadata, which
// the server and the other four clients all allow. Note the server rejects it in
// combination with embedding, chunk, array, or large-field annotations.
// `iverson_summary`, `iverson_keywords`, `iverson_extract`, and `iverson_contextual`
// follow the same pattern: independent tag keys so they compose freely with any
// `iverson` kind, rather than being mutually-exclusive kinds of their own.
//
// A type-level description is supplied by implementing the optional interface:
//
//	interface{ IversonDescription() string }
package iverson

import (
	"fmt"
	"reflect"
	"strconv"
	"strings"
)

// Tag key for struct tag parsing.
const TagKey = "iverson"

// DescriptionTagKey is the struct tag key for field descriptions. It is
// independent of TagKey and may appear on a field of any kind.
const DescriptionTagKey = "iverson_desc"

// MetadataTagKey is the struct tag key marking a field as metadata. Like
// DescriptionTagKey it is independent of TagKey, so it composes with any kind.
const MetadataTagKey = "iverson_meta"

// SummaryTagKey is the struct tag key marking a field as the summary
// enrichment target. Independent of TagKey.
const SummaryTagKey = "iverson_summary"

// KeywordsTagKey is the struct tag key marking a field as the keywords
// enrichment target. Independent of TagKey.
const KeywordsTagKey = "iverson_keywords"

// ExtractTagKey is the struct tag key marking a field as an extraction
// target; the tag's value is the extraction hint. Independent of TagKey.
const ExtractTagKey = "iverson_extract"

// ContextualTagKey is the struct tag key enabling the "contextual" option on
// a chunk field. Independent of TagKey, but only meaningful alongside
// `iverson:"chunk..."` on the same field.
const ContextualTagKey = "iverson_contextual"

// Kind constants for tag values.
const (
	KindKey        = "key"
	KindSearchKey  = "search_key"
	KindLargeField = "large_field"
	KindEmbedding  = "embedding"
	KindChunk      = "chunk"
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
	// ChunkMaxTokens is the window size in tokens when Kind == KindChunk. Default 512.
	ChunkMaxTokens int
	// ChunkOverlap is the tokens shared between adjacent windows when Kind == KindChunk. Default 64.
	ChunkOverlap int
	// RelatedType is the target type name for relation kinds.
	RelatedType string
	// Description is the field description from the `iverson_desc` struct tag,
	// or "" when absent. Independent of Kind.
	Description string
	// Metadata reports whether the field carries `iverson_meta:"true"`.
	// Independent of Kind, so it composes with search_key, large_field, and the rest.
	Metadata bool
	// IsSummaryTarget reports whether the field carries `iverson_summary:"true"`.
	IsSummaryTarget bool
	// IsKeywordsTarget reports whether the field carries `iverson_keywords:"true"`.
	IsKeywordsTarget bool
	// ExtractHint is the value of `iverson_extract:"<hint>"`, or "" when absent.
	ExtractHint string
	// ChunkContextual reports whether the field carries `iverson_contextual:"true"`.
	// Only valid when Kind == KindChunk.
	ChunkContextual bool
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

	case KindEmbedding:
		meta.Kind = KindEmbedding

	case KindChunk:
		meta.Kind = KindChunk
		meta.ChunkMaxTokens = 512
		meta.ChunkOverlap = 64
		if len(parts) == 2 {
			chunkParts := strings.SplitN(parts[1], ":", 2)
			maxTokens, err := strconv.Atoi(chunkParts[0])
			if err != nil {
				return meta, fmt.Errorf("iverson tag %q: chunk maxTokens %q is not an integer", tagValue, chunkParts[0])
			}
			meta.ChunkMaxTokens = maxTokens
			if len(chunkParts) == 2 {
				overlap, err := strconv.Atoi(chunkParts[1])
				if err != nil {
					return meta, fmt.Errorf("iverson tag %q: chunk overlap %q is not an integer", tagValue, chunkParts[1])
				}
				meta.ChunkOverlap = overlap
			}
		}

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
		fm.Description = sf.Tag.Get(DescriptionTagKey)
		fm.Metadata = sf.Tag.Get(MetadataTagKey) == "true"
		fm.IsSummaryTarget = sf.Tag.Get(SummaryTagKey) == "true"
		fm.IsKeywordsTarget = sf.Tag.Get(KeywordsTagKey) == "true"

		if hint, ok := sf.Tag.Lookup(ExtractTagKey); ok {
			if strings.TrimSpace(hint) == "" {
				return EntityMeta{}, fmt.Errorf("iverson tag %q: field %s has a blank extraction hint; the server treats an empty extract_hint as \"not an extraction target\" and would silently drop this declaration — provide a non-empty hint", ExtractTagKey, sf.Name)
			}
			fm.ExtractHint = hint
		}

		fm.ChunkContextual = sf.Tag.Get(ContextualTagKey) == "true"
		if fm.ChunkContextual && fm.Kind != KindChunk {
			return EntityMeta{}, fmt.Errorf("iverson tag %q: field %s carries iverson_contextual but is not a chunk field (iverson:\"chunk...\"); contextual is only meaningful on a chunk field", ContextualTagKey, sf.Name)
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
