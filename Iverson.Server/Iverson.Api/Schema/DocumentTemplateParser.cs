using System.Text;
using System.Text.RegularExpressions;

namespace Iverson.Api.Schema;

/// <summary>
/// Thrown by <see cref="DocumentTemplateParser.Parse"/> when a document template is
/// structurally invalid. <see cref="Placeholder"/> carries the offending placeholder text
/// (including its surrounding braces where applicable) so callers can report a precise error.
/// </summary>
public sealed class DocumentTemplateParseException(string message, string placeholder)
    : Exception(message)
{
    public string Placeholder { get; } = placeholder;
}

/// <summary>
/// Parses the document template grammar: <c>{Prop}</c> (scalar), <c>{Rel.Prop}</c> (one-hop),
/// <c>{#Rel}…{/Rel}</c> (block section), and <c>{{</c> (escapes a literal brace). This parser
/// is structural only — it knows nothing about schemas. It does not check that a named
/// property or relation actually exists on a type; that is a later, semantic validation pass.
/// </summary>
public static class DocumentTemplateParser
{
    private static readonly Regex s_identifier = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    public static DocumentTemplate Parse(string template)
    {
        var (segments, _) = ParseSegments(template, 0, template.Length, insideBlock: false);

        if (!segments.Any(s => s.Kind != DocumentSegmentKind.Literal))
            throw new DocumentTemplateParseException(
                "Document template must contain at least one placeholder.", string.Empty);

        return new DocumentTemplate(segments);
    }

    // Parses segments from template[start..end). Returns the parsed segments and the index of
    // the first character not consumed. When insideBlock is true: a block open tag is rejected
    // (blocks cannot nest), dotted placeholders are rejected, and a block close tag ("{/Name}")
    // is left unconsumed so the caller (the enclosing block handler) can match it itself.
    private static (List<DocumentSegment> Segments, int End) ParseSegments(
        string template, int start, int end, bool insideBlock)
    {
        var segments = new List<DocumentSegment>();
        var literal = new StringBuilder();
        var i = start;

        void FlushLiteral()
        {
            if (literal.Length > 0)
            {
                segments.Add(new DocumentSegment(DocumentSegmentKind.Literal, Text: literal.ToString()));
                literal.Clear();
            }
        }

        while (i < end)
        {
            var c = template[i];

            if (c != '{')
            {
                literal.Append(c);
                i++;
                continue;
            }

            // Escaped literal brace: "{{" -> a single literal "{". Scanning resumes normally
            // afterward, so any following "}" is ordinary literal text, not a token closer.
            if (i + 1 < end && template[i + 1] == '{')
            {
                literal.Append('{');
                i += 2;
                continue;
            }

            var close = template.IndexOf('}', i + 1);
            if (close < 0 || close >= end)
            {
                var offending = template[i..end];
                throw new DocumentTemplateParseException(
                    $"Unclosed placeholder: '{offending}'.", offending);
            }

            var content = template[(i + 1)..close];
            var token = template[i..(close + 1)];

            if (content.StartsWith('#'))
            {
                if (insideBlock)
                    throw new DocumentTemplateParseException(
                        $"Block sections cannot nest: '{token}'.", token);

                var relationName = content[1..];
                if (!s_identifier.IsMatch(relationName))
                    throw new DocumentTemplateParseException(
                        $"Unparseable placeholder: '{token}'.", token);

                var (innerSegments, innerEnd) = ParseSegments(template, close + 1, end, insideBlock: true);

                var closedRelation = TryReadCloseTag(template, innerEnd, end, out var afterClose);
                if (closedRelation is null)
                    throw new DocumentTemplateParseException(
                        $"Unclosed block section: '{token}'.", token);

                if (!string.Equals(closedRelation, relationName, StringComparison.Ordinal))
                    throw new DocumentTemplateParseException(
                        $"Mismatched block close tag: expected '{{/{relationName}}}' but found '{{/{closedRelation}}}'.",
                        $"{{/{closedRelation}}}");

                FlushLiteral();
                segments.Add(new DocumentSegment(
                    DocumentSegmentKind.Block, RelationName: relationName, Inner: innerSegments));

                i = afterClose;
                continue;
            }

            if (content.StartsWith('/'))
            {
                if (insideBlock)
                {
                    // Don't consume: this is the enclosing block's close tag, matched by our caller.
                    FlushLiteral();
                    return (segments, i);
                }

                throw new DocumentTemplateParseException(
                    $"Unexpected block close tag: '{token}'.", token);
            }

            var parts = content.Split('.');
            if (parts.Length > 2)
                throw new DocumentTemplateParseException(
                    $"Only one-hop placeholders are supported: '{token}'.", token);

            if (parts.Any(p => !s_identifier.IsMatch(p)))
                throw new DocumentTemplateParseException(
                    $"Unparseable placeholder: '{token}'.", token);

            if (parts.Length == 2)
            {
                if (insideBlock)
                    throw new DocumentTemplateParseException(
                        $"Dotted placeholders are not allowed inside a block: '{token}'.", token);

                FlushLiteral();
                segments.Add(new DocumentSegment(
                    DocumentSegmentKind.OneHop, RelationName: parts[0], PropertyName: parts[1]));
            }
            else
            {
                FlushLiteral();
                segments.Add(new DocumentSegment(DocumentSegmentKind.Scalar, PropertyName: parts[0]));
            }

            i = close + 1;
        }

        FlushLiteral();
        return (segments, i);
    }

    // If [start, end) begins with a "{/Name}" tag, returns "Name" and sets afterClose to the
    // index just past it. Otherwise returns null and leaves afterClose set to start.
    private static string? TryReadCloseTag(string template, int start, int end, out int afterClose)
    {
        afterClose = start;
        if (start >= end || template[start] != '{')
            return null;

        var close = template.IndexOf('}', start + 1);
        if (close < 0 || close > end)
            return null;

        var content = template[(start + 1)..close];
        if (!content.StartsWith('/'))
            return null;

        afterClose = close + 1;
        return content[1..];
    }
}
