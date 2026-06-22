namespace Iverson.Elasticsearch;

public sealed record IndexSchema(
    string IndexName,
    IReadOnlyList<FieldMapping> Fields,
    string? RelationsMetaJson = null);

public sealed record FieldMapping(string Name, EsFieldType FieldType, int? VectorDims = null);

public enum EsFieldType
{
    Text,       // analysed string + .keyword sub-field
    Keyword,    // exact-match only (Guid, FK, enum)
    Integer,
    Long,
    Float,
    Double,
    Boolean,
    Date,
    DenseVector
}
