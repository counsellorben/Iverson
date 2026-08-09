package models

// Tag is a sample entity demonstrating a many-to-many relation.
type Tag struct {
	Id       string   `iverson_key:"true" iverson_guid:"true"`
	Name     string   `iverson_search_key:"0"`
	Articles []string `iverson:"many_to_many:Article"`
	TenantId string   `iverson_tenant:"true"`
}
