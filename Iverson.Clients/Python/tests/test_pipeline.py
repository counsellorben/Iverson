import pytest

from iverson_client import pipeline
from iverson_client.generated import object_search_pb2 as pb


def test_build_full_pipeline_compiles_to_expected_proto():
    request = (
        pipeline("Article")
        .where("IsPublished", pb.EQUALS, True)
        .step("by_author", lambda s: s
              .group_by("AuthorId")
              .count_all("articles")
              .having("articles", pb.GREATER_THAN, 5))
        .step("ranked", lambda s: s
              .row_number("rank", order_by="articles", descending=True))
        .step("named", lambda s: s
              .join("Author", "AuthorId", "Id")
              .select(lambda sel: sel.all_from("ranked").pick("Author", "Name", "author_name")))
        .sort_on_desc("rank")
        .limit(5)
        .build()
    )

    assert request.type_name == "Article"
    assert len(request.base_where) == 1
    assert len(request.steps) == 3

    agg = request.steps[0]
    assert agg.name == "by_author"
    assert agg.group_by[0].field == "AuthorId"
    assert agg.group_by[0].date_trunc == pb.NONE
    assert agg.metrics[0].name == "articles"
    assert agg.having[0].property == "articles"

    win = request.steps[1]
    assert win.windows[0].kind == pb.ROW_NUMBER
    assert win.windows[0].descending is True

    joined = request.steps[2]
    assert joined.joins[0].source == "Author"
    assert joined.joins[0].on[0].left == "AuthorId"
    assert joined.select[0].all is True
    assert joined.select[1].alias == "author_name"

    assert request.limit == 5


def test_reads_carried_and_defaults_to_10000_limit():
    request = (
        pipeline("Article")
        .step("a", lambda s: s.derive("x", "WordCount + 1"))
        .step("b", lambda s: s.reads("base").derive("y", "WordCount + 2"))
        .build()
    )
    assert request.steps[1].reads == "base"
    assert request.limit == 10_000


def test_duplicate_step_name_raises():
    b = pipeline("Article").step("x", lambda s: s.derive("a", "WordCount"))
    with pytest.raises(ValueError, match="X"):
        b.step("X", lambda s: s.derive("b", "WordCount"))


def test_reads_unknown_step_raises():
    with pytest.raises(ValueError, match="nope"):
        pipeline("Article").step("a", lambda s: s.reads("nope"))


def test_window_and_group_by_in_one_step_raises():
    with pytest.raises(ValueError, match="bad"):
        pipeline("Article").step("bad", lambda s: s
                                 .row_number("rn", order_by="Id")
                                 .group_by("AuthorId").count_all("n"))


def test_join_without_select_raises():
    with pytest.raises(ValueError, match="select"):
        pipeline("Article").step("bad", lambda s: s.join("Author", "AuthorId", "Id"))


def test_duplicate_aliases_raise():
    with pytest.raises(ValueError, match="X"):
        pipeline("Article").step("bad", lambda s: s
                                 .row_number("x", order_by="Id")
                                 .derive("X", "WordCount + 1"))


def test_date_trunc_group_key():
    request = (
        pipeline("Article")
        .step("m", lambda s: s.group_by("PublishedAt", pb.MONTH).count_all("n"))
        .build()
    )
    assert request.steps[0].group_by[0].date_trunc == pb.MONTH
