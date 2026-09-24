# Third-party notices

## Trino

`Iverson.Server/Iverson.Patterns` contains C# ports of source files from Trino 483
(https://github.com/trinodb/trino, tag `483`), used under the Apache License, Version 2.0
(https://www.apache.org/licenses/LICENSE-2.0). Each ported file names its Trino origin in its header.
Ported: `io.trino.operator.window.matcher` (`Matcher`, `ThreadEquivalence`, `Instruction`, `Captures`, `IntList`,
`IntStack`, `IntMultimap`, `ArrayView`, `MatchResult`, `IrRowPatternToProgramRewriter`),
`io.trino.operator.window.PatternRecognitionPartition`, `io.trino.operator.window.pattern`
(`LogicalIndexNavigation`, `MeasureComputation`, `LabelEvaluator`), and `io.trino.sql.planner.rowpattern`
(`IrRowPatternFlattener`, `IrPatternAlternationOptimizer`).
Modifications: rewritten in C#; recursion replaced by explicit stacks; aggregations recomputed from matched
labels instead of carried per thread; thread-equivalence navigation offsets walked at comparison time instead of
materialized.
