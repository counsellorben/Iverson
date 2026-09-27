// Port of Trino 483 IrRowPatternFlattener and IrPatternAlternationOptimizer (Apache-2.0); see THIRD-PARTY-NOTICES.md.
using Iverson.Patterns.Syntax;

namespace Iverson.Patterns.Compilation;

/// <summary>
/// Optimizes a parsed pattern tree before compilation: removes nested exclusions, flattens alternations and
/// concatenations, removes redundant empty patterns, and replaces an empty alternation branch with a zero-or-one
/// quantification of its neighbour. Recursion depth is bounded by the parsed pattern's depth, which
/// <c>MaxPatternLength</c> (1,000 characters) bounds.
/// </summary>
internal static class PatternOptimizer
{
    public static PatternNode Optimize(PatternNode root) => AlternationOptimizer(Flatten(root, inExclusion: false));

    // --- IrRowPatternFlattener ---

    private static PatternNode Flatten(PatternNode node, bool inExclusion)
    {
        switch (node)
        {
            case LabelNode or AnchorNode or EmptyNode:
                return node;

            case ExclusionNode exclusion:
            {
                var child = Flatten(exclusion.Pattern, true);
                // Skip nested exclusion. This is necessary to resolve exclusions correctly during pattern matching.
                return inExclusion ? child : new ExclusionNode(child);
            }

            case AlternationNode alternation:
                return FlattenAlternation(alternation, inExclusion);

            case ConcatenationNode concatenation:
                return FlattenConcatenation(concatenation, inExclusion);

            case PermutationNode permutation:
                return FlattenPermutation(permutation, inExclusion);

            case QuantifiedNode quantified:
            {
                var child = Flatten(quantified.Pattern, inExclusion);
                return child is EmptyNode ? child : new QuantifiedNode(child, quantified.Min, quantified.Max, quantified.Greedy);
            }

            default:
                throw new NotSupportedException($"unsupported node type: {node.GetType().Name}");
        }
    }

    /// <summary>
    /// Flattens the alternation and removes redundant empty branches: all empty branches following the first
    /// empty branch are unreachable (less preferred than the first empty branch), so they are removed. Returns
    /// the flattened <see cref="AlternationNode"/> containing at most one empty branch, or <see cref="EmptyNode"/>
    /// when the alternation reduces to a single empty branch.
    /// </summary>
    private static PatternNode FlattenAlternation(AlternationNode node, bool inExclusion)
    {
        var children = node.Patterns.Select(p => Flatten(p, inExclusion)).ToList();

        var flattened = new List<PatternNode>();
        foreach (var child in children)
        {
            if (child is AlternationNode nested)
            {
                flattened.AddRange(nested.Patterns);
            }
            else
            {
                flattened.Add(child);
            }
        }

        var firstEmptyChild = flattened.FirstOrDefault(c => c is EmptyNode);
        if (firstEmptyChild is null)
        {
            return new AlternationNode(flattened);
        }

        flattened = flattened.Where(c => c is not EmptyNode || ReferenceEquals(c, firstEmptyChild)).ToList();

        return flattened.Count == 1 ? new EmptyNode() : new AlternationNode(flattened);
    }

    /// <summary>
    /// Flattens the concatenation and removes all empty branches. Returns the flattened
    /// <see cref="ConcatenationNode"/> containing no empty branches, or a single sub-pattern when the
    /// concatenation reduces to one branch, or <see cref="EmptyNode"/> when all sub-patterns are empty.
    /// </summary>
    private static PatternNode FlattenConcatenation(ConcatenationNode node, bool inExclusion)
    {
        var children = node.Patterns.Select(p => Flatten(p, inExclusion)).ToList();

        var flattened = new List<PatternNode>();
        foreach (var child in children)
        {
            if (child is ConcatenationNode nested)
            {
                flattened.AddRange(nested.Patterns);
            }
            else
            {
                flattened.Add(child);
            }
        }

        flattened = flattened.Where(c => c is not EmptyNode).ToList();

        return flattened.Count switch
        {
            0 => new EmptyNode(),
            1 => flattened[0],
            _ => new ConcatenationNode(flattened),
        };
    }

    /// <summary>
    /// Removes all empty branches from the permutation. Returns the <see cref="PermutationNode"/> containing no
    /// empty branches, or a single sub-pattern when the permutation reduces to one branch, or
    /// <see cref="EmptyNode"/> when all sub-patterns are empty.
    /// </summary>
    private static PatternNode FlattenPermutation(PermutationNode node, bool inExclusion)
    {
        var children = node.Patterns.Select(p => Flatten(p, inExclusion)).Where(c => c is not EmptyNode).ToList();

        return children.Count switch
        {
            0 => new EmptyNode(),
            1 => children[0],
            _ => new PermutationNode(children),
        };
    }

    // --- IrPatternAlternationOptimizer ---

    private static PatternNode AlternationOptimizer(PatternNode node) => node switch
    {
        LabelNode or AnchorNode or EmptyNode => node,
        ExclusionNode exclusion => new ExclusionNode(AlternationOptimizer(exclusion.Pattern)),
        AlternationNode alternation => OptimizeAlternation(alternation),
        ConcatenationNode concatenation =>
            new ConcatenationNode(concatenation.Patterns.Select(AlternationOptimizer).ToList()),
        PermutationNode permutation =>
            new PermutationNode(permutation.Patterns.Select(AlternationOptimizer).ToList()),
        QuantifiedNode quantified =>
            new QuantifiedNode(AlternationOptimizer(quantified.Pattern), quantified.Min, quantified.Max, quantified.Greedy),
        _ => throw new NotSupportedException($"unsupported node type: {node.GetType().Name}"),
    };

    /// <summary>
    /// Removes the empty child from the alternation and replaces it with a zero-or-one quantification of its
    /// neighbour: <c>(() | A) -&gt; A??</c>, <c>(() | A | B) -&gt; (A?? | B)</c>, <c>(A | ()) -&gt; A?</c>,
    /// <c>(A | B | () | C) -&gt; (A | B? | C)</c>.
    /// </summary>
    private static PatternNode OptimizeAlternation(AlternationNode node)
    {
        var children = node.Patterns.Select(AlternationOptimizer).ToList();

        var emptyChildIndex = -1;
        for (var i = 0; i < children.Count; i++)
        {
            if (children[i] is EmptyNode)
            {
                if (emptyChildIndex >= 0)
                {
                    throw new InvalidOperationException("run the flattener first to remove redundant empty pattern");
                }

                emptyChildIndex = i;
            }
        }

        if (emptyChildIndex < 0)
        {
            return new AlternationNode(children);
        }

        if (emptyChildIndex == 0)
        {
            var child = new QuantifiedNode(children[1], 0, 1, Greedy: false);
            if (children.Count == 2)
            {
                return child;
            }

            var branches = new List<PatternNode> { child };
            branches.AddRange(children.Skip(2));
            return new AlternationNode(branches);
        }

        var replaced = new List<PatternNode>();
        replaced.AddRange(children.Take(emptyChildIndex - 1));
        replaced.Add(new QuantifiedNode(children[emptyChildIndex - 1], 0, 1, Greedy: true));
        replaced.AddRange(children.Skip(emptyChildIndex + 1));

        return replaced.Count == 1 ? replaced[0] : new AlternationNode(replaced);
    }
}
