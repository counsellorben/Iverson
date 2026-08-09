// Package iverson provides the Go client for the Iverson gRPC API.
// Struct tags are used in place of runtime annotations to declare entity metadata.
//
// Tag format:
//
// The `iverson` tag carries relation kinds only:
//
//	`iverson:"many_to_one:TypeName"` — FK to TypeName (this entity holds the FK)
//	`iverson:"many_to_many:TypeName"` — join-table FK
//	`iverson:"one_to_many:TypeName"` — inverse of many_to_one
//	`iverson:"one_to_one:TypeName"` — 1:1 FK
//
// Every scalar declaration is its own independent tag key, and all of them
// compose:
//
//	`iverson_key:"true"`            — primary key field
//	`iverson_search_key:"N"`        — sort key at position N (0-based)
//	`iverson_large_field:"true"`    — excluded from StarRocks materialized view
//	`iverson_embedding:"true"`      — embedding source field
//	`iverson_chunk:"..."`           — chunk field; "true", "256", or "256:32"
//
// One exception: `iverson_key` composes only with `iverson_desc`. Any other
// declaration on the key field is rejected, because the server builds every
// per-property declaration from non-key properties only and would accept and
// silently discard it.
//
// A field may also carry these independent tags, each valid on any field:
//
//	`iverson_desc:"Human-readable description"`
//	`iverson_meta:"true"`  — denormalized onto chunk points for chunk-search filtering
//	`iverson_tenant:"true"` — marks the field as the tenant boundary. Exactly one
//	                          field on a type must carry it; the server requires
//	                          every schema to declare a tenant boundary.
//	`iverson_summary:"true"`     — marks the field as the ingest-time summary target
//	`iverson_keywords:"true"`    — marks the field as the ingest-time keywords target
//	`iverson_extract:"<hint>"`   — marks the field as an extraction target; the tag
//	                               value is the extraction hint. A blank hint is
//	                               rejected: the server treats an empty hint as
//	                               "not an extraction target" and would silently
//	                               drop the declaration.
//	`iverson_contextual:"true"`  — the "contextual" option for a chunk field; valid
//	                               only alongside `iverson_chunk:"..."` on the same
//	                               field.
//
// `iverson_meta` is a separate tag key rather than an `iverson` kind because it
// composes: a field can be both a search key and metadata, which the server and
// the other four clients all allow. Note the server rejects it in combination
// with embedding, chunk, array, or large-field annotations.
// `iverson_summary`, `iverson_keywords`, `iverson_extract`, and `iverson_contextual`
// follow the same pattern: independent tag keys so they compose freely, rather
// than being mutually-exclusive kinds of their own.
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

// TenantTagKey is the struct tag key marking a field as the tenant boundary.
// Independent of TagKey (not a kind) because the tenant field may legitimately
// also be a search key or another kind; kinds are mutually exclusive but this
// tag must compose with them.
const TenantTagKey = "iverson_tenant"

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
// `iverson_chunk:"..."` on the same field.
const ContextualTagKey = "iverson_contextual"

// KeyTagKey marks the primary key field: `iverson_key:"true"`.
const KeyTagKey = "iverson_key"

// GuidTagKey marks a property as a UUID column: `iverson_guid:"true"`. Go has no
// UUID type in this client's dependency set, so the tag carries what the type cannot.
const GuidTagKey = "iverson_guid"

// SearchKeyTagKey declares a sort key at a 0-based position: `iverson_search_key:"0"`.
const SearchKeyTagKey = "iverson_search_key"

// LargeFieldTagKey excludes the column from the StarRocks materialized view:
// `iverson_large_field:"true"`.
const LargeFieldTagKey = "iverson_large_field"

// EmbeddingTagKey marks the field as an embedding source: `iverson_embedding:"true"`.
const EmbeddingTagKey = "iverson_embedding"

// ChunkTagKey marks the field for chunking. Value is "true" for defaults, "256" for a
// window size, or "256:32" for window size and overlap.
const ChunkTagKey = "iverson_chunk"

// Kind constants for relation tag values.
const (
	KindManyToOne  = "many_to_one"
	KindManyToMany = "many_to_many"
	KindOneToMany  = "one_to_many"
	KindOneToOne   = "one_to_one"
)

