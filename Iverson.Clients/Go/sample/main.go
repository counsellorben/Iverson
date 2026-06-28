// Package main demonstrates the Iverson Go client with sample models.
// It does NOT connect to a live server — it shows how to build queries
// and inspect schema metadata without a running Iverson instance.
package main

import (
	"fmt"
	"log"
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
		if f.Kind == "" {
			fmt.Printf("  %s (plain)\n", f.Name)
		} else {
			fmt.Printf("  %s (%s)\n", f.Name, f.Kind)
		}
	}
	fmt.Printf("Relations (%d):\n", len(meta.Relations))
	for _, r := range meta.Relations {
		fmt.Printf("  %s → %s (%s)\n", r.Name, r.RelatedType, r.Kind)
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

	// ── IN / VectorSimilar examples ────────────────────────────────────────────
	req2, err := iverson.NewQuery("Article").
		Where("Category").In("tech", "science", "health").
		Where("Body").VectorSimilar([]float32{0.1, 0.2, 0.3}).
		Build()
	if err != nil {
		log.Fatalf("Build req2: %v", err)
	}
	fmt.Printf("\nVector+IN request: %d clauses\n", len(req2.Query.Clauses))
}
