"""Iverson Python gRPC client."""

from iverson_client.annotations import (
    iverson_entity,
    iverson_key,
    iverson_search_key,
    iverson_large_field,
    many_to_one,
    many_to_many,
    one_to_many,
    one_to_one,
    FieldMeta,
)
from iverson_client.core import IversonClient, EntityCoordinator, SchemaRegistrar
from iverson_client.group_by import GroupByBuilder
from iverson_client.pipeline import PipelineBuilder, PipelineStepBuilder, pipeline
from iverson_client.search import QueryBuilder
from iverson_client.search import group_by as group_by
from iverson_client.vector_search import SimilarBuilder, ChunksBuilder, similar, chunks

__all__ = [
    "iverson_entity",
    "iverson_key",
    "iverson_search_key",
    "iverson_large_field",
    "many_to_one",
    "many_to_many",
    "one_to_many",
    "one_to_one",
    "FieldMeta",
    "IversonClient",
    "EntityCoordinator",
    "SchemaRegistrar",
    "QueryBuilder",
    "GroupByBuilder",
    "group_by",
    "PipelineBuilder",
    "PipelineStepBuilder",
    "pipeline",
    "SimilarBuilder",
    "ChunksBuilder",
    "similar",
    "chunks",
]
