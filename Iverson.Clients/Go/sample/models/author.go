package models

// Author is a sample entity demonstrating a one-to-many relation.
type Author struct {
	Id       string `iverson:"key"`
	Name     string
	Email    string
	Articles []string `iverson:"one_to_many:Article"`
}
