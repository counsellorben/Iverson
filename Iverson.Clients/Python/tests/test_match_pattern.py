import json
from pathlib import Path

from google.protobuf.json_format import MessageToJson

import iverson_client
from iverson_client import MatchPatternBuilder, match_pattern
from iverson_client.generated import object_search_pb2 as pb

# Shared cross-language golden fixtures, checked in at Iverson.Clients/Common/testdata/.
# Every language's builder must produce the same structural JSON for the same logical
# request. Do not hand-edit the JSON files.
_TESTDATA = Path(__file__).resolve().parents[2] / "Common" / "testdata"


def test_build_matches_golden_fixture_match_pattern_contract_1():
    request = (
        match_pattern("PatternDoc")
        .where("Marker", pb.EQUALS, "m1")
        .not_("Label", pb.EQUALS, "skip")
        .with_logic(pb.OR)
        .partition_by("Label")
        .order_by("Seq")
        .order_by("Id", descending=True)
        .pattern("A B+")
        .subset("AB", "A", "B")
        .define("B", "Seq > PREV(Seq)")
        .measure("n", "COUNT(*)")
        .measure("last_seq", "LAST(B.Seq)")
        .rows_per_match(pb.ALL_ROWS_SHOW_EMPTY)
        .after_match(pb.TO_FIRST, "B")
        .limit(100)
        .build("trace-1")
    )

    actual = json.loads(MessageToJson(request))
    expected = json.loads((_TESTDATA / "match-pattern-contract-1.json").read_text())

    assert actual == expected


def test_build_matches_golden_fixture_match_pattern_contract_2():
    request = (
        match_pattern("VectorDoc")
        .chunks("Body")
        .pattern("A")
        .define("A", "SIMILARITY(text, 'refund') > 0.5")
        .build("")
    )

    actual = json.loads(MessageToJson(request))
    expected = json.loads((_TESTDATA / "match-pattern-contract-2.json").read_text())

    assert actual == expected


def test_chunks_sets_the_source_and_the_chunk_property():
    request = match_pattern("VectorDoc").chunks("Body").build()

    assert request.source == pb.CHUNKS
    assert request.chunk_property == "Body"


def test_after_match_stays_unset_unless_called():
    request = match_pattern("PatternDoc").pattern("A").build()

    assert not request.HasField("after_match")


def test_trace_id_defaults_to_empty():
    request = match_pattern("PatternDoc").build()

    assert request.trace_id == ""


def test_match_pattern_names_are_exported():
    assert isinstance(match_pattern("PatternDoc"), MatchPatternBuilder)
    assert {"MatchPatternBuilder", "match_pattern", "MatchPatternResult"} <= set(iverson_client.__all__)
