package models

// Author is a sample entity demonstrating a one-to-many relation.
type Author struct {
	Id       string `iverson_key:"true"`
	Name     string
	Email    string
	Articles []string `iverson:"one_to_many:Article"`
	TenantId string   `iverson_tenant:"true"`
}
