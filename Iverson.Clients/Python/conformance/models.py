"""S1 ``crud-roundtrip`` entity models for the Python conformance driver.

Mirrors the .NET driver's ``DotNetArticle``/``DotNetAuthor``/``DotNetTag`` triple (see
``Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Models/``), adapted to the Python
client's declaration style: the relation lives directly on the foreign-key member (``py_author_id``,
``py_tag_ids``) rather than as a separate FK field plus an annotated navigation property.
"""
from __future__ import annotations

import uuid

from iverson_client.annotations import (
    iverson_chunk,
    iverson_embedding,
    iverson_entity,
    iverson_key,
    iverson_metadata,
    iverson_tenant,
    many_to_many,
    many_to_one,
    one_to_many,
    one_to_one,
)


@iverson_entity
class PyAuthor:
    """S1's "one" side. Carries the reverse ``one_to_many`` navigation the foreign-key-only
    write contract work broke, so the harness observes it end to end."""

    id: uuid.UUID = iverson_key()
    tenant_id: str = iverson_tenant()
    owner_id: str = None
    name: str = None
    py_articles: list = one_to_many("PyArticle")


@iverson_entity
class PyTag:
    id: uuid.UUID = iverson_key()
    tenant_id: str = iverson_tenant()
    owner_id: str = None
    label: str = None


@iverson_entity
class PyArticle:
    id: uuid.UUID = iverson_key()
    tenant_id: str = iverson_tenant()
    owner_id: str = None
    title: str = None
    py_author_id: str = many_to_one("PyAuthor")
    py_tag_ids: str = many_to_many("PyTag")
    # IVC-REL-001/002/003's one_to_one fixture: a second relation to PyTag (the many_to_many
    # relation's own related type), through the SINGULAR "py_tag_id" foreign key so it does not
    # collide with the many_to_many's plural "py_tag_ids" — exercising one_to_one end to end
    # without a whole new entity type.
    py_tag_id: str = one_to_one("PyTag")


@iverson_entity
class SharedAuthor:
    """S4 ``interop``'s "one" side. Every one of the five drivers declares the same type name and
    shape; only the .NET driver ever registers it (register-once rule), so this driver's own
    ``SchemaRegistrar`` is never invoked for it."""

    id: uuid.UUID = iverson_key()
    tenant_id: str = iverson_tenant()
    owner_id: str = None
    name: str = None


@iverson_entity
class SharedArticle:
    """S4 ``interop``'s root type."""

    id: uuid.UUID = iverson_key()
    tenant_id: str = iverson_tenant()
    owner_id: str = None
    title: str = None
    shared_author_id: str = many_to_one("SharedAuthor")


@iverson_entity
class PyBadArticle:
    """Exists only for the naming-rejected (S2) conformance scenario. ``writer_id`` declares a
    many_to_one relation to PyAuthor but is not named ``author_id`` — the name
    ``SchemaRegistrar`` requires, since the field itself IS the foreign key. Registering this
    type must fail client-side, before any RPC (see ``iverson_client/core.py``'s relation-naming
    check)."""

    id: uuid.UUID = iverson_key()
    tenant_id: str = iverson_tenant()
    owner_id: str = None
    writer_id: str = many_to_one("PyAuthor")

@iverson_entity
class QueryDoc:
    """S6 ``query``'s subject type. Every one of the five drivers declares the same type name and
    shape; only the .NET driver ever registers it (register-once rule), and every driver writes one
    row into it and then queries it.

    Deliberately relation-free: the scenario's exact result-set comparison is over row keys, and a
    relation would drag hydration into what a search returns without adding anything the QRY axis
    asserts. ``marker`` carries the run's ``--id-prefix`` and is the property every driver filters
    on — unique per run, so the expected result set is exactly this run's rows."""

    id: uuid.UUID = iverson_key()
    tenant_id: str = iverson_tenant()
    owner_id: str = None
    marker: str = None
    label: str = None


@iverson_entity
class VectorDoc:
    """S7 ``vector-search``'s subject type. Every one of the five drivers declares the same type
    name and shape; only the .NET driver ever registers it (register-once rule), and every driver
    writes one row into it and then searches it.

    Deliberately relation-free, and deliberately without any enrichment annotation (summary,
    keywords, contextual chunking): the scenario's exact set comparisons must not depend on
    generative output that differs run to run.

    ``marker`` carries the run's ``--id-prefix`` and is the property both queries filter on. It is
    metadata so that one value scopes BOTH stores: the object collection filters it as an ordinary
    scalar payload clause, and the chunks collection can filter it only because metadata columns are
    denormalized onto every chunk point. ``title`` is the embedding source ``SearchSimilar``
    searches; ``body`` is the chunk source ``SearchChunks`` searches, short enough to produce a
    single window per row. ``label`` is the row's per-language identity — ``SearchSimilar`` streams
    the Qdrant payload, whose row key lives under a reserved ``key`` entry no typed projection binds
    to ``id`` — and its spelling must match ``VectorSearchScenario.LabelFor``."""

    id: uuid.UUID = iverson_key()
    tenant_id: str = iverson_tenant()
    owner_id: str = None
    marker: str = iverson_metadata()
    title: str = iverson_embedding()
    body: str = iverson_chunk(max_tokens=256, overlap=32)
    label: str = None
