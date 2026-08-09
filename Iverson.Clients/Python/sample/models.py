"""Sample entity definitions demonstrating the iverson_client annotation API."""
from __future__ import annotations

import uuid
from datetime import datetime

from iverson_client.annotations import (
    iverson_entity,
    iverson_key,
    iverson_search_key,
    iverson_tenant,
    iverson_large_field,
    many_to_one,
    one_to_many,
)


@iverson_entity
class Tag:
    id: uuid.UUID = iverson_key()
    name: str = None
    tenant_id: str = iverson_tenant()


@iverson_entity
class Author:
    id: uuid.UUID = iverson_key()
    name: str = None
    bio: str = iverson_large_field()
    articles: list = one_to_many("Article")
    tenant_id: str = iverson_tenant()


@iverson_entity
class Article:
    id: uuid.UUID = iverson_key()
    title: str = None
    body: str = iverson_large_field()
    category: str = iverson_search_key(order=0)
    word_count: int = None
    published_at: datetime = iverson_search_key(order=1)
    author_id: str = many_to_one("Author")
    tenant_id: str = iverson_tenant()
