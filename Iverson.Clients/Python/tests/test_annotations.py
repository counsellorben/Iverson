"""Tests for the @iverson_entity decorator and field-level annotation helpers."""
import pytest
from datetime import datetime

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


# ── Sample entity ──────────────────────────────────────────────────────────────

@iverson_entity
class Article:
    id: str = iverson_key()
    title: str = None
    body: str = iverson_large_field()
    category: str = iverson_search_key(order=0)
    word_count: int = None
    published_at: datetime = iverson_search_key(order=1)
    author_id: str = many_to_one('Author')


@iverson_entity
class Author:
    id: str = iverson_key()
    name: str = None
    articles: list = one_to_many('Article')


@iverson_entity
class Tag:
    id: str = iverson_key()
    label: str = None


# ── Tests ──────────────────────────────────────────────────────────────────────

class TestIversonEntityMetadata:
    def test_meta_dict_present(self):
        assert hasattr(Article, "_iverson_meta")

    def test_type_name(self):
        assert Article._iverson_meta["type_name"] == "Article"

    def test_key_field_detected(self):
        assert Article._iverson_meta["key_field"] == "id"

    def test_large_field_detected(self):
        assert "body" in Article._iverson_meta["large_fields"]

    def test_search_keys_detected(self):
        search_keys = Article._iverson_meta["search_keys"]
        names = [f for f, _ in search_keys]
        assert "category" in names
        assert "published_at" in names

    def test_search_key_order(self):
        search_keys = dict(Article._iverson_meta["search_keys"])
        assert search_keys["category"] == 0
        assert search_keys["published_at"] == 1

    def test_search_keys_sorted_by_order(self):
        search_keys = Article._iverson_meta["search_keys"]
        orders = [o for _, o in search_keys]
        assert orders == sorted(orders)

    def test_relation_detected(self):
        relations = Article._iverson_meta["relations"]
        assert len(relations) == 1
        rel = relations[0]
        assert rel["field"] == "author_id"
        assert rel["kind"] == "many_to_one"
        assert rel["related_type"] == "Author"

    def test_one_to_many_relation(self):
        relations = Author._iverson_meta["relations"]
        assert len(relations) == 1
        assert relations[0]["kind"] == "one_to_many"
        assert relations[0]["related_type"] == "Article"

    def test_field_meta_replaced_with_none(self):
        """FieldMeta sentinel must be replaced so instances can set these attrs."""
        assert Article.id is None
        assert Article.body is None
        assert Article.category is None


class TestEntityInstantiation:
    def test_can_create_instance(self):
        """After decoration, plain instances work normally."""
        a = Article()
        a.id = "123"
        a.title = "Hello"
        a.body = "Body text"
        assert a.id == "123"
        assert a.title == "Hello"

    def test_key_field_not_set_by_default(self):
        a = Article()
        assert a.id is None


class TestMultipleEntities:
    def test_each_has_own_meta(self):
        assert Article._iverson_meta["type_name"] == "Article"
        assert Author._iverson_meta["type_name"] == "Author"
        assert Tag._iverson_meta["type_name"] == "Tag"

    def test_tag_has_no_relations(self):
        assert Tag._iverson_meta["relations"] == []

    def test_tag_has_no_large_fields(self):
        assert Tag._iverson_meta["large_fields"] == []

    def test_tag_has_key(self):
        assert Tag._iverson_meta["key_field"] == "id"


class TestRelationKinds:
    def test_many_to_many_relation(self):
        @iverson_entity
        class Post:
            id: str = iverson_key()
            tags: list = many_to_many("Tag")

        rels = Post._iverson_meta["relations"]
        assert len(rels) == 1
        assert rels[0]["kind"] == "many_to_many"
        assert rels[0]["related_type"] == "Tag"

    def test_one_to_one_relation(self):
        @iverson_entity
        class Profile:
            id: str = iverson_key()
            user: object = one_to_one("User")

        rels = Profile._iverson_meta["relations"]
        assert len(rels) == 1
        assert rels[0]["kind"] == "one_to_one"
