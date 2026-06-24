namespace Iverson.Api.Schema;

public sealed record SchemaDescriptor
{
    public required string TypeName       { get; init; }
    public required string TableName      { get; init; }   // SQL
    public required string IndexName      { get; init; }   // Elasticsearch
    public string?         CollectionName { get; init; }   // Qdrant — null if no [IversonEmbedding]

    public required ColumnDescriptor                   KeyColumn     { get; init; }
    public required IReadOnlyList<ColumnDescriptor>    ScalarColumns { get; init; }
    public required IReadOnlyList<ForeignKeyDescriptor> FkColumns    { get; init; }
    public required IReadOnlyList<VectorDescriptor>    VectorFields  { get; init; }
    public required IReadOnlyList<ChunkDescriptor>     ChunkFields   { get; init; }
    public required IReadOnlyList<RelationDescriptor>  Relations     { get; init; }
}

public sealed record ColumnDescriptor(string Name, string SqlType, bool IsNullable);

public sealed record ForeignKeyDescriptor(string ColumnName, string ReferencedTypeName);

public sealed record VectorDescriptor(string PropertyName, int Dimension, string ModelId);

public sealed record ChunkDescriptor(string PropertyName, int MaxTokens, int Overlap, string ModelId, int Dimension);

public sealed record RelationDescriptor(
    string PropertyName,
    RelationKind Kind,
    string RelatedTypeName,
    string ForeignKey);

public enum RelationKind { OneToOne, OneToMany, ManyToOne, ManyToMany }
