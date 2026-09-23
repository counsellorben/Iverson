from datetime import datetime, timedelta, timezone
from unittest.mock import MagicMock

import pytest

from iverson_client.generated import object_mapping_pb2 as mpb
from iverson_agent.schema import (
    SchemaCache, TypeNotAccessible, ValidFilter, render_schema, resolve_type, validate_filters,
)


def policy_doc_type() -> mpb.SchemaType:
    t = mpb.SchemaType(name="PolicyDoc", description="A policy document.")
    t.fields.add(name="Id", clr_type=mpb.CLR_GUID, is_key=True)
    t.fields.add(name="Title", clr_type=mpb.CLR_STRING, description="Document title")
    t.fields.add(name="Source", clr_type=mpb.CLR_STRING, is_metadata=True, description="hr|legal|it")
    t.fields.add(name="PublishedAt", clr_type=mpb.CLR_DATETIME, is_metadata=True, description="Published")
    t.fields.add(name="WordCount", clr_type=mpb.CLR_INT32, is_metadata=True)
    t.fields.add(name="Body", clr_type=mpb.CLR_STRING, is_chunk=True)
    return t


def test_resolve_type_is_case_insensitive_and_raises_when_absent():
    assert resolve_type([policy_doc_type()], "policydoc").name == "PolicyDoc"
    with pytest.raises(TypeNotAccessible):
        resolve_type([policy_doc_type()], "Other")


def test_render_schema_lists_only_metadata_fields_by_schema_name():
    text = render_schema(policy_doc_type())
    assert "PolicyDoc" in text and "A policy document." in text
    assert "Source" in text and "PublishedAt" in text and "WordCount" in text
    assert "Title" not in text and "Body" not in text


def test_validate_filters_matches_case_insensitively_and_emits_canonical_spelling():
    valid = validate_filters([("PUBLISHEDAT", "2025-11-02"), ("SOURCE", "legal")],
                             policy_doc_type(), trace_id="t")
    assert valid == [ValidFilter("PublishedAt", "2025-11-02"), ValidFilter("Source", "legal")]


def test_validate_filters_drops_unknown_non_metadata_and_uncoercible():
    valid = validate_filters(
        [("Nope", "x"), ("Title", "x"), ("WordCount", "many"), ("WordCount", "12")],
        policy_doc_type(), trace_id="t")
    assert valid == [ValidFilter("WordCount", 12.0)]


def test_schema_cache_uses_factory_once_per_user_within_ttl():
    client = MagicMock()
    client.__enter__.return_value = client
    client.get_schema.return_value = [policy_doc_type()]
    factory = MagicMock(return_value=client)
    cache = SchemaCache(ttl=timedelta(minutes=10), client_factory=factory)

    first = cache.get("user-a", "token-a")
    second = cache.get("user-a", "token-a")
    assert first is second
    factory.assert_called_once_with("token-a")


def test_schema_cache_refetches_once_the_ttl_has_expired():
    client = MagicMock()
    client.__enter__.return_value = client
    client.get_schema.return_value = [policy_doc_type()]
    factory = MagicMock(return_value=client)
    cache = SchemaCache(ttl=timedelta(0), client_factory=factory)
    cache.get("user-a", "token-a")
    cache.get("user-a", "token-a")
    assert factory.call_count == 2


def test_schema_cache_drops_the_expired_entry_before_refetching():
    def boom(token):
        raise RuntimeError("factory called")
    cache = SchemaCache(ttl=timedelta(minutes=10), client_factory=boom)
    cache._entries["user-a"] = (datetime.now(timezone.utc) - timedelta(hours=1), [policy_doc_type()])
    with pytest.raises(RuntimeError):
        cache.get("user-a", "token-a")
    assert "user-a" not in cache._entries        # stale schema is never left behind


def test_schema_cache_is_per_user():
    client = MagicMock()
    client.__enter__.return_value = client
    client.get_schema.return_value = [policy_doc_type()]
    factory = MagicMock(return_value=client)
    cache = SchemaCache(ttl=timedelta(minutes=10), client_factory=factory)
    cache.get("user-a", "token-a")
    cache.get("user-b", "token-b")
    assert factory.call_count == 2