// FieldMeta holds the parsed metadata for a single struct field.
type FieldMeta struct {
	// Name is the struct field name (PascalCase).
	Name string
	// RelationKind is one of KindManyToOne, KindManyToMany, KindOneToMany or
	// KindOneToOne, or "" for scalar fields. Relations are mutually exclusive by
	// design: they serialize to RelationDescriptor, a different proto message.
	RelationKind string
	// IsKey reports whether the field carries `iverson_key:"true"`.
	IsKey bool
	// IsGuid reports whether the field carries `iverson_guid:"true"`.
	IsGuid bool
	// IsSearchKey reports whether the field carries `iverson_search_key:"N"`.
	IsSearchKey bool
	// IsLargeField reports whether the field carries `iverson_large_field:"true"`.
	IsLargeField bool
	// IsEmbedding reports whether the field carries `iverson_embedding:"true"`.
	IsEmbedding bool
	// IsChunk reports whether the field carries `iverson_chunk:"..."`.
	IsChunk bool
	// SearchKeyOrder is the sort position when IsSearchKey.
	SearchKeyOrder int
	// ChunkMaxTokens is the window size in tokens when IsChunk. Default 512.
	ChunkMaxTokens int
	// ChunkOverlap is the tokens shared between adjacent windows when IsChunk. Default 64.
	ChunkOverlap int
	// RelatedType is the target type name for relation kinds.
	RelatedType string
	// Description is the field description from the `iverson_desc` struct tag,
	// or "" when absent.
	Description string
	// Metadata reports whether the field carries `iverson_meta:"true"`.
	// Independent, so it composes with search_key, large_field, and the rest.
	Metadata bool
	// Tenant reports whether the field carries `iverson_tenant:"true"`.
	// Independent, so it composes with search_key and the rest.
	Tenant bool
	// IsSummaryTarget reports whether the field carries `iverson_summary:"true"`.
	IsSummaryTarget bool
	// IsKeywordsTarget reports whether the field carries `iverson_keywords:"true"`.
	IsKeywordsTarget bool
	// ExtractHint is the value of `iverson_extract:"<hint>"`, or "" when absent.
	ExtractHint string
	// ChunkContextual reports whether the field carries `iverson_contextual:"true"`.
	// Only valid when IsChunk.
	ChunkContextual bool
}

