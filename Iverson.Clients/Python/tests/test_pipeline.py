import json
from pathlib import Path

import pytest
from google.protobuf.json_format import MessageToJson

from iverson_client import pipeline
from iverson_client.generated import object_search_pb2 as pb

# Shared cross-language golden fixture. Generated from the C# builder (the reference
# implementation); every language's builder must produce the same structural JSON for
# the same logical request.
_GOLDEN_FIXTURE = (
    Path(__file__).resolve().parents[2] / "Common" / "testdata" / "pipeline-contract-1.json"
)


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


def test_join_all_with_composite_key_adds_multiple_conditions():
    request = (
        pipeline("Article")
        .step("enriched", lambda s: s
              .join_all("Author", [("AuthorId", "Id"), ("TenantId", "TenantId")])
              .select(lambda sel: sel.all_from("base")))
        .build()
    )
    join = request.steps[0].joins[0]
    assert len(join.on) == 2
    assert join.on[0].left == "AuthorId" and join.on[0].right == "Id"
    assert join.on[1].left == "TenantId" and join.on[1].right == "TenantId"


def test_step_with_reserved_name_base_raises():
    with pytest.raises(ValueError):
        pipeline("Article").step("base", lambda s: s).build()


def test_step_with_metrics_but_no_group_by_raises():
    with pytest.raises(ValueError):
        pipeline("Article").step("s1", lambda s: s.count("id")).build()


# ── Cross-language golden-fixture contract ──────────────────────────────────
# Golden fixture generated from the C# builder (the reference implementation), checked
# in at Iverson.Clients/Common/testdata/pipeline-contract-1.json. Same logical request —
# a base-step filter, an aggregate step, and a composite-key join step (2 ON pairs) with
# a select projection — built here via Python's pipeline(...), must serialize to the
# same JSON structure.

def test_build_matches_golden_fixture_pipeline_contract_1():
    request = (
        pipeline("Article")
        .where("IsPublished", pb.EQUALS, True)
        .step("by_author", lambda s: s
              .group_by("AuthorId")
              .count_all("articles")
              .having("articles", pb.GREATER_THAN, 5))
        .step("enriched", lambda s: s
              .join_all("Author", [("AuthorId", "Id"), ("TenantId", "TenantId")])
              .select(lambda sel: sel.all_from("by_author").pick("Author", "Name", "author_name")))
        .sort_on_desc("articles")
        .limit(25)
        .build("fixture-trace-id")
    )

    actual = json.loads(MessageToJson(request))
    expected = json.loads(_GOLDEN_FIXTURE.read_text())

    assert actual == expected
