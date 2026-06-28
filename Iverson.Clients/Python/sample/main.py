"""
Sample demonstrating IversonClient, SchemaRegistrar, EntityCoordinator,
and QueryBuilder — no live server required for the schema/query examples.
"""
from __future__ import annotations

import sys
import os

# Allow running from the Python/ directory directly
sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))

from iverson_client import IversonClient, QueryBuilder
from sample.models import Article, Author


def demo_query_builder() -> None:
    """Show how to construct a SearchRequest with QueryBuilder."""
    request = (
        QueryBuilder("Article")
        .where("category").eq("technology")
        .where("word_count").gte(500)
        .where("body").contains("machine learning")
        .order_by("published_at", descending=True)
        .limit(10)
        .build()
    )
    print(f"SearchRequest type_name : {request.type_name}")
    print(f"SearchRequest clauses   : {len(request.query.clauses)}")
    print(f"SearchRequest page_size : {request.page_size}")
    for clause in request.query.clauses:
        print(f"  [{clause.property}] op={clause.operator} "
              f"val={clause.value}")


def demo_vector_search() -> None:
    """Show a vector similarity search clause."""
    query_vec = [0.1, 0.2, 0.3, 0.4, 0.5]
    request = (
        QueryBuilder("Article")
        .where("embedding").vector_similar(query_vec)
        .limit(5)
        .build()
    )
    print(f"\nVector search — floats in clause: "
          f"{list(request.query.clauses[0].value.float_list.values)}")


def demo_in_operator() -> None:
    """Show the IN operator with a string list."""
    request = (
        QueryBuilder("Article")
        .where("category").in_(["technology", "science", "ai"])
        .build()
    )
    print(f"\nIN operator values: "
          f"{list(request.query.clauses[0].value.string_list.values)}")


if __name__ == "__main__":
    print("=== QueryBuilder demo ===")
    demo_query_builder()
    demo_vector_search()
    demo_in_operator()

    print("\n=== Entity metadata ===")
    for name, meta in [("Article", Article._iverson_meta),
                       ("Author", Author._iverson_meta)]:
        print(f"{name}: key={meta['key_field']}, "
              f"search_keys={meta['search_keys']}, "
              f"large_fields={meta['large_fields']}, "
              f"relations={[r['field'] for r in meta['relations']]}")