// ParseTag parses an `iverson:"..."` tag value for one field's relation kind.
// Returns a FieldMeta; RelationKind is "" for untagged fields.
func ParseTag(fieldName, tagValue string) (FieldMeta, error) {
	meta := FieldMeta{Name: fieldName}
	if tagValue == "" {
		return meta, nil
	}

	// Tags may have the form "kind" or "kind:value"
	parts := strings.SplitN(tagValue, ":", 2)
	kind := parts[0]

	switch kind {
	case KindManyToOne, KindManyToMany, KindOneToMany, KindOneToOne:
		meta.RelationKind = kind
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
	var tenantFields []string

	for i := 0; i < t.NumField(); i++ {
		sf := t.Field(i)
		tagValue := sf.Tag.Get(TagKey)
		fm, err := ParseTag(sf.Name, tagValue)
		if err != nil {
			return EntityMeta{}, err
		}
		fm.Description = sf.Tag.Get(DescriptionTagKey)
		fm.Metadata = sf.Tag.Get(MetadataTagKey) == "true"
		fm.Tenant = sf.Tag.Get(TenantTagKey) == "true"
		fm.IsSummaryTarget = sf.Tag.Get(SummaryTagKey) == "true"
		fm.IsKeywordsTarget = sf.Tag.Get(KeywordsTagKey) == "true"

		fm.IsKey = sf.Tag.Get(KeyTagKey) == "true"
		fm.IsGuid = sf.Tag.Get(GuidTagKey) == "true"
		fm.IsLargeField = sf.Tag.Get(LargeFieldTagKey) == "true"
		fm.IsEmbedding = sf.Tag.Get(EmbeddingTagKey) == "true"

		if order, ok := sf.Tag.Lookup(SearchKeyTagKey); ok {
			n, err := strconv.Atoi(strings.TrimSpace(order))
			if err != nil {
				return EntityMeta{}, fmt.Errorf("iverson tag %q: field %s has a non-integer search-key order %q; the value is the 0-based sort position", SearchKeyTagKey, sf.Name, order)
			}
			fm.IsSearchKey = true
			fm.SearchKeyOrder = n
		}

		if chunk, ok := sf.Tag.Lookup(ChunkTagKey); ok {
			fm.IsChunk = true
			fm.ChunkMaxTokens = 512
			fm.ChunkOverlap = 64
			if chunk != "true" {
				parts := strings.SplitN(chunk, ":", 2)
				maxTokens, err := strconv.Atoi(strings.TrimSpace(parts[0]))
				if err != nil {
					return EntityMeta{}, fmt.Errorf("iverson tag %q: field %s has a non-integer chunk window size %q; use \"true\", \"256\" or \"256:32\"", ChunkTagKey, sf.Name, parts[0])
				}
				fm.ChunkMaxTokens = maxTokens
				if len(parts) == 2 {
					overlap, err := strconv.Atoi(strings.TrimSpace(parts[1]))
					if err != nil {
						return EntityMeta{}, fmt.Errorf("iverson tag %q: field %s has a non-integer chunk overlap %q; use \"true\", \"256\" or \"256:32\"", ChunkTagKey, sf.Name, parts[1])
					}
					fm.ChunkOverlap = overlap
				}
			}
		}

		if hint, ok := sf.Tag.Lookup(ExtractTagKey); ok {
			if strings.TrimSpace(hint) == "" {
				return EntityMeta{}, fmt.Errorf("iverson tag %q: field %s has a blank extraction hint; the server treats an empty extract_hint as \"not an extraction target\" and would silently drop this declaration — provide a non-empty hint", ExtractTagKey, sf.Name)
			}
			fm.ExtractHint = hint
		}

		fm.ChunkContextual = sf.Tag.Get(ContextualTagKey) == "true"
		if fm.ChunkContextual && !fm.IsChunk {
			return EntityMeta{}, fmt.Errorf("iverson tag %q: field %s carries iverson_contextual but is not a chunk field (iverson_chunk:\"...\"); contextual is only meaningful on a chunk field", ContextualTagKey, sf.Name)
		}

		// The server builds every per-property declaration from non-key properties
		// only, so anything but a description on the key is accepted and silently
		// dropped.
		if fm.IsKey {
			var rejected []string
			if fm.IsSearchKey {
				rejected = append(rejected, SearchKeyTagKey)
			}
			if fm.IsLargeField {
				rejected = append(rejected, LargeFieldTagKey)
			}
			if fm.IsEmbedding {
				rejected = append(rejected, EmbeddingTagKey)
			}
			if fm.IsChunk {
				rejected = append(rejected, ChunkTagKey)
			}
			if fm.Metadata {
				rejected = append(rejected, MetadataTagKey)
			}
			if fm.IsSummaryTarget {
				rejected = append(rejected, SummaryTagKey)
			}
			if fm.IsKeywordsTarget {
				rejected = append(rejected, KeywordsTagKey)
			}
			if fm.ExtractHint != "" {
				rejected = append(rejected, ExtractTagKey)
			}
			if len(rejected) > 0 {
				return EntityMeta{}, fmt.Errorf("%s.%s is the primary key and also declares %s; the server builds every per-property declaration from non-key properties only, so this would be accepted and silently discarded. Remove it from the key field. (Only a description is valid on a key.)", meta.TypeName, sf.Name, strings.Join(rejected, ", "))
			}
		}

		if fm.RelationKind != "" {
			// Relations never reach meta.Fields, which is where the tenant field
			// is looked up on registration — so a tenant marker on a relation is
			// not a tenant declaration at all and must not satisfy the check.
			meta.Relations = append(meta.Relations, fm)
		} else {
			if fm.Tenant {
				tenantFields = append(tenantFields, sf.Name)
			}
			meta.Fields = append(meta.Fields, fm)
		}
	}

	if len(tenantFields) == 0 {
		return EntityMeta{}, fmt.Errorf("iverson tag %q: type %s has no field marked iverson_tenant:\"true\"; the server requires every schema to declare a tenant boundary and will reject registration without one", TenantTagKey, meta.TypeName)
	}
	if len(tenantFields) > 1 {
		return EntityMeta{}, fmt.Errorf("iverson tag %q: type %s has multiple fields marked iverson_tenant:\"true\" (%s); exactly one field must carry the tenant marker", TenantTagKey, meta.TypeName, strings.Join(tenantFields, ", "))
	}

	return meta, nil
}
