package models

import "time"

// Article is a sample entity demonstrating all Iverson struct tag forms.
type Article struct {
	Id          string    `iverson:"key"`
	Title       string
	Body        string    `iverson:"large_field"`
	Category    string    `iverson:"search_key:0"`
	WordCount   int
	PublishedAt time.Time `iverson:"search_key:1"`
	AuthorId    string    `iverson:"many_to_one:Author"`
}
