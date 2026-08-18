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
