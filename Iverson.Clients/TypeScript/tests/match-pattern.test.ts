import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { describe, expect, it } from 'vitest';
import * as client from '../src/index.js';
import { MatchPatternBuilder, matchPattern } from '../src/match-pattern.js';
import {
    AfterMatchSkipKind,
    MatchPatternRequest,
    PatternRowSource,
    RowsPerMatch,
    SearchLogic,
    SearchOperator,
} from '../generated/object_search.js';

const __dirname = dirname(fileURLToPath(import.meta.url));

// Shared cross-language golden fixtures, checked in at Iverson.Clients/Common/testdata/.
// Every language's builder must produce the same structural JSON for the same logical
// request. Do not hand-edit the JSON files.
const TESTDATA = join(__dirname, '..', '..', 'Common', 'testdata');

describe('MatchPatternBuilder', () => {
    it('build() matches the golden fixture match-pattern-contract-1.json', () => {
        const request = matchPattern('PatternDoc')
            .where('Marker', SearchOperator.EQUALS, 'm1')
            .not('Label', SearchOperator.EQUALS, 'skip')
            .withLogic(SearchLogic.OR)
            .partitionBy('Label')
            .orderBy('Seq')
            .orderBy('Id', true)
            .pattern('A B+')
            .subset('AB', 'A', 'B')
            .define('B', 'Seq > PREV(Seq)')
            .measure('n', 'COUNT(*)')
            .measure('last_seq', 'LAST(B.Seq)')
            .rowsPerMatch(RowsPerMatch.ALL_ROWS_SHOW_EMPTY)
            .afterMatch(AfterMatchSkipKind.TO_FIRST, 'B')
            .limit(100)
            .build('trace-1');

        const actual = MatchPatternRequest.toJSON(request);
        const expected = JSON.parse(readFileSync(join(TESTDATA, 'match-pattern-contract-1.json'), 'utf-8'));

        expect(actual).toEqual(expected);
    });

    it('build() matches the golden fixture match-pattern-contract-2.json', () => {
        const request = matchPattern('VectorDoc')
            .chunks('Body')
            .pattern('A')
            .define('A', "SIMILARITY(text, 'refund') > 0.5")
            .build('');

        const actual = MatchPatternRequest.toJSON(request);
        const expected = JSON.parse(readFileSync(join(TESTDATA, 'match-pattern-contract-2.json'), 'utf-8'));

        expect(actual).toEqual(expected);
    });

    it('chunks() sets the source and the chunk property', () => {
        const request = matchPattern('VectorDoc').chunks('Body').build();

        expect(request.source).toBe(PatternRowSource.CHUNKS);
        expect(request.chunkProperty).toBe('Body');
    });

    it('afterMatch stays unset unless afterMatch() is called', () => {
        const request = matchPattern('PatternDoc').pattern('A').build();

        expect(request.afterMatch).toBeUndefined();
    });

    it('the trace id defaults to an empty string', () => {
        const request = matchPattern('PatternDoc').build();

        expect(request.traceId).toBe('');
    });

    it('the builder and its factory are exported from the package index', () => {
        expect(client.matchPattern).toBe(matchPattern);
        expect(client.MatchPatternBuilder).toBe(MatchPatternBuilder);
    });
});
