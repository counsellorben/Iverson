package io.iverson.client.search;

import com.google.protobuf.util.JsonFormat;
import iverson.ObjectSearch.AfterMatchSkipKind;
import iverson.ObjectSearch.MatchPatternRequest;
import iverson.ObjectSearch.PatternRowSource;
import iverson.ObjectSearch.RowsPerMatch;
import iverson.ObjectSearch.SearchLogic;
import iverson.ObjectSearch.SearchOperator;
import org.json.JSONException;
import org.junit.jupiter.api.Test;
import org.skyscreamer.jsonassert.JSONAssert;
import org.skyscreamer.jsonassert.JSONCompareMode;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;

import static org.junit.jupiter.api.Assertions.*;

class MatchPatternBuilderTest {

    @Test
    void chunks_setsSourceAndChunkProperty() {
        MatchPatternRequest req = Query.matchPattern("VectorDoc").chunks("Body").build();

        assertEquals(PatternRowSource.CHUNKS, req.getSource());
        assertEquals("Body", req.getChunkProperty());
    }

    @Test
    void afterMatch_isUnsetUnlessCalled() {
        MatchPatternRequest req = Query.matchPattern("PatternDoc").pattern("A").build();

        assertFalse(req.hasAfterMatch());
    }

    @Test
    void limit_isUnsetUnlessCalled() {
        MatchPatternRequest req = Query.matchPattern("PatternDoc").pattern("A").build();

        assertEquals(0, req.getLimit());
    }

    @Test
    void build_withoutTraceId_givesEmptyTraceId() {
        MatchPatternRequest req = Query.matchPattern("PatternDoc").pattern("A").build();

        assertEquals("", req.getTraceId());
    }

    // ── Cross-language golden-fixture contract ───────────────────────────────
    // Golden fixtures checked in at Iverson.Clients/Common/testdata/match-pattern-contract-{1,2}.json.
    // Every SDK's builder, driven by the same call sequence, must serialize to the same JSON.
    // Do not hand-edit the JSON files.

    @Test
    void build_matchesGoldenFixture_matchPatternContract1() throws IOException, JSONException {
        MatchPatternRequest request = Query.matchPattern("PatternDoc")
            .where("Marker", SearchOperator.EQUALS, "m1")
            .not("Label", SearchOperator.EQUALS, "skip")
            .withLogic(SearchLogic.OR)
            .partitionBy("Label")
            .orderBy("Seq")
            .orderBy("Id", true)
            .pattern("A B+")
            .subset("AB", "A", "B")
            .define("B", "Seq > PREV(Seq)")
            .measure("n", "COUNT(*)")
            .measure("last_seq", "LAST(B.Seq)")
            .rowsPerMatch(RowsPerMatch.ALL_ROWS_SHOW_EMPTY)
            .afterMatch(AfterMatchSkipKind.TO_FIRST, "B")
            .limit(100)
            .build("trace-1");

        String actualJson = JsonFormat.printer().print(request);
        String expectedJson = Files.readString(goldenFixturePath("match-pattern-contract-1.json"));

        JSONAssert.assertEquals(expectedJson, actualJson, JSONCompareMode.STRICT);
    }

    @Test
    void build_matchesGoldenFixture_matchPatternContract2() throws IOException, JSONException {
        MatchPatternRequest request = Query.matchPattern("VectorDoc")
            .chunks("Body")
            .pattern("A")
            .define("A", "SIMILARITY(text, 'refund') > 0.5")
            .build("");

        String actualJson = JsonFormat.printer().print(request);
        String expectedJson = Files.readString(goldenFixturePath("match-pattern-contract-2.json"));

        JSONAssert.assertEquals(expectedJson, actualJson, JSONCompareMode.STRICT);
    }

    /** Resolves a shared golden fixture relative to this module's basedir ({@code Iverson.Clients/Java/client}). */
    private static Path goldenFixturePath(String fileName) {
        return Paths.get("..", "..", "Common", "testdata", fileName);
    }
}
