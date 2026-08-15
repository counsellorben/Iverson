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
