// Port of Trino 483 IrRowPatternToProgramRewriter (Apache-2.0); see THIRD-PARTY-NOTICES.md.
using Iverson.Patterns.Syntax;

namespace Iverson.Patterns.Compilation;

/// <summary>
/// Compiles an optimized pattern tree into a flat <see cref="Instruction"/> program, enforcing
/// <c>MaxProgramInstructions</c> on every emission (including the trailing <c>Done</c>).
/// </summary>
internal static class ProgramCompiler
{
    public static Instruction[] Compile(PatternNode root, IReadOnlyDictionary<string, int> labelIndexes, int maxInstructions)
    {
        var optimized = PatternOptimizer.Optimize(root);
        return new Rewriter(labelIndexes, maxInstructions).Rewrite(optimized);
    }

    private sealed class Rewriter(IReadOnlyDictionary<string, int> labelIndexes, int maxInstructions)
    {
        private readonly List<Instruction> _instructions = [];

        public Instruction[] Rewrite(PatternNode root)
        {
            Process(root);
            Append(new Instruction(InstructionKind.Done));
            return [.. _instructions];
        }

        private void Append(Instruction instruction)
        {
            if (_instructions.Count + 1 > maxInstructions)
            {
                throw new PatternValidationException(
                    $"The compiled pattern exceeds MaxProgramInstructions ({maxInstructions} instructions).");
            }

            _instructions.Add(instruction);
        }

        private void Patch(int position, Instruction instruction) => _instructions[position] = instruction;

        private void Process(PatternNode node)
        {
            switch (node)
            {
                case LabelNode label:
                    Append(new Instruction(InstructionKind.MatchLabel, labelIndexes[label.Name]));
                    break;

                case EmptyNode:
                    break;

                case AnchorNode anchor:
                    Append(new Instruction(anchor.PartitionStart ? InstructionKind.MatchStart : InstructionKind.MatchEnd));
                    break;

                case ExclusionNode exclusion:
                    Append(new Instruction(InstructionKind.ExclusionStart));
                    Process(exclusion.Pattern);
                    Append(new Instruction(InstructionKind.ExclusionEnd));
                    break;

                case AlternationNode alternation:
                    Alternation(alternation.Patterns);
                    break;

                case ConcatenationNode concatenation:
                    Concatenation(concatenation.Patterns);
                    break;

                case PermutationNode permutation:
                    PermutationAlternation(permutation.Patterns);
                    break;

                case QuantifiedNode quantified:
                    if (quantified.Max is { } max)
                    {
                        RangeQuantified(quantified.Pattern, quantified.Greedy, quantified.Min, max);
                    }
                    else
                    {
                        LoopingQuantified(quantified.Pattern, quantified.Greedy, quantified.Min);
                    }

                    break;

                default:
                    throw new NotSupportedException($"unsupported node type: {node.GetType().Name}");
            }
        }

        private void Concatenation(IReadOnlyList<PatternNode> patterns)
        {
            foreach (var pattern in patterns)
            {
                Process(pattern);
            }
        }

        private void Alternation(IReadOnlyList<PatternNode> parts)
        {
            var jumpPositions = new List<int>();

            for (var i = 0; i < parts.Count - 1; i++)
            {
                var splitPosition = _instructions.Count;
                Append(default);
                var splitTarget = _instructions.Count;
                Process(parts[i]);
                jumpPositions.Add(_instructions.Count);
                Append(default);
                Patch(splitPosition, new Instruction(InstructionKind.Split, splitTarget, _instructions.Count));
            }

            Process(parts[^1]);

            foreach (var position in jumpPositions)
            {
                Patch(position, new Instruction(InstructionKind.Jump, _instructions.Count));
            }
        }

