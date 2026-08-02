// Package main demonstrates the Iverson Go client with sample models.
// It does NOT connect to a live server — it shows how to build queries
// and inspect schema metadata without a running Iverson instance.
package main

import (
	"fmt"
	"log"
	"strings"
	"time"

	"github.com/iverson/clients/go/iverson"
	"github.com/iverson/clients/go/sample/models"
)

func main() {
	// ── Schema inspection ──────────────────────────────────────────────────────
	meta, err := iverson.InspectType(models.Article{})
	if err != nil {
		log.Fatalf("InspectType: %v", err)
	}
	fmt.Printf("Entity: %s\n", meta.TypeName)
	fmt.Printf("Fields (%d):\n", len(meta.Fields))
	for _, f := range meta.Fields {
		if !f.IsKey && !f.IsSearchKey && !f.IsLargeField && !f.IsEmbedding && !f.IsChunk &&
			f.RelationKind == "" {
			fmt.Printf("  %s (plain)\n", f.Name)
		} else {
			var flags []string
			if f.IsKey {
				flags = append(flags, "key")
			}
			if f.IsSearchKey {
				flags = append(flags, "search_key")
			}
			if f.IsLargeField {
				flags = append(flags, "large_field")
			}
			if f.IsEmbedding {
				flags = append(flags, "embedding")
			}
			if f.IsChunk {
				flags = append(flags, "chunk")
			}
			fmt.Printf("  %s (%s)\n", f.Name, strings.Join(flags, ", "))
		}
	}
	fmt.Printf("Relations (%d):\n", len(meta.Relations))
	for _, r := range meta.Relations {
		fmt.Printf("  %s → %s (%s)\n", r.Name, r.RelatedType, r.RelationKind)
	}

	// ── QueryBuilder ───────────────────────────────────────────────────────────
	req, err := iverson.NewQuery("Article").
		Where("Category").Eq("tech").
		Where("WordCount").Gt(500).
		Where("PublishedAt").Gte(time.Date(2024, 1, 1, 0, 0, 0, 0, time.UTC)).
		OrderByDesc("PublishedAt").
		Limit(20).
		Offset(0).
		Build()
	if err != nil {
		log.Fatalf("Build: %v", err)
	}

	fmt.Printf("\nSearch request for %q:\n", req.TypeName)
	fmt.Printf("  Clauses: %d\n", len(req.Query.Clauses))
	fmt.Printf("  Sorts:   %d\n", len(req.Query.Sort))
	fmt.Printf("  Page:    %d  PageSize: %d\n", req.Page, req.PageSize)

	// ── IN example ─────────────────────────────────────────────────────────────
	req2, err := iverson.NewQuery("Article").
		Where("Category").In("tech", "science", "health").
		Build()
	if err != nil {
		log.Fatalf("Build req2: %v", err)
	}
	fmt.Printf("\nIN request: %d clauses\n", len(req2.Query.Clauses))
}
