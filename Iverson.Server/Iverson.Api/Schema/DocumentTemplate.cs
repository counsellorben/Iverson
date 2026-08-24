namespace Iverson.Api.Schema;

/// <summary>
/// Discriminates the four kinds of <see cref="DocumentSegment"/> a parsed
/// <see cref="DocumentTemplate"/> can contain.
/// </summary>
public enum DocumentSegmentKind { Literal, Scalar, OneHop, Block }

/// <summary>
/// A single flat record with a kind discriminator — not a polymorphic hierarchy. This is
/// serialized onto <see cref="SchemaDescriptor"/> and through the <c>_iverson_schema</c> table
/// via <see cref="SchemaRegistry"/>, whose <c>JsonSerializerOptions</c> configure no
/// polymorphic resolver and no converters. A derived-record hierarchy would serialize lossily
/// and throw <see cref="NotSupportedException"/> on read in <c>LoadAsync</c> at startup.
/// <c>Inner</c> is the same record type, so a block's nested list round-trips without one.
/// Blocks cannot nest, so <c>Inner</c> admits only <see cref="DocumentSegmentKind.Literal"/>
/// and <see cref="DocumentSegmentKind.Scalar"/> segments.
/// </summary>
public sealed record DocumentSegment(
    DocumentSegmentKind Kind,
    string? Text            = null,   // Literal
    string? PropertyName    = null,   // Scalar, OneHop
    string? RelationName    = null,   // OneHop, Block
    IReadOnlyList<DocumentSegment>? Inner = null);  // Block

/// <summary>
/// The parsed form of a type's document template — an ordered list of segments produced by
/// <see cref="DocumentTemplateParser.Parse"/>. Purely structural: it knows nothing about
/// schemas. Semantic validation (does the property exist, does the relation exist) happens
/// elsewhere.
/// </summary>
public sealed record DocumentTemplate(IReadOnlyList<DocumentSegment> Segments);