        /// <summary>
        /// Deviation from Trino: <c>PERMUTE</c> orderings are generated one at a time in lexicographic index
        /// order (next-permutation) rather than materialized up front, so a <c>PERMUTE</c> that exceeds the
        /// cap stops — via <see cref="Append"/> throwing — after at most <c>maxInstructions</c> emissions,
        /// without ever holding more than the current ordering.
        /// </summary>
        private void PermutationAlternation(IReadOnlyList<PatternNode> patterns)
        {
            var indexes = new int[patterns.Count];
            for (var i = 0; i < indexes.Length; i++)
            {
                indexes[i] = i;
            }

            var jumpPositions = new List<int>();
            var hasNext = true;

            while (hasNext)
            {
                var order = (int[])indexes.Clone();
                hasNext = NextPermutation(indexes);

                if (hasNext)
                {
                    var splitPosition = _instructions.Count;
                    Append(default);
                    var splitTarget = _instructions.Count;
                    PermutationBranch(patterns, order);
                    jumpPositions.Add(_instructions.Count);
                    Append(default);
                    Patch(splitPosition, new Instruction(InstructionKind.Split, splitTarget, _instructions.Count));
                }
                else
                {
                    PermutationBranch(patterns, order);
                }
            }

            foreach (var position in jumpPositions)
            {
                Patch(position, new Instruction(InstructionKind.Jump, _instructions.Count));
            }
        }

        private void PermutationBranch(IReadOnlyList<PatternNode> patterns, int[] order)
        {
            foreach (var index in order)
            {
                Process(patterns[index]);
            }
        }

        /// <summary>Mutates <paramref name="values"/> into the next lexicographic permutation; returns false and
        /// leaves it unspecified when <paramref name="values"/> was already the last (descending) permutation.</summary>
        private static bool NextPermutation(int[] values)
        {
            var i = values.Length - 2;
            while (i >= 0 && values[i] >= values[i + 1])
            {
                i--;
            }

            if (i < 0)
            {
                return false;
            }

            var j = values.Length - 1;
            while (values[j] <= values[i])
            {
                j--;
            }

            (values[i], values[j]) = (values[j], values[i]);
            Array.Reverse(values, i + 1, values.Length - i - 1);
            return true;
        }

        private void LoopingQuantified(PatternNode pattern, bool greedy, int min)
        {
            if (min == 0)
            {
                var startSplitPosition = _instructions.Count;
                Append(default);
                var splitTarget = _instructions.Count;
                var loopingPosition = _instructions.Count;

                Process(pattern);
                Loop(loopingPosition, greedy);

                Patch(
                    startSplitPosition,
                    greedy
                        ? new Instruction(InstructionKind.Split, splitTarget, _instructions.Count)
                        : new Instruction(InstructionKind.Split, _instructions.Count, splitTarget));
                return;
            }

            var loop = _instructions.Count;
            for (var i = 0; i < min; i++)
            {
                loop = _instructions.Count;
                Process(pattern);
            }

            Loop(loop, greedy);
        }

        private void Loop(int loopingPosition, bool greedy)
        {
            var split = greedy
                ? new Instruction(InstructionKind.Split, loopingPosition, _instructions.Count + 1)
                : new Instruction(InstructionKind.Split, _instructions.Count + 1, loopingPosition);
            Append(split);
        }

        private void RangeQuantified(PatternNode pattern, bool greedy, int min, int max)
        {
            for (var i = 0; i < min; i++)
            {
                Process(pattern);
            }

            // Handles 0 without adding instructions.
            if (min == max)
            {
                return;
            }

            var splitPositions = new List<int>();
            var splitTargets = new List<int>();

            for (var i = min; i < max; i++)
            {
                splitPositions.Add(_instructions.Count);
                Append(default);
                splitTargets.Add(_instructions.Count);
                Process(pattern);
            }

            for (var i = 0; i < splitPositions.Count; i++)
            {
                Patch(
                    splitPositions[i],
                    greedy
                        ? new Instruction(InstructionKind.Split, splitTargets[i], _instructions.Count)
                        : new Instruction(InstructionKind.Split, _instructions.Count, splitTargets[i]));
            }
        }
    }
}
