import pytest

from iverson_client.generated import object_search_pb2 as pb
from iverson_client.vector_search import similar, chunks


def test_similar_build_happy_path_produces_expected_request():
    req = (
        similar("Article", "Title")
        .text("machine learning")
        .top_k(10)
        .where("Category", pb.EQUALS, "Tech")
        .build()
    )
    assert req.type_name == "Article"
    assert req.property == "Title"
    assert req.query == "machine learning"
    assert req.top_k == 10
    assert len(req.filter) == 1
    assert req.filter[0].property == "Category"


def test_similar_where_contains_operator_raises():
    b = similar("Article", "Title")
    with pytest.raises(ValueError):
        b.where("Category", pb.CONTAINS, "x")


def test_similar_where_vector_similar_operator_raises():
    b = similar("Article", "Title")
    with pytest.raises(ValueError):
        b.where("Category", pb.VECTOR_SIMILAR, "x")


def test_chunks_build_happy_path_produces_expected_request():
    req = (
        chunks("Article", "Body")
        .text("neural networks")
        .top_k(5)
        .where("Id", pb.EQUALS, "parent-123")
        .build()
    )
    assert req.type_name == "Article"
    assert req.property == "Body"
    assert req.top_k == 5
    assert len(req.filter) == 1
    assert req.filter[0].property == "Id"


def test_chunks_where_non_equals_operator_raises():
    b = chunks("Article", "Body")
    with pytest.raises(ValueError):
        b.where("Id", pb.GREATER_THAN, "x")


def test_chunks_where_called_twice_raises_on_second_call():
    b = chunks("Article", "Body").where("Id", pb.EQUALS, "a")
    with pytest.raises(ValueError):
        b.where("Id", pb.EQUALS, "b")
