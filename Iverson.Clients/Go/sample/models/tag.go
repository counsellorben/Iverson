package models

// Tag is a sample entity demonstrating a many-to-many relation.
type Tag struct {
	Id       string   `iverson:"key"`
	Name     string   `iverson:"search_key:0"`
	Articles []string `iverson:"many_to_many:Article"`
}
