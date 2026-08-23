package models

import "time"

// Article is a sample entity demonstrating all Iverson struct tag forms.
type Article struct {
	Id          string `iverson_key:"true" iverson_guid:"true"`
	Title       string
	Body        string `iverson_large_field:"true" iverson_chunk:"256:32"`
	Category    string `iverson_search_key:"0"`
	WordCount   int
	PublishedAt time.Time `iverson_search_key:"1"`
	AuthorId    string    `iverson:"many_to_one:Author"`
}
