"""S1 ``crud-roundtrip`` entity models for the Python conformance driver.

Mirrors the .NET driver's ``DotNetArticle``/``DotNetAuthor``/``DotNetTag`` triple (see
``Iverson.Clients/DotNet/Iverson.Client.Conformance.Driver/Models/``), adapted to the Python
client's declaration style: the relation lives directly on the foreign-key member (``py_author_id``,
``py_tag_ids``) rather than as a separate FK field plus an annotated navigation property.
"""
from __future__ import annotations

import uuid

from iverson_client.annotations import (
    iverson_entity,
    iverson_key,
    iverson_tenant,
    many_to_many,
    many_to_one,
)


@iverson_entity
class PyAuthor:
    id: uuid.UUID = iverson_key()
    tenant_id: str = iverson_tenant()
    owner_id: str = None
    name: str = None


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
