// Package main's S1 crud-roundtrip entity models for the Go conformance driver.
//
// Mirrors the .NET driver's DotNetArticle/DotNetAuthor/DotNetTag triple (see
// Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Models/), adapted to the Go client's
// declaration style: the relation lives directly on the foreign-key member (GoAuthorId,
// GoTagIds) per iverson/registrar.go's naming rule, and the key needs iverson_guid:"true" since
// Go maps a bare string key to CLR_STRING otherwise (see sample/models/article.go for the tag
// style this mirrors).
package main

// GoAuthor is the S1 author entity. GoArticles carries the reverse one_to_many
// navigation the foreign-key-only write contract work broke, so the harness observes
// it end to end.
type GoAuthor struct {
	Id         string `iverson_key:"true" iverson_guid:"true"`
	TenantId   string `iverson_tenant:"true"`
	OwnerId    string
	Name       string
	GoArticles []string `iverson:"one_to_many:GoArticle"`
	// Hydrated is the read-path carrier for depth-resolved relation children (see
	// iverson.HydratedFieldName). GoArticles can't hold them: it's declared []string
	// (a foreign-key id list), and protoValueToGoValue has no struct case, so routing
	// hydrated GoArticle structs there would silently fill it with one empty string
	// per related row.
	Hydrated map[string]any
}

// GoTag is the S1 tag entity.
type GoTag struct {
	Id       string `iverson_key:"true" iverson_guid:"true"`
	TenantId string `iverson_tenant:"true"`
	OwnerId  string
	Label    string
}

// GoArticle is the S1 article entity, relating to GoAuthor (many-to-one) and GoTag
// (many-to-many).
type GoArticle struct {
	Id         string `iverson_key:"true" iverson_guid:"true"`
	TenantId   string `iverson_tenant:"true"`
	OwnerId    string
	Title      string
	GoAuthorId string   `iverson:"many_to_one:GoAuthor"`
	GoTagIds   []string `iverson:"many_to_many:GoTag"`
	// IVC-REL-001/002/003's one_to_one fixture: a second relation to GoTag (the many_to_many
	// relation's own related type), through the SINGULAR "GoTagId" foreign key so it does not
	// collide with the many_to_many's plural "GoTagIds" — exercising one_to_one end to end
	// without a whole new entity type.
	GoTagId string `iverson:"one_to_one:GoTag"`
	// Hydrated is the read-path carrier for depth-resolved relation children (see
	// iverson.HydratedFieldName): GoAuthor, GoTags, and GoTag land here under their
	// wire (nav-property) names on a depth-resolved read.
	Hydrated map[string]any
}

// SharedAuthor and SharedArticle are S4 interop's fixtures. Every one of the five drivers declares
// the same type names and shapes; only the .NET driver ever registers them (register-once rule),
// so Go's own SchemaRegistrar is never invoked for these two types.
type SharedAuthor struct {
	Id       string `iverson_key:"true" iverson_guid:"true"`
	TenantId string `iverson_tenant:"true"`
	OwnerId  string
	Name     string
}

type SharedArticle struct {
	Id             string `iverson_key:"true" iverson_guid:"true"`
	TenantId       string `iverson_tenant:"true"`
	OwnerId        string
	Title          string
	SharedAuthorId string `iverson:"many_to_one:SharedAuthor"`
}

// GoBadArticle exists only for the naming-rejected (S2) conformance scenario. WriterId declares
// a many_to_one relation to GoAuthor but is not named AuthorId — the name
// iverson/registrar.go's buildRequest requires, since the field itself IS the foreign key.
// Registering this type must fail client-side, before any RPC.
type GoBadArticle struct {
	Id       string `iverson_key:"true" iverson_guid:"true"`
	TenantId string `iverson_tenant:"true"`
	OwnerId  string
	WriterId string `iverson:"many_to_one:GoAuthor"`
}

// QueryDoc is S6 query's subject type. Every one of the five drivers declares the same type name
// and shape; only the .NET driver ever registers it (register-once rule), and every driver writes
// one row into it and then queries it.
//
// Deliberately relation-free: the scenario's exact result-set comparison is over row keys, and a
// relation would drag hydration into what a search returns without adding anything the QRY axis
// asserts. Marker carries the run's --id-prefix and is the property every driver filters on —
// unique per run, so the expected result set is exactly this run's rows.
type QueryDoc struct {
	Id       string `iverson_key:"true" iverson_guid:"true"`
	TenantId string `iverson_tenant:"true"`
	OwnerId  string
	Marker   string
	Label    string
}

// VectorDoc is S7 vector-search's subject type. Every one of the five drivers declares the same
// type name and shape; only the .NET driver ever registers it (register-once rule), and every
// driver writes one row into it and then searches it.
//
// Deliberately relation-free, and deliberately without any enrichment annotation (summary,
// keywords, contextual chunking): the scenario's exact set comparisons must not depend on
// generative output that differs run to run.
//
// Marker carries the run's --id-prefix and is the property both queries filter on. It is
// iverson_meta so that one value scopes BOTH stores: the object collection filters it as an
// ordinary scalar payload clause, and the chunks collection can filter it only because metadata
// columns are denormalized onto every chunk point. Title is the embedding source SearchSimilar
// searches; Body is the chunk source SearchChunks searches, short enough to produce a single window
// per row. Label is the row's per-language identity — SearchSimilar streams the Qdrant payload,
// whose row key lives under a reserved "key" entry no typed projection binds to Id — and its
// spelling must match VectorSearchScenario.LabelFor.
type VectorDoc struct {
	Id       string `iverson_key:"true" iverson_guid:"true"`
	TenantId string `iverson_tenant:"true"`
	OwnerId  string
	Marker   string `iverson_meta:"true"`
	Title    string `iverson_embedding:"true"`
	Body     string `iverson_chunk:"256:32"`
	Label    string
}

// IdentityDoc is S8 identity's subject type. Every one of the five drivers declares the same type
// name and shape; only the .NET driver ever registers it (register-once rule), and every driver
// writes one row into it, reads that row back, and then attempts one update under a deliberately
// wrong acting user.
//
// Deliberately relation-free and search-free: the axis is about WHOSE identity the server resolves
// a row's tenant and owner from, and a relation or a vector field would only add ways for the
// scenario to go red for reasons that are not about identity.
type IdentityDoc struct {
	Id       string `iverson_key:"true" iverson_guid:"true"`
	TenantId string `iverson_tenant:"true"`
	OwnerId  string
	Label    string
}
